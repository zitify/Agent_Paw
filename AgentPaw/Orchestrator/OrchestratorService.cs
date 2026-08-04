using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AgentPaw.Data;
using AgentPaw.Models;
using AgentPaw.Services;

namespace AgentPaw.Orchestrator;

public partial class OrchestratorService
{
    private readonly IDbContextFactory<AgentPawDbContext> _dbFactory;
    private readonly ConfigLoaderService _configLoader;
    private readonly ClassifierService _classifier;
    private readonly ContextInjectorService _contextInjector;
    private readonly AiClientService _aiClient;
    private readonly SelfCriticService _selfCritic;
    private readonly ToolExecutorService _toolExecutor;
    private readonly PmReportService _pmReport;
    private readonly WikiService _wiki;

    private const int MaxIterations = 24;
    private const int MaxHandoffs = 12;
    // 토론 설정 기본값 — 프로젝트별 설정이 없을 때 폴백
    private const int DefaultMaxDiscussionRounds = 10;
    private const int DefaultMaxDiscussionParticipants = 4;

    public OrchestratorService(
        IDbContextFactory<AgentPawDbContext> dbFactory,
        ConfigLoaderService configLoader,
        ClassifierService classifier,
        ContextInjectorService contextInjector,
        AiClientService aiClient,
        SelfCriticService selfCritic,
        ToolExecutorService toolExecutor,
        PmReportService pmReport,
        WikiService wiki)
    {
        _dbFactory = dbFactory;
        _configLoader = configLoader;
        _classifier = classifier;
        _contextInjector = contextInjector;
        _aiClient = aiClient;
        _selfCritic = selfCritic;
        _toolExecutor = toolExecutor;
        _pmReport = pmReport;
        _wiki = wiki;
    }

    public async Task<OrchestratorOutput> RunPipelineAsync(
        OrchestratorInput input,
        IProgress<AgentTurn>? progress = null,
        CancellationToken ct = default)
    {
        if (input.TeamPersonaIds?.Count >= 2)
            return await RunTeamPipelineAsync(input, progress, ct);

        var personas = await _configLoader.ListPersonasAsync(input.ProjectId, ct);
        if (personas.Count == 0)
            throw new InvalidOperationException("프로젝트에 페르소나가 없습니다.");

        // 멀티에이전트: PM + 비PM이 1명 이상이면 자유 토론 모드 (ForcePersonaId 지정 시 단일 에이전트 유지)
        if (input.ForcePersonaId == null
            && personas.Any(p => p.IsPm)
            && personas.Count(p => !p.IsPm) >= 1)
            return await RunFreeDiscussionPipelineAsync(input, personas, progress, ct);

        var workspaceRoot = await ResolveWorkspaceRootAsync(input.ProjectId);
        var pmPersona = personas.FirstOrDefault(p => p.IsPm);
        var askUserEnabled = await ResolveAskUserEnabledAsync(input);
        var (maxDiscussionRounds, maxDiscussionParticipants) = await ResolveDiscussionSettingsAsync(input.ProjectId);
        var runId = Guid.NewGuid().ToString("N")[..8];

        var classification = _classifier.Classify(input.Message, personas, input.ForcePersonaId);

        if (classification.NeedsConfirmation)
        {
            return new OrchestratorOutput
            {
                RunId = runId,
                PersonaId = classification.PersonaId,
                Confidence = classification.Confidence,
                NeedsConfirmation = true,
                Content = "어떤 에이전트에게 요청할까요?"
            };
        }

        var currentPersonaId = classification.PersonaId;
        var currentRequest = input.Message;
        string? fromPersonaLabel = null;
        var history = new List<AgentTurn>();
        var seenHandoffs = new HashSet<string>();
        int handoffCount = 0;
        string? pendingToolFeedback = null;
        string? pendingDiscussionFeedback = null;
        AgentTurn? lastTurn = null;
        bool endReport = false;
        bool userIntervention = false;
        string pmReportSummary = string.Empty;
        string pmReportBody = string.Empty;
        string interventionReason = string.Empty;
        string interventionQuestion = string.Empty;
        int iterationsUsed = 0;

        for (int iter = 0; iter < MaxIterations; iter++)
        {
            iterationsUsed = iter + 1;
            var config = await _configLoader.GetPersonaConfigAsync(currentPersonaId, input.ProjectId, ct);
            var isCurrentPm = pmPersona != null && config.PersonaId == pmPersona.PersonaId;

            var isDevRequest = DetectDevIntent(input.Message);
            var addendum = BuildProtocolAddendum(personas, config.Name, workspaceRoot, isCurrentPm, pmPersona, askUserEnabled, maxDiscussionRounds, maxDiscussionParticipants, isDevRequest);
            var systemPrompt = string.IsNullOrWhiteSpace(addendum)
                ? config.SystemPrompt
                : config.SystemPrompt + "\n\n" + addendum;

            string userPrompt;
            if (iter == 0)
            {
                userPrompt = await _contextInjector.InjectAsync(
                    currentRequest, input.ProjectId, config.Name, classification.Confidence, ct);
            }
            else if (pendingToolFeedback != null)
            {
                userPrompt = BuildToolFeedbackPrompt(input.Message, history, pendingToolFeedback);
                pendingToolFeedback = null;
            }
            else if (pendingDiscussionFeedback != null)
            {
                userPrompt = BuildDiscussionReviewPrompt(input.Message, history, pendingDiscussionFeedback);
                pendingDiscussionFeedback = null;
            }
            else
            {
                userPrompt = BuildTeamContextPrompt(input.Message, history, fromPersonaLabel ?? "팀", currentRequest);
            }

            var streamKey = $"{runId}-{iter}";
            var streamBuffer = new StringBuilder();
            var lastEmitTicks = 0L;
            // 30fps (33ms) 시간 기반 throttle — 토큰이 쌓이는 대로 UI에 즉시 반영하되 재렌더 폭주는 막는다
            const long EmitIntervalTicks = TimeSpan.TicksPerMillisecond * 33;

            void EmitPreview(bool force)
            {
                if (progress == null) return;
                var nowTicks = DateTime.UtcNow.Ticks;
                if (!force && nowTicks - lastEmitTicks < EmitIntervalTicks) return;
                lastEmitTicks = nowTicks;

                progress.Report(new AgentTurn
                {
                    TurnIndex = iter,
                    PersonaId = config.PersonaId,
                    PersonaName = config.Name,
                    PersonaLabel = config.Label,
                    PersonaAvatar = config.Avatar,
                    IsPm = isCurrentPm,
                    Content = CleanStreamingPreview(streamBuffer.ToString()),
                    ModelUsed = string.Empty,
                    StreamKey = streamKey,
                    IsStreamingPreview = true,
                    IsPmGreeting = isCurrentPm && iter == 0
                });
            }

            // 첫 토큰이 도착하기 전에 빈 프리뷰 버블을 띄워 "생성 중" 상태를 즉시 노출한다
            EmitPreview(force: true);

            var response = await _aiClient.ChatWithFallbackStreamAsync(
                config.PrimaryModel,
                config.FallbackModel,
                systemPrompt,
                userPrompt,
                config.Temperature,
                config.MaxTokens,
                onDelta: chunk =>
                {
                    streamBuffer.Append(chunk);
                    EmitPreview(force: false);
                },
                history: iter == 0 ? input.PriorConversation : null,
                ct: ct);

            var toolParse = ToolCallParser.Parse(response.Content);
            var afterTools = toolParse.CleanedContent;

            // PM 전용 블록은 PM 턴에서만 해석한다
            PmBlockResult? pmBlock = null;
            DiscussionOpenResult? discussionOpen = null;
            DiscussionSummaryResult? discussionSummary = null;
            string cleanedForHandoff = afterTools;
            if (isCurrentPm)
            {
                pmBlock = PmBlockParser.Parse(afterTools);
                cleanedForHandoff = pmBlock.CleanedContent;

                discussionOpen = DiscussionBlockParser.ParseOpen(cleanedForHandoff);
                cleanedForHandoff = discussionOpen.CleanedContent;

                discussionSummary = DiscussionBlockParser.ParseSummary(cleanedForHandoff);
                cleanedForHandoff = discussionSummary.CleanedContent;
            }

            // wiki_save 블록은 모든 페르소나가 발신 가능하다 — 의사결정·명세·트러블슈팅을 위키로 승격한다
            var turnEventId = Guid.NewGuid().ToString();
            var wikiParse = WikiSaveParser.Parse(cleanedForHandoff);
            cleanedForHandoff = wikiParse.CleanedContent;
            foreach (var block in wikiParse.Saves)
            {
                try
                {
                    await _wiki.CreateWikiAsync(
                        input.ProjectId, block.Category, block.Title, block.Content, sourceEventId: turnEventId, ct: ct);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WikiSave] FAILED: {ex.GetType().Name}: {ex.Message}");
                }
            }

            var handoff = HandoffParser.Parse(cleanedForHandoff);

            var toolRecords = new List<ToolCallRecord>();
            var writtenFiles = new List<string>();
            if (toolParse.HasCalls)
            {
                foreach (var call in toolParse.Calls)
                {
                    var exec = await _toolExecutor.ExecuteAsync(workspaceRoot, call.Name, call.Args, ct);
                    toolRecords.Add(new ToolCallRecord
                    {
                        Name = call.Name,
                        ArgsSummary = SummarizeArgs(call.Args),
                        Success = exec.Success,
                        Result = exec.Message
                    });

                    if (exec.Success && IsFileWritingTool(call.Name))
                    {
                        var relPath = GetStringArg(call.Args, "path");
                        if (!string.IsNullOrWhiteSpace(relPath))
                            writtenFiles.Add(relPath);
                    }
                }
            }

            // PM이 토론을 개시했으면 handoff 블록을 무시한다(토론 서브루틴이 화자 순서를 제어)
            bool pmOpeningDiscussion = isCurrentPm && discussionOpen?.HasOpen == true;
            if (pmOpeningDiscussion)
            {
                handoff = new HandoffResult { HasHandoff = false, CleanedContent = cleanedForHandoff };
            }

            Persona? nextPersona = null;
            bool autoReturnToPm = false;
            if (!toolParse.HasCalls && handoff.HasHandoff)
            {
                nextPersona = personas.FirstOrDefault(p =>
                    string.Equals(p.Name, handoff.To, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.Label, handoff.To, StringComparison.OrdinalIgnoreCase));

                if (nextPersona == null || nextPersona.PersonaId == currentPersonaId)
                {
                    handoff = new HandoffResult { HasHandoff = false, CleanedContent = handoff.CleanedContent };
                    nextPersona = null;
                }
                else
                {
                    var key = $"{nextPersona.PersonaId}::{handoff.Request.Trim()}";
                    if (!seenHandoffs.Add(key) || handoffCount >= MaxHandoffs)
                    {
                        handoff = new HandoffResult { HasHandoff = false, CleanedContent = handoff.CleanedContent };
                        nextPersona = null;
                    }
                }
            }

            // PM 허브 자동 복귀: 비PM 페르소나가 handoff·도구 호출을 끝내고도 pm_report/intervention이 없으면 PM에게 복귀
            if (!toolParse.HasCalls && nextPersona == null && !isCurrentPm && pmPersona != null
                && handoffCount < MaxHandoffs)
            {
                nextPersona = pmPersona;
                autoReturnToPm = true;
            }

            var turnContent = toolParse.HasCalls
                ? toolParse.CleanedContent
                : (isCurrentPm ? cleanedForHandoff : handoff.CleanedContent);
            bool pmHasSpecialBlock = isCurrentPm && (
                pmBlock?.HasReport == true
                || pmBlock?.HasIntervention == true
                || discussionOpen?.HasOpen == true
                || discussionSummary?.HasSummary == true);
            if (string.IsNullOrWhiteSpace(turnContent) && toolRecords.Count == 0 && nextPersona == null
                && !pmHasSpecialBlock)
            {
                turnContent = "(응답 없음)";
            }

            var turn = new AgentTurn
            {
                EventId = turnEventId,
                TurnIndex = iter,
                PersonaId = config.PersonaId,
                PersonaName = config.Name,
                PersonaLabel = config.Label,
                PersonaAvatar = config.Avatar,
                IsPm = isCurrentPm,
                Content = turnContent,
                ModelUsed = response.ModelUsed,
                HandoffToLabel = nextPersona?.Label,
                HandoffToName = nextPersona?.Name,
                HandoffRequest = (nextPersona != null && !autoReturnToPm) ? handoff.Request : null,
                ToolCalls = toolRecords,
                WrittenFiles = writtenFiles,
                IsEndReport = isCurrentPm && pmBlock?.HasReport == true,
                IsUserIntervention = isCurrentPm && pmBlock?.HasIntervention == true,
                IsPmGreeting = isCurrentPm && iter == 0,
                IsDiscussionOpener = pmOpeningDiscussion,
                IsDiscussionSummary = isCurrentPm && discussionSummary?.HasSummary == true,
                DiscussionTopic = pmOpeningDiscussion ? discussionOpen?.Topic : null,
                StreamKey = streamKey,
                IsStreamingPreview = false
            };

            history.Add(turn);
            lastTurn = turn;
            progress?.Report(turn);

            if (toolParse.HasCalls)
            {
                pendingToolFeedback = BuildToolResultsText(toolRecords);
                continue;
            }

            // PM이 종료 보고 또는 User 개입 요청을 발신했으면 즉시 루프 종료
            if (isCurrentPm && pmBlock != null)
            {
                if (pmBlock.HasReport)
                {
                    endReport = true;
                    pmReportSummary = pmBlock.ReportSummary;
                    pmReportBody = pmBlock.ReportBody;
                    break;
                }
                if (pmBlock.HasIntervention)
                {
                    userIntervention = true;
                    interventionReason = pmBlock.InterventionReason;
                    interventionQuestion = pmBlock.InterventionQuestion;
                    break;
                }
            }

            // PM이 다자 토론을 개시했으면 인라인으로 실행하고 전사를 PM 다음 턴의 입력으로 넘긴다
            if (pmOpeningDiscussion && discussionOpen != null && pmPersona != null)
            {
                var validated = ValidateDiscussionParticipants(discussionOpen.Participants, personas, pmPersona, maxDiscussionParticipants);
                if (validated.Count >= 2)
                {
                    var rounds = Math.Clamp(discussionOpen.Rounds, 1, maxDiscussionRounds);
                    var transcript = await RunDiscussionAsync(
                        input, personas, pmPersona, askUserEnabled,
                        discussionOpen.Topic, discussionOpen.StanceHint,
                        validated, rounds, history, progress, runId,
                        maxParticipants: maxDiscussionParticipants,
                        ct: ct);
                    pendingDiscussionFeedback = transcript;
                    currentPersonaId = pmPersona.PersonaId;
                    fromPersonaLabel = "다자 토론";
                    // 토론 도중 iter 상한에 걸려 PM 검토 턴이 누락되더라도 OrchestratorOutput이 최신 발언을 반영하도록 갱신
                    if (history.Count > 0) lastTurn = history[^1];
                    continue;
                }
                // 참여자 2인 미만이면 토론 무효화 → PM에게 다시 위임 결정 요청
                pendingDiscussionFeedback = "⚠ 참여자가 2인 이상 유효하지 않아 토론이 개시되지 않았다. 다른 방법(handoff 등)으로 진행한다.";
                currentPersonaId = pmPersona.PersonaId;
                fromPersonaLabel = "시스템";
                continue;
            }

            if (nextPersona == null)
                break;

            if (!autoReturnToPm)
                handoffCount++;

            currentPersonaId = nextPersona.PersonaId;
            currentRequest = autoReturnToPm
                ? BuildAutoReturnRequest(config.Label, turnContent, writtenFiles)
                : handoff.Request;
            fromPersonaLabel = config.Label;
        }

        // 루프가 상한에 막힌 경우 시스템 메시지 추가
        if (iterationsUsed >= MaxIterations && lastTurn != null
            && (lastTurn.ToolCalls.Count > 0 || lastTurn.HandoffToName != null))
        {
            var limitTurn = new AgentTurn
            {
                TurnIndex = history.Count,
                PersonaId = lastTurn.PersonaId,
                PersonaLabel = "시스템",
                PersonaAvatar = string.Empty,
                Content = $"⚠ 반복 상한({MaxIterations}회)에 도달해 작업을 중단합니다. 필요하면 다시 요청하세요.",
                ModelUsed = string.Empty
            };
            history.Add(limitTurn);
            lastTurn = limitTurn;
            progress?.Report(limitTurn);
        }

        if (lastTurn == null)
            throw new InvalidOperationException("페르소나 응답을 생성하지 못했습니다.");

        // 종료 보고 시 산출물 취합·Git 커밋
        string? outputsFolder = null;
        string? reportPath = null;
        string? commitSha = null;
        if (endReport)
        {
            try
            {
                var ctx = new PmReportContext
                {
                    WorkspaceRoot = workspaceRoot,
                    RunId = runId,
                    OriginalUserMessage = input.Message,
                    ReportSummary = pmReportSummary,
                    ReportBody = pmReportBody,
                    Turns = history
                        .Where(t => t.PersonaId != "시스템" && !string.IsNullOrWhiteSpace(t.PersonaId))
                        .Select(t => new TurnOutputRecord
                        {
                            TurnIndex = t.TurnIndex,
                            PersonaName = t.PersonaName,
                            PersonaLabel = t.PersonaLabel,
                            Content = t.Content,
                            ModelUsed = t.ModelUsed,
                            WrittenFiles = t.WrittenFiles
                        })
                        .ToList()
                };
                var aggregated = _pmReport.Aggregate(ctx);
                outputsFolder = aggregated.OutputsFolder;
                reportPath = aggregated.ReportPath;
                commitSha = aggregated.CommitSha;
            }
            catch
            {
                // 취합 실패는 보고 자체를 막지 않는다. 추후 재시도 큐로 분리 가능.
            }
        }

        var eventId = await LogEventsAsync(input, history, runId, endReport, userIntervention, outputsFolder, commitSha, reportPath, ct);

        return new OrchestratorOutput
        {
            EventId = eventId,
            RunId = runId,
            PersonaId = lastTurn.PersonaId,
            PersonaLabel = lastTurn.PersonaLabel,
            PersonaAvatar = lastTurn.PersonaAvatar,
            Content = lastTurn.Content,
            ModelUsed = lastTurn.ModelUsed,
            Verified = true,
            Confidence = classification.Confidence,
            NeedsConfirmation = false,
            Turns = history,
            IsEndReport = endReport,
            IsUserIntervention = userIntervention,
            OutputsFolder = outputsFolder,
            ReportPath = reportPath,
            CommitSha = commitSha,
            InterventionReason = userIntervention ? interventionReason : null,
            InterventionQuestion = userIntervention ? interventionQuestion : null
        };
    }

    // === 다자 토론(round-table) ===

    private static List<Persona> ValidateDiscussionParticipants(
        List<string> names, List<Persona> allPersonas, Persona pm,
        int maxParticipants = DefaultMaxDiscussionParticipants)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Persona>();
        foreach (var raw in names)
        {
            var n = raw?.Trim();
            if (string.IsNullOrEmpty(n)) continue;
            var match = allPersonas.FirstOrDefault(p =>
                string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Label, n, StringComparison.OrdinalIgnoreCase));
            if (match == null) continue;
            if (match.PersonaId == pm.PersonaId) continue;  // PM은 참여자에서 제외
            if (!seen.Add(match.PersonaId)) continue;
            result.Add(match);
            if (result.Count >= maxParticipants) break;
        }
        return result;
    }

    private async Task<string> RunDiscussionAsync(
        OrchestratorInput input,
        List<Persona> allPersonas,
        Persona pm,
        bool askUserEnabled,
        string topic,
        string stanceHint,
        List<Persona> participants,
        int rounds,
        List<AgentTurn> history,
        IProgress<AgentTurn>? progress,
        string runId,
        int maxParticipants = DefaultMaxDiscussionParticipants,
        bool isFreeMode = false,
        CancellationToken ct = default)
    {
        var discussionId = Guid.NewGuid().ToString("N")[..8];
        int speakerCounter = 0;
        // 합의(전원 agree)가 주 종료 조건. maxTurns는 무한 루프 방지 안전 캡 (명목 턴수 = rounds × participants).
        int maxTurns = rounds * maxParticipants;

        // 각 참여자가 마지막으로 발언한 턴 인덱스 (-1 = 미발언)
        var lastSpoke = participants.ToDictionary(p => p.PersonaId, _ => -1);
        var currentSpeaker = participants[0];
        // 조기 종료는 참여자별 최신 stance가 모두 agree일 때만 허용한다.
        var latestStance = new Dictionary<string, string>();
        string? repeatedSelfSpeakerId = null;
        int repeatedSelfSpeakerCount = 0;

        for (int turn = 0; turn < maxTurns; turn++)
        {
            var speaker = currentSpeaker;
            var roundIndex = turn / participants.Count;
            var config = await _configLoader.GetPersonaConfigAsync(speaker.PersonaId, input.ProjectId, ct);
            var addendum = BuildDiscussionSpeakerAddendum(
                topic, stanceHint, participants, config.Name, roundIndex, rounds, isFreeMode);
            var systemPrompt = string.IsNullOrWhiteSpace(addendum)
                ? config.SystemPrompt
                : config.SystemPrompt + "\n\n" + addendum;

            var userPrompt = BuildDiscussionSpeakerPrompt(
                input.Message, topic, history, discussionId);

            var streamKey = $"{runId}-d-{discussionId}-t{turn}";
            var streamBuffer = new StringBuilder();
            long lastEmitTicks = 0L;
            const long EmitIntervalTicks = TimeSpan.TicksPerMillisecond * 33;

            void EmitPreview(bool force)
            {
                if (progress == null) return;
                var nowTicks = DateTime.UtcNow.Ticks;
                if (!force && nowTicks - lastEmitTicks < EmitIntervalTicks) return;
                lastEmitTicks = nowTicks;

                progress.Report(new AgentTurn
                {
                    TurnIndex = history.Count,
                    PersonaId = config.PersonaId,
                    PersonaName = config.Name,
                    PersonaLabel = config.Label,
                    PersonaAvatar = config.Avatar,
                    IsPm = false,
                    Content = CleanStreamingPreview(streamBuffer.ToString()),
                    ModelUsed = string.Empty,
                    StreamKey = streamKey,
                    IsStreamingPreview = true,
                    DiscussionId = discussionId,
                    RoundIndex = roundIndex,
                    SpeakerOrder = speakerCounter,
                    IsDiscussionSpeaker = true,
                    DiscussionTopic = topic
                });
            }

            // 토론 화자도 동일 — 첫 토큰 전 빈 버블을 먼저 띄운다
            EmitPreview(force: true);

            var response = await _aiClient.ChatWithFallbackStreamAsync(
                config.PrimaryModel,
                config.FallbackModel,
                systemPrompt,
                userPrompt,
                config.Temperature,
                config.MaxTokens,
                onDelta: chunk =>
                {
                    streamBuffer.Append(chunk);
                    EmitPreview(force: false);
                },
                history: turn == 0 ? input.PriorConversation : null,
                ct: ct);

            var stance = DiscussionBlockParser.ParseStance(response.Content);
            var afterStance = stance.CleanedContent;

            // 토론 중에도 wiki_save 블록을 발신할 수 있다 — 쟁점 정리나 합의 사항을 즉시 위키로 승격한다
            var dEventId = Guid.NewGuid().ToString();
            var dWikiParse = WikiSaveParser.Parse(afterStance);
            afterStance = dWikiParse.CleanedContent;
            foreach (var block in dWikiParse.Saves)
            {
                try
                {
                    await _wiki.CreateWikiAsync(
                        input.ProjectId, block.Category, block.Title, block.Content, sourceEventId: dEventId, ct: ct);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WikiSave] FAILED: {ex.GetType().Name}: {ex.Message}");
                }
            }

            var content = afterStance;
            if (string.IsNullOrWhiteSpace(content)) content = "(응답 없음)";

            var agentTurn = new AgentTurn
            {
                EventId = dEventId,
                TurnIndex = history.Count,
                PersonaId = config.PersonaId,
                PersonaName = config.Name,
                PersonaLabel = config.Label,
                PersonaAvatar = config.Avatar,
                IsPm = false,
                Content = content,
                ModelUsed = response.ModelUsed,
                DiscussionId = discussionId,
                RoundIndex = roundIndex,
                SpeakerOrder = speakerCounter++,
                Stance = stance.HasStance ? stance.Position : "extend",
                IsDiscussionSpeaker = true,
                DiscussionTopic = topic,
                StreamKey = streamKey,
                IsStreamingPreview = false
            };
            history.Add(agentTurn);
            progress?.Report(agentTurn);
            lastSpoke[speaker.PersonaId] = turn;

            // 참여자 전원이 최소 1회 발언했고, 각자의 최신 stance가 모두 agree면 합의 완료
            latestStance[speaker.PersonaId] = agentTurn.Stance!;
            if (participants.All(p => latestStance.TryGetValue(p.PersonaId, out var s) && s == "agree"))
                break;

            // 다음 화자 결정: 현재 화자가 next_speaker 지명 → 없으면 가장 오래 발언 안 한 참여자
            Persona? next = null;
            if (stance.HasStance && !string.IsNullOrWhiteSpace(stance.NextSpeaker))
            {
                next = participants.FirstOrDefault(p =>
                    string.Equals(p.Name, stance.NextSpeaker, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.Label, stance.NextSpeaker, StringComparison.OrdinalIgnoreCase));
                if (next?.PersonaId == speaker.PersonaId)
                {
                    repeatedSelfSpeakerCount = repeatedSelfSpeakerId == speaker.PersonaId
                        ? repeatedSelfSpeakerCount + 1
                        : 1;
                    repeatedSelfSpeakerId = speaker.PersonaId;
                    if (repeatedSelfSpeakerCount >= 2)
                        next = null;
                }
                else
                {
                    repeatedSelfSpeakerId = null;
                    repeatedSelfSpeakerCount = 0;
                }
            }
            else
            {
                repeatedSelfSpeakerId = null;
                repeatedSelfSpeakerCount = 0;
            }
            if (next == null)
                next = participants.OrderBy(p => lastSpoke[p.PersonaId]).First();
            currentSpeaker = next;
        }

        return RenderDiscussionTranscript(history, discussionId, topic);
    }
}
