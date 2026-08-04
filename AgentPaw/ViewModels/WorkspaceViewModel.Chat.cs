using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using AgentPaw.Models;
using AgentPaw.Orchestrator;

namespace AgentPaw.ViewModels;

public partial class WorkspaceViewModel : ObservableObject
{
    private async Task LoadHistoryAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var events = await db.EventLogs
            .AsNoTracking()
            .Where(e => e.ProjectId == ProjectId && !e.IsDeleted)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync();

        // 이벤트마다 선형 탐색하지 않도록 PersonaId → Persona 인덱스를 1회만 구축한다
        var personaById = Personas
            .Where(p => !string.IsNullOrEmpty(p.PersonaId))
            .GroupBy(p => p.PersonaId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var personaByName = Personas
            .Where(p => !string.IsNullOrEmpty(p.Name))
            .GroupBy(p => p.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var evt in events)
        {
            try
            {
                if (evt.EventType == "USER_MESSAGE")
                {
                    var payload = JsonSerializer.Deserialize<JsonElement>(evt.Payload);
                    var message = payload.GetProperty("message").GetString() ?? string.Empty;
                    Messages.Add(new ChatMessage
                    {
                        Role = "user",
                        Content = message,
                        Timestamp = evt.CreatedAt
                    });
                }
                else if (evt.EventType == "PM_REPORT" || evt.EventType == "PM_INTERVENTION")
                {
                    var payload = JsonSerializer.Deserialize<JsonElement>(evt.Payload);
                    var runId = payload.TryGetProperty("runId", out var r) ? r.GetString() : null;
                    var outputsFolder = payload.TryGetProperty("outputsFolder", out var of) ? of.GetString() : null;
                    var commitSha = payload.TryGetProperty("commitSha", out var cs) ? cs.GetString() : null;
                    var reportPath = payload.TryGetProperty("reportPath", out var rp) ? rp.GetString() : null;
                    var sb = new System.Text.StringBuilder();
                    if (evt.EventType == "PM_REPORT")
                    {
                        sb.AppendLine($"✅ 프로젝트 종료 보고 — Run `{runId}`");
                        if (!string.IsNullOrWhiteSpace(outputsFolder)) sb.AppendLine($"📁 산출물 폴더: {outputsFolder}");
                        if (!string.IsNullOrWhiteSpace(commitSha)) sb.AppendLine($"🔖 Git 커밋: {commitSha[..Math.Min(8, commitSha.Length)]}");
                    }
                    else
                    {
                        sb.AppendLine($"⚠ User 개입 요청 — Run `{runId}`");
                    }
                    Messages.Add(new ChatMessage
                    {
                        Role = evt.EventType == "PM_REPORT" ? "pm_report" : "pm_intervention",
                        Content = sb.ToString().TrimEnd(),
                        Timestamp = evt.CreatedAt,
                        IsEndReport = evt.EventType == "PM_REPORT",
                        IsUserIntervention = evt.EventType == "PM_INTERVENTION",
                        RunId = runId,
                        OutputsFolder = outputsFolder,
                        CommitSha = commitSha,
                        ReportPath = reportPath
                    });
                }
                else if (evt.EventType == "AI_RESPONSE" || evt.EventType == "PM_RESPONSE")
                {
                    var payload = JsonSerializer.Deserialize<JsonElement>(evt.Payload);
                    var content = payload.GetProperty("content").GetString() ?? string.Empty;
                    var personaId = payload.TryGetProperty("personaId", out var pid) ? pid.GetString() : null;
                    Persona? persona = null;
                    if (!string.IsNullOrEmpty(personaId)) personaById.TryGetValue(personaId, out persona);
                    var isPm = evt.EventType == "PM_RESPONSE"
                        || (payload.TryGetProperty("isPm", out var ip) && ip.ValueKind == JsonValueKind.True);

                    var sb = new System.Text.StringBuilder();
                    if (!string.IsNullOrWhiteSpace(content)) sb.Append(content);

                    if (payload.TryGetProperty("toolCalls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in tc.EnumerateArray())
                        {
                            var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            var args = t.TryGetProperty("args", out var a) ? a.GetString() ?? "" : "";
                            var success = t.TryGetProperty("success", out var s) && s.GetBoolean();
                            var result = t.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
                            if (sb.Length > 0) sb.Append("\n\n");
                            sb.Append($"🔧 {(success ? "✓" : "✗")} {name}({args}) → {result}");
                        }
                    }

                    string? handoffTargetLabel = null;
                    string? handoffReq = null;
                    if (payload.TryGetProperty("handoffTo", out var ht) && ht.ValueKind == JsonValueKind.String)
                    {
                        var handoffTo = ht.GetString();
                        handoffReq = payload.TryGetProperty("handoffRequest", out var hr) ? hr.GetString() : null;
                        if (!string.IsNullOrEmpty(handoffTo))
                        {
                            handoffTargetLabel = personaByName.TryGetValue(handoffTo, out var ht2)
                                ? ht2.Label ?? handoffTo
                                : handoffTo;
                        }
                    }

                    int? turnIdx = null;
                    if (payload.TryGetProperty("turnIndex", out var ti) && ti.ValueKind == JsonValueKind.Number)
                        turnIdx = ti.GetInt32();

                    string? discussionId = payload.TryGetProperty("discussionId", out var did) && did.ValueKind == JsonValueKind.String
                        ? did.GetString() : null;
                    int? roundIdx = null;
                    if (payload.TryGetProperty("roundIndex", out var ri) && ri.ValueKind == JsonValueKind.Number)
                        roundIdx = ri.GetInt32();
                    string? stanceVal = payload.TryGetProperty("stance", out var stv) && stv.ValueKind == JsonValueKind.String
                        ? stv.GetString() : null;
                    bool isDiscussionSpeaker = payload.TryGetProperty("isDiscussionSpeaker", out var ids) && ids.ValueKind == JsonValueKind.True;
                    bool isDiscussionOpener = payload.TryGetProperty("isDiscussionOpener", out var ido) && ido.ValueKind == JsonValueKind.True;
                    bool isDiscussionSummary = payload.TryGetProperty("isDiscussionSummary", out var idsum) && idsum.ValueKind == JsonValueKind.True;
                    string? discussionTopic = payload.TryGetProperty("discussionTopic", out var dtop) && dtop.ValueKind == JsonValueKind.String
                        ? dtop.GetString() : null;

                    var writtenFiles = new List<string>();
                    if (payload.TryGetProperty("writtenFiles", out var wf) && wf.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var f in wf.EnumerateArray())
                            if (f.ValueKind == JsonValueKind.String) writtenFiles.Add(f.GetString()!);
                    }
                    if (writtenFiles.Count > 0)
                    {
                        sb.Append("\n\n📎 생성·수정 파일:");
                        foreach (var f in writtenFiles) sb.Append($"\n  - {f}");
                    }

                    var loadedMsg = new ChatMessage
                    {
                        Role = "assistant",
                        Content = sb.ToString().TrimEnd(),
                        PersonaLabel = persona?.Label,
                        PersonaAvatar = persona?.Avatar,
                        PersonaIcon = persona?.Icon,
                        PersonaColor = persona?.Color,
                        ModelUsed = evt.ModelUsed,
                        Timestamp = evt.CreatedAt,
                        IsPm = isPm,
                        WrittenFiles = writtenFiles,
                        TurnIndex = turnIdx,
                        HandoffToLabel = handoffTargetLabel,
                        HandoffRequest = handoffReq,
                        DiscussionId = discussionId,
                        RoundIndex = roundIdx,
                        Stance = stanceVal,
                        IsDiscussionSpeaker = isDiscussionSpeaker,
                        IsDiscussionOpener = isDiscussionOpener,
                        IsDiscussionSummary = isDiscussionSummary,
                        DiscussionTopic = discussionTopic
                    };
                    // 로드 시에도 토론 마커를 앞에 붙여 재방문 시 일관된 렌더를 보장
                    loadedMsg.Content = ApplyDiscussionMarker(loadedMsg.Content, loadedMsg);
                    Messages.Add(loadedMsg);
                }
            }
            catch { /* skip malformed events */ }
        }
    }

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        var message = InputMessage?.Trim() ?? string.Empty;
        var attachments = Attachments.ToList();
        if ((string.IsNullOrEmpty(message) && attachments.Count == 0) || _authService.CurrentUserId == null) return;

        InputMessage = string.Empty;
        Attachments.Clear();
        ErrorMessage = null;
        _cts = new CancellationTokenSource();
        IsLoading = true;
        StartGenerationTimer();

        // 사용자 메시지 표시 — 첨부파일은 파일명 목록만 노출
        var displayMessage = attachments.Count == 0
            ? message
            : (string.IsNullOrEmpty(message) ? string.Empty : message + "\n\n")
              + "📎 첨부: " + string.Join(", ", attachments.Select(a => a.Name));

        Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = displayMessage,
            Timestamp = DateTimeOffset.UtcNow
        });

        // AI에게 전달할 메시지에는 첨부파일 전체 내용을 포함한다
        var payloadMessage = message;
        if (attachments.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(message))
            {
                sb.Append(message);
                sb.Append("\n\n");
            }
            sb.AppendLine("[첨부 파일]");
            foreach (var a in attachments)
            {
                sb.AppendLine();
                sb.AppendLine($"--- {a.Name} ---");
                sb.AppendLine("```markdown");
                sb.AppendLine(a.Content);
                sb.AppendLine("```");
            }
            payloadMessage = sb.ToString().TrimEnd();
        }

        try
        {
            // 팀 모드가 활성이면 선택된 페르소나 목록으로 팀 파이프라인을 실행한다.
            // 비활성이면 @mention 라우팅(PM 허브 우회)을 사용한다.
            var teamIds = TeamPickerItems.Where(x => x.IsSelected).Select(x => x.Persona.PersonaId).ToList();
            var isTeamMode = teamIds.Count >= 2;
            var mentioned = isTeamMode ? null : ResolveMention(message);

            var priorConversation = BuildPriorConversation();

            var input = new OrchestratorInput
            {
                ProjectId = ProjectId,
                UserId = _authService.CurrentUserId,
                Message = payloadMessage,
                ForcePersonaId = isTeamMode ? null : mentioned?.PersonaId,
                TeamPersonaIds = isTeamMode ? teamIds : null,
                TeamMode = isTeamMode ? TeamMode : null,
                AskUserEnabled = AskUserEnabled,
                PriorConversation = priorConversation.Count > 0 ? priorConversation : null
            };

            var progress = new Progress<AgentTurn>(turn =>
            {
                var existingIdx = !string.IsNullOrEmpty(turn.StreamKey)
                    ? IndexOfStreamKey(turn.StreamKey)
                    : -1;

                if (turn.IsStreamingPreview)
                {
                    StreamingPersonaId = turn.PersonaId;
                    StreamingPreview = turn.Content;

                    // 프리뷰: 기존 메시지가 있으면 Content만 갱신, 없으면 최초 청크로 새 메시지 생성
                    if (existingIdx >= 0)
                    {
                        Messages[existingIdx].Content = turn.Content;
                    }
                    else
                    {
                        Messages.Add(BuildAssistantMessage(turn, previewOnly: true));
                    }
                    return;
                }

                StreamingPersonaId = turn.PersonaId;

                // 최종 턴: 프리뷰 메시지를 완성본으로 치환한다
                var finalized = BuildAssistantMessage(turn, previewOnly: false);
                if (existingIdx >= 0)
                    Messages[existingIdx] = finalized;
                else
                    Messages.Add(finalized);
            });

            var output = await _orchestrator.RunPipelineAsync(input, progress, _cts.Token);

            if (output.NeedsConfirmation)
            {
                Messages.Add(new ChatMessage
                {
                    Role = "system",
                    Content = output.Content,
                    Timestamp = DateTimeOffset.UtcNow
                });
            }
            else if (output.IsEndReport)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"✅ 프로젝트 종료 보고 — Run `{output.RunId}`");
                if (!string.IsNullOrWhiteSpace(output.OutputsFolder))
                    sb.AppendLine($"📁 산출물 폴더: {output.OutputsFolder}");
                if (!string.IsNullOrWhiteSpace(output.ReportPath))
                    sb.AppendLine($"📄 보고서: {output.ReportPath}");
                if (!string.IsNullOrWhiteSpace(output.CommitSha))
                    sb.AppendLine($"🔖 Git 커밋: {output.CommitSha[..Math.Min(8, output.CommitSha.Length)]}");
                Messages.Add(new ChatMessage
                {
                    Role = "pm_report",
                    Content = sb.ToString().TrimEnd(),
                    Timestamp = DateTimeOffset.UtcNow,
                    IsEndReport = true,
                    RunId = output.RunId,
                    OutputsFolder = output.OutputsFolder,
                    ReportPath = output.ReportPath,
                    CommitSha = output.CommitSha
                });
            }
            else if (output.IsUserIntervention)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("⚠ User 개입 요청");
                if (!string.IsNullOrWhiteSpace(output.InterventionReason))
                    sb.AppendLine($"사유: {output.InterventionReason}");
                if (!string.IsNullOrWhiteSpace(output.InterventionQuestion))
                    sb.AppendLine($"질문: {output.InterventionQuestion}");
                Messages.Add(new ChatMessage
                {
                    Role = "pm_intervention",
                    Content = sb.ToString().TrimEnd(),
                    Timestamp = DateTimeOffset.UtcNow,
                    IsUserIntervention = true,
                    RunId = output.RunId,
                    InterventionReason = output.InterventionReason,
                    InterventionQuestion = output.InterventionQuestion
                });
            }
        }
        catch (OperationCanceledException)
        {
            Messages.Add(new ChatMessage
            {
                Role = "system",
                Content = "⏹ 토론이 중단되었습니다. PM이 진행 내용을 정리합니다…",
                Timestamp = DateTimeOffset.UtcNow
            });

            var pm = Personas.FirstOrDefault(p => p.IsPm);
            if (pm != null)
            {
                try
                {
                    using var summaryCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var priorConversation = BuildPriorConversation();
                    var summary = await _orchestrator.SummarizeCancelAsync(
                        ProjectId, pm.PersonaId, priorConversation, summaryCts.Token);
                    if (!string.IsNullOrWhiteSpace(summary))
                        Messages.Add(new ChatMessage
                        {
                            Role = "assistant",
                            Content = summary,
                            PersonaLabel = pm.Label,
                            Timestamp = DateTimeOffset.UtcNow
                        });
                }
                catch { /* 요약 실패 무시 */ }
            }
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage
            {
                Role = "error",
                Content = ex.Message,
                Timestamp = DateTimeOffset.UtcNow
            });
        }
        finally
        {
            StreamingPersonaId = null;
            StreamingPreview = null;
            IsLoading = false;
            StopGenerationTimer();
            _cts?.Dispose();
            _cts = null;
        }
    }

}
