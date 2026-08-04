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
    private static string BuildDiscussionSpeakerAddendum(
        string topic, string stanceHint, List<Persona> participants,
        string currentName, int round, int totalRounds, bool isFreeMode = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine(isFreeMode
            ? "[자유 토론 모드 — 너는 팀원이다. 사용자 요청에 직접 응답하면서 동료 발언에도 반응하라]"
            : "[다자 토론 모드 — 너는 라운드 테이블 참여자다]");
        sb.AppendLine(isFreeMode ? $"사용자 요청: {topic}" : $"토론 주제: {topic}");
        if (!string.IsNullOrWhiteSpace(stanceHint))
            sb.AppendLine($"PM의 발언 가이드: {stanceHint}");
        sb.AppendLine($"진행 상황: 라운드 {round + 1}/{totalRounds}");
        sb.AppendLine("참여자: " + string.Join(", ",
            participants.Select(p => $"{p.Name}({p.Label})" + (string.Equals(p.Name, currentName, StringComparison.OrdinalIgnoreCase) ? " ← 너" : ""))));
        sb.AppendLine();
        sb.AppendLine("발언 규칙:");
        sb.AppendLine("  - 네 전문 관점에서 주제에 대한 입장을 제시한다.");
        sb.AppendLine("  - 직전 발언자들의 주장을 명시적으로 동의·반박·보완한다. (누구의 어떤 주장에 대한 것인지 밝힌다.)");
        sb.AppendLine("  - 근거 없는 동조는 금지. 네 역할의 관점에서 구체적 근거를 댄다.");
        sb.AppendLine("  - handoff·tool·pm_report·pm_intervention 블록은 사용 금지. 본문 끝에 stance 블록만 남긴다.");
        sb.AppendLine();
        sb.AppendLine("응답 형식:");
        sb.AppendLine("  <본문: 너의 주장·근거·동료 발언에 대한 반응>");
        sb.AppendLine("```stance");
        sb.AppendLine("{\"position\": \"agree|object|extend\", \"argument\": \"<한 줄 요지>\", \"next_speaker\": \"<다음에 발언할 팀원 이름 — 생략 시 자동 선택>\"}");
        sb.AppendLine("```");
        sb.AppendLine("- agree: 직전 합의안에 이견 없음");
        sb.AppendLine("- object: 명시적 반대 (반대 근거는 본문에)");
        sb.AppendLine("- extend: 보완·추가 조건 제시");
        sb.AppendLine("- next_speaker: 핑퐁 가능 — 같은 팀원을 다시 지명하거나 연속 발언도 허용됨");
        return sb.ToString().TrimEnd();
    }

    private static string BuildDiscussionSpeakerPrompt(
        string originalUserMessage, string topic, List<AgentTurn> history, string discussionId)
    {
        // 프로젝트 이력은 이번 토론 외 발언만 — 토론 발언은 별도 섹션으로 분리해 중복 컨텍스트를 줄인다
        var priorTurns = history
            .Where(t => !string.IsNullOrWhiteSpace(t.PersonaLabel) && t.DiscussionId != discussionId)
            .ToList();
        var historyText = RenderHistory(priorTurns);
        var discussionSoFar = history
            .Where(t => t.DiscussionId == discussionId)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("[원본 사용자 요청]");
        sb.AppendLine(originalUserMessage);
        sb.AppendLine();
        sb.AppendLine("[토론 주제]");
        sb.AppendLine(topic);
        sb.AppendLine();
        sb.AppendLine("[프로젝트 대화 이력]");
        sb.AppendLine(string.IsNullOrWhiteSpace(historyText) ? "(이전 대화 없음)" : historyText);
        sb.AppendLine();
        sb.AppendLine("[이번 토론의 지금까지 발언]");
        if (discussionSoFar.Count == 0)
        {
            sb.AppendLine("(네가 첫 발언자다.)");
        }
        else
        {
            foreach (var t in discussionSoFar)
            {
                sb.AppendLine($"- [{t.PersonaLabel}] (R{(t.RoundIndex ?? 0) + 1}, stance={t.Stance ?? "?"})");
                sb.AppendLine((string.IsNullOrWhiteSpace(t.Content) ? "(내용 없음)" : t.Content));
                sb.AppendLine();
            }
        }
        sb.AppendLine("이제 네 차례다. 위 규칙에 따라 발언하라.");
        return sb.ToString().TrimEnd();
    }

    private static string BuildDiscussionReviewPrompt(
        string originalUserMessage, List<AgentTurn> history, string transcript)
    {
        var priorText = RenderHistory(history.Where(t => t.DiscussionId == null).ToList());
        return
            "[컨텍스트]\n" +
            "사용자 원본 요청:\n" +
            originalUserMessage + "\n\n" +
            "이전 대화:\n" +
            (string.IsNullOrWhiteSpace(priorText) ? "(없음)" : priorText) + "\n\n" +
            "[방금 네가 개시한 다자 토론 전사]\n" +
            transcript + "\n\n" +
            "이 결과를 검토해 discussion_summary 블록으로 합의·잔여 쟁점·다음 단계를 정리하고, " +
            "이어서 handoff / pm_report / pm_intervention 중 하나로 다음 행동을 지정한다. " +
            "단, 같은 주제로 토론을 즉시 재개시(discussion)하지 않는다.";
    }

    private static string RenderDiscussionTranscript(
        List<AgentTurn> history, string discussionId, string topic)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"토론 주제: {topic}");
        sb.AppendLine();
        int? lastRound = null;
        foreach (var t in history.Where(t => t.DiscussionId == discussionId))
        {
            if (t.RoundIndex != lastRound)
            {
                sb.AppendLine($"── 라운드 {(t.RoundIndex ?? 0) + 1} ──");
                lastRound = t.RoundIndex;
            }
            sb.AppendLine($"[{t.PersonaLabel}] (stance: {t.Stance ?? "?"})");
            sb.AppendLine(string.IsNullOrWhiteSpace(t.Content) ? "(내용 없음)" : t.Content);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // === 자유 토론 파이프라인 (PM + 비PM 혼합 프로젝트 기본 동작) ===

    private async Task<OrchestratorOutput> RunFreeDiscussionPipelineAsync(
        OrchestratorInput input,
        List<Persona> personas,
        IProgress<AgentTurn>? progress,
        CancellationToken ct = default)
    {
        var workspaceRoot = await ResolveWorkspaceRootAsync(input.ProjectId);
        var askUserEnabled = await ResolveAskUserEnabledAsync(input);
        var (maxDiscussionRounds, maxDiscussionParticipants) = await ResolveDiscussionSettingsAsync(input.ProjectId);
        var runId = Guid.NewGuid().ToString("N")[..8];

        var pmPersona = personas.First(p => p.IsPm);
        var speakers = personas.Where(p => !p.IsPm).ToList();

        var history = new List<AgentTurn>();

        // Phase 1: 비PM 팀원들이 자유 토론
        var transcript = await RunDiscussionAsync(
            input, personas, pmPersona, askUserEnabled,
            topic: input.Message,
            stanceHint: string.Empty,
            participants: speakers,
            rounds: maxDiscussionRounds,
            history, progress, runId,
            maxParticipants: maxDiscussionParticipants,
            isFreeMode: true,
            ct: ct);

        // Phase 2: PM 한 번 — 취합만
        var pmConfig = await _configLoader.GetPersonaConfigAsync(pmPersona.PersonaId, input.ProjectId, ct);
        var pmSystemPrompt = pmConfig.SystemPrompt + "\n\n" + BuildPmAggregateAddendum();
        var pmUserPrompt = BuildPmAggregatePrompt(input.Message, transcript);

        var streamKey = $"{runId}-pm-agg";
        var streamBuffer = new StringBuilder();
        var lastEmitTicks = 0L;
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
                PersonaId = pmConfig.PersonaId,
                PersonaName = pmConfig.Name,
                PersonaLabel = pmConfig.Label,
                PersonaAvatar = pmConfig.Avatar,
                IsPm = true,
                Content = CleanStreamingPreview(streamBuffer.ToString()),
                ModelUsed = string.Empty,
                StreamKey = streamKey,
                IsStreamingPreview = true
            });
        }

        EmitPreview(force: true);

        var pmResponse = await _aiClient.ChatWithFallbackStreamAsync(
            pmConfig.PrimaryModel, pmConfig.FallbackModel,
            pmSystemPrompt, pmUserPrompt,
            pmConfig.Temperature, pmConfig.MaxTokens,
            onDelta: chunk => { streamBuffer.Append(chunk); EmitPreview(false); },
            history: input.PriorConversation,
            ct: ct);

        var pmBlock = PmBlockParser.Parse(pmResponse.Content);
        var pmEventId = Guid.NewGuid().ToString();
        var wikiParse = WikiSaveParser.Parse(pmBlock.CleanedContent);
        var pmContent = wikiParse.CleanedContent;
        if (string.IsNullOrWhiteSpace(pmContent)) pmContent = "(응답 없음)";

        foreach (var block in wikiParse.Saves)
        {
            try { await _wiki.CreateWikiAsync(input.ProjectId, block.Category, block.Title, block.Content, sourceEventId: pmEventId, ct: ct); }
            catch (Exception ex) { Console.Error.WriteLine($"[WikiSave] FAILED: {ex.GetType().Name}: {ex.Message}"); }
        }

        bool endReport = pmBlock.HasReport;
        var pmTurn = new AgentTurn
        {
            EventId = pmEventId,
            TurnIndex = history.Count,
            PersonaId = pmConfig.PersonaId,
            PersonaName = pmConfig.Name,
            PersonaLabel = pmConfig.Label,
            PersonaAvatar = pmConfig.Avatar,
            IsPm = true,
            Content = pmContent,
            ModelUsed = pmResponse.ModelUsed,
            IsEndReport = endReport,
            StreamKey = streamKey,
            IsStreamingPreview = false
        };
        history.Add(pmTurn);
        progress?.Report(pmTurn);

        string? outputsFolder = null, reportPath = null, commitSha = null;
        if (endReport)
        {
            try
            {
                var ctx = new PmReportContext
                {
                    WorkspaceRoot = workspaceRoot,
                    RunId = runId,
                    OriginalUserMessage = input.Message,
                    ReportSummary = pmBlock.ReportSummary,
                    ReportBody = pmBlock.ReportBody,
                    Turns = history
                        .Where(t => !string.IsNullOrWhiteSpace(t.PersonaId))
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
            catch { }
        }

        var eventId = await LogEventsAsync(input, history, runId, endReport, false, outputsFolder, commitSha, reportPath, ct);

        return new OrchestratorOutput
        {
            EventId = eventId,
            RunId = runId,
            PersonaId = pmTurn.PersonaId,
            PersonaLabel = pmTurn.PersonaLabel,
            PersonaAvatar = pmTurn.PersonaAvatar,
            Content = pmTurn.Content,
            ModelUsed = pmTurn.ModelUsed,
            Verified = true,
            Turns = history,
            IsEndReport = endReport,
            OutputsFolder = outputsFolder,
            ReportPath = reportPath,
            CommitSha = commitSha
        };
    }

    private static string BuildPmAggregateAddendum()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[취합 모드]");
        sb.AppendLine("팀원들이 자유 토론을 마쳤다. 너는 결과를 취합·정리하는 역할만 한다.");
        sb.AppendLine("규칙:");
        sb.AppendLine("  - handoff·discussion 블록 사용 금지.");
        sb.AppendLine("  - 각 팀원의 주장을 중립적으로 요약한다.");
        sb.AppendLine("  - 합의 사항, 잔여 쟁점, 권고 사항을 구분해서 제시한다.");
        sb.AppendLine("  - 산출물이 있으면 pm_report 블록으로 종료. 추가 확인이 필요하면 pm_intervention 블록 사용.");
        return sb.ToString().TrimEnd();
    }

    private static string BuildPmAggregatePrompt(string userMessage, string transcript)
    {
        return
            "[사용자 요청]\n" +
            userMessage + "\n\n" +
            "[팀 토론 전사]\n" +
            transcript + "\n\n" +
            "위 토론 결과를 취합하여 사용자에게 응답하라.";
    }

    // === 팀 파이프라인 (멀티에이전트) ===

    private async Task<OrchestratorOutput> RunTeamPipelineAsync(
        OrchestratorInput input,
        IProgress<AgentTurn>? progress,
        CancellationToken ct = default)
    {
        var allPersonas = await _configLoader.ListPersonasAsync(input.ProjectId, ct);
        var workspaceRoot = await ResolveWorkspaceRootAsync(input.ProjectId);
        var askUserEnabled = await ResolveAskUserEnabledAsync(input);
        var runId = Guid.NewGuid().ToString("N")[..8];
        var mode = (input.TeamMode ?? "panel").ToLowerInvariant();

        var teamPersonas = input.TeamPersonaIds!
            .Select(id => allPersonas.FirstOrDefault(p => p.PersonaId == id))
            .Where(p => p != null)
            .Cast<Persona>()
            .ToList();

        if (teamPersonas.Count < 2)
            throw new InvalidOperationException("팀 모드에는 최소 2개의 페르소나가 필요합니다.");

        var history = new List<AgentTurn>();

        switch (mode)
        {
            case "debate":
            {
                var topic = input.Message.Length > 120 ? input.Message[..120] + "…" : input.Message;
                var (dMaxRounds, dMaxParticipants) = await ResolveDiscussionSettingsAsync(input.ProjectId);
                await RunDiscussionAsync(
                    input, allPersonas, teamPersonas[0], askUserEnabled,
                    topic, string.Empty,
                    teamPersonas, dMaxRounds, history, progress, runId,
                    maxParticipants: dMaxParticipants,
                    ct: ct);
                break;
            }
            case "chain":
                await RunChainAsync(input, teamPersonas, workspaceRoot, history, progress, runId, ct);
                break;
            default:
                await RunPanelAsync(input, teamPersonas, workspaceRoot, history, progress, runId, ct);
                break;
        }

        if (history.Count == 0)
            throw new InvalidOperationException("팀 파이프라인이 응답을 생성하지 못했습니다.");

        var eventId = await LogEventsAsync(input, history, runId, false, false, null, null, null, ct);
        var lastTurn = history[^1];

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
            Turns = history
        };
    }

    private async Task RunPanelAsync(
        OrchestratorInput input,
        List<Persona> teamPersonas,
        string workspaceRoot,
        List<AgentTurn> history,
        IProgress<AgentTurn>? progress,
        string runId,
        CancellationToken ct = default)
    {
        for (int i = 0; i < teamPersonas.Count; i++)
        {
            var persona = teamPersonas[i];
            var config = await _configLoader.GetPersonaConfigAsync(persona.PersonaId, input.ProjectId, ct);
            var streamKey = $"{runId}-panel-{i}";

            var addendum = BuildTeamModeAddendum(teamPersonas, config.Name, workspaceRoot, "panel");
            var systemPrompt = string.IsNullOrWhiteSpace(addendum)
                ? config.SystemPrompt
                : config.SystemPrompt + "\n\n" + addendum;

            var userPrompt = await _contextInjector.InjectAsync(
                input.Message, input.ProjectId, config.Name, 1.0, ct);

            var streamBuffer = new StringBuilder();
            long lastEmitTicks = 0L;
            const long EmitIntervalTicks = TimeSpan.TicksPerMillisecond * 33;
            var turnIdx = i;

            void EmitPreview(bool force)
            {
                if (progress == null) return;
                var nowTicks = DateTime.UtcNow.Ticks;
                if (!force && nowTicks - lastEmitTicks < EmitIntervalTicks) return;
                lastEmitTicks = nowTicks;
                progress.Report(new AgentTurn
                {
                    TurnIndex = turnIdx,
                    PersonaId = config.PersonaId,
                    PersonaName = config.Name,
                    PersonaLabel = config.Label,
                    PersonaAvatar = config.Avatar,
                    Content = CleanStreamingPreview(streamBuffer.ToString()),
                    StreamKey = streamKey,
                    IsStreamingPreview = true
                });
            }

            EmitPreview(force: true);

            var response = await _aiClient.ChatWithFallbackStreamAsync(
                config.PrimaryModel, config.FallbackModel,
                systemPrompt, userPrompt,
                config.Temperature, config.MaxTokens,
                onDelta: chunk => { streamBuffer.Append(chunk); EmitPreview(force: false); },
                history: input.PriorConversation,
                ct: ct);

            var eventId = Guid.NewGuid().ToString();
            var wikiParse = WikiSaveParser.Parse(response.Content);
            var content = wikiParse.CleanedContent;
            foreach (var block in wikiParse.Saves)
            {
                try { await _wiki.CreateWikiAsync(input.ProjectId, block.Category, block.Title, block.Content, sourceEventId: eventId, ct: ct); }
                catch { }
            }

            var turn = new AgentTurn
            {
                EventId = eventId,
                TurnIndex = i,
                PersonaId = config.PersonaId,
                PersonaName = config.Name,
                PersonaLabel = config.Label,
                PersonaAvatar = config.Avatar,
                Content = string.IsNullOrWhiteSpace(content) ? "(응답 없음)" : content,
                ModelUsed = response.ModelUsed,
                StreamKey = streamKey,
                IsStreamingPreview = false
            };
            history.Add(turn);
            progress?.Report(turn);
        }
    }

}
