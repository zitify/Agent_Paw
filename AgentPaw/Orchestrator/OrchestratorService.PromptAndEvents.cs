using System.Text;
using System.Text.Json;
using AgentPaw.Models;

namespace AgentPaw.Orchestrator;

public partial class OrchestratorService
{
    private static string BuildTeamContextPrompt(
        string originalUserMessage, List<AgentTurn> history, string fromLabel, string requestToMe)
    {
        var historyText = RenderHistory(history);
        return
            "[팀 협업 컨텍스트]\n" +
            "사용자가 팀에게 요청한 원본 메시지:\n" +
            originalUserMessage + "\n\n" +
            "지금까지 팀에서 진행된 대화:\n" +
            historyText + "\n\n" +
            $"[지금 당신의 차례 — '{fromLabel}'로부터 받은 요청]\n" +
            requestToMe + "\n\n" +
            "당신의 역할에 맞게 응답하고, 필요하면 tool/handoff를 사용한다.";
    }

    private static string BuildToolFeedbackPrompt(
        string originalUserMessage, List<AgentTurn> history, string toolResults)
    {
        var historyText = RenderHistory(history);
        return
            "[컨텍스트]\n" +
            "사용자 원본 요청:\n" +
            originalUserMessage + "\n\n" +
            "지금까지의 대화:\n" +
            historyText + "\n\n" +
            "[방금 네가 호출한 도구의 실행 결과]\n" +
            toolResults + "\n\n" +
            "이 결과를 반영해 작업을 이어간다. 추가 도구가 필요하면 호출하고, 작업이 끝났으면 최종 응답 또는 handoff로 마무리한다.";
    }

    private static string RenderHistory(List<AgentTurn> history)
    {
        var parts = new List<string>();
        foreach (var h in history)
        {
            var body = string.IsNullOrWhiteSpace(h.Content) ? "(내용 없음)" : h.Content;
            var sb = new StringBuilder();
            sb.Append($"[{h.PersonaLabel}]\n{body}");

            if (h.ToolCalls.Count > 0)
            {
                sb.Append("\n  (도구 호출: ");
                sb.Append(string.Join(", ", h.ToolCalls.Select(t =>
                    $"{t.Name}({t.ArgsSummary}) → {(t.Success ? "ok" : "fail")}")));
                sb.Append(')');
            }
            parts.Add(sb.ToString());
        }
        return string.Join("\n\n", parts);
    }

    private static string BuildToolResultsText(List<ToolCallRecord> records)
    {
        var sb = new StringBuilder();
        foreach (var r in records)
        {
            sb.AppendLine($"▸ {r.Name}({r.ArgsSummary}) — {(r.Success ? "성공" : "실패")}");
            sb.AppendLine(r.Result);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string SummarizeArgs(Dictionary<string, object?> args)
    {
        var parts = new List<string>();
        foreach (var kv in args)
        {
            var v = kv.Value?.ToString() ?? "";
            if (kv.Key.Equals("content", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"{kv.Key}=<{System.Text.Encoding.UTF8.GetByteCount(v)}B>");
            }
            else
            {
                var trimmed = v.Length > 60 ? v[..60] + "…" : v;
                parts.Add($"{kv.Key}={trimmed}");
            }
        }
        return string.Join(", ", parts);
    }

    private async Task<string> LogEventsAsync(
        OrchestratorInput input,
        List<AgentTurn> turns,
        string runId,
        bool endReport,
        bool userIntervention,
        string? outputsFolder,
        string? commitSha,
        string? reportPath = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var userEventId = Guid.NewGuid().ToString();
        db.EventLogs.Add(new EventLog
        {
            EventId = userEventId,
            ProjectId = input.ProjectId,
            EventType = "USER_MESSAGE",
            Payload = JsonSerializer.Serialize(new
            {
                runId,
                message = input.Message,
                personaId = turns.FirstOrDefault()?.PersonaId
            }),
            TriggeredBy = input.UserId,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var lastEventId = userEventId;
        foreach (var turn in turns)
        {
            // 턴 생성 시 미리 발급된 EventId 를 그대로 사용한다 — Wiki sourceEventId 등 외부 참조와 일치시킨다.
            var aiEventId = string.IsNullOrEmpty(turn.EventId) ? Guid.NewGuid().ToString() : turn.EventId;
            db.EventLogs.Add(new EventLog
            {
                EventId = aiEventId,
                ProjectId = input.ProjectId,
                EventType = turn.IsPm ? "PM_RESPONSE" : "AI_RESPONSE",
                Payload = JsonSerializer.Serialize(new
                {
                    runId,
                    content = turn.Content,
                    personaId = turn.PersonaId,
                    personaName = turn.PersonaName,
                    isPm = turn.IsPm,
                    turnIndex = turn.TurnIndex,
                    handoffTo = turn.HandoffToName,
                    handoffRequest = turn.HandoffRequest,
                    writtenFiles = turn.WrittenFiles,
                    isEndReport = turn.IsEndReport,
                    isUserIntervention = turn.IsUserIntervention,
                    discussionId = turn.DiscussionId,
                    roundIndex = turn.RoundIndex,
                    speakerOrder = turn.SpeakerOrder,
                    stance = turn.Stance,
                    isDiscussionOpener = turn.IsDiscussionOpener,
                    isDiscussionSpeaker = turn.IsDiscussionSpeaker,
                    isDiscussionSummary = turn.IsDiscussionSummary,
                    discussionTopic = turn.DiscussionTopic,
                    toolCalls = turn.ToolCalls.Select(t => new
                    {
                        name = t.Name,
                        args = t.ArgsSummary,
                        success = t.Success,
                        result = t.Result
                    })
                }),
                ModelUsed = turn.ModelUsed,
                TriggeredBy = input.UserId,
                CreatedAt = DateTimeOffset.UtcNow
            });
            lastEventId = aiEventId;
        }

        if (endReport || userIntervention)
        {
            var pmEventId = Guid.NewGuid().ToString();
            db.EventLogs.Add(new EventLog
            {
                EventId = pmEventId,
                ProjectId = input.ProjectId,
                EventType = endReport ? "PM_REPORT" : "PM_INTERVENTION",
                Payload = JsonSerializer.Serialize(new
                {
                    runId,
                    outputsFolder,
                    commitSha,
                    reportPath
                }),
                TriggeredBy = input.UserId,
                CreatedAt = DateTimeOffset.UtcNow
            });
            lastEventId = pmEventId;
        }

        await db.SaveChangesAsync(ct);
        return lastEventId;
    }

}
