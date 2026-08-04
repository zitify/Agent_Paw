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
    private async Task RunChainAsync(
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
            var streamKey = $"{runId}-chain-{i}";

            var addendum = BuildTeamModeAddendum(teamPersonas, config.Name, workspaceRoot, "chain");
            var systemPrompt = string.IsNullOrWhiteSpace(addendum)
                ? config.SystemPrompt
                : config.SystemPrompt + "\n\n" + addendum;

            var userPrompt = i == 0
                ? await _contextInjector.InjectAsync(input.Message, input.ProjectId, config.Name, 1.0, ct)
                : BuildChainContextPrompt(input.Message, history, config.Label, i);

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
                history: i == 0 ? input.PriorConversation : null,
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

    private static string BuildTeamModeAddendum(
        List<Persona> teamPersonas, string currentName, string workspaceRoot, string mode)
    {
        var sb = new StringBuilder();

        if (mode == "panel")
        {
            sb.AppendLine("[팀 패널 모드]");
            sb.AppendLine("사용자가 여러 에이전트에게 동시에 독립 응답을 요청했다.");
            sb.AppendLine("다른 에이전트의 응답을 알 수 없으므로 네 전문 영역에서 독립적으로 최선의 응답을 작성한다.");
            sb.AppendLine("handoff / pm_report / pm_intervention 블록은 사용하지 않는다.");
        }
        else // chain
        {
            sb.AppendLine("[팀 체인 모드]");
            sb.AppendLine("사용자가 여러 에이전트에게 순차적으로 작업을 이어받도록 요청했다.");
            sb.AppendLine("이전 에이전트의 결과를 이어받아 네 전문 영역에서 추가 기여한다.");
            sb.AppendLine("이미 충분히 다뤄진 내용은 반복하지 않고 네 역할에서 새로 추가할 내용에 집중한다.");
            sb.AppendLine("handoff / pm_report / pm_intervention 블록은 사용하지 않는다.");
        }

        sb.AppendLine("참여 팀원: " + string.Join(", ",
            teamPersonas.Select(p =>
                $"{p.Name}({p.Label})" +
                (string.Equals(p.Name, currentName, StringComparison.OrdinalIgnoreCase) ? " ← 너" : ""))));
        sb.AppendLine();
        sb.AppendLine("[도구 사용]");
        sb.AppendLine($"작업 폴더(workspace root): {workspaceRoot}");
        sb.AppendLine("  - read_file(path) / list_dir(path?) / search_files(pattern, path?, include?)");
        sb.AppendLine("  - write_file(path, content) — 신규 파일 전체 작성");
        sb.AppendLine("  - edit_file(path, old_text, new_text) — 기존 파일 부분 교체 (old_text는 파일 내 1곳만 존재해야 함)");
        sb.AppendLine("  - append_file(path, content) / make_dir(path) / delete_file(path)");
        sb.AppendLine("  - run_command(command) — 작업 폴더 기준 셸 명령 실행 (빌드·테스트·git). 타임아웃 60초.");
        sb.AppendLine("  - generate_image(prompt, path, size?, quality?) — DALL-E 3 이미지 생성 후 파일 저장. OPENAI 키 필요.");
        sb.AppendLine("```tool");
        sb.AppendLine("{\"name\": \"<도구명>\", \"args\": {<인자>}}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("[위키 승격]");
        sb.AppendLine("나중에 참조할 가치가 있는 의사결정·명세·트러블슈팅은 wiki_save 블록으로 저장한다.");
        sb.AppendLine("```wiki_save");
        sb.AppendLine("{\"category\": \"WIKI_ADR|WIKI_SPEC|WIKI_TROUBLE\", \"title\": \"제목\", \"content\": \"내용\"}");
        sb.AppendLine("```");

        return sb.ToString().TrimEnd();
    }

    private static string BuildChainContextPrompt(
        string originalMessage, List<AgentTurn> priorTurns, string currentLabel, int step)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[원본 사용자 요청]");
        sb.AppendLine(originalMessage);
        sb.AppendLine();
        sb.AppendLine($"[이전 에이전트 응답 — {step}단계 누적]");
        foreach (var t in priorTurns)
        {
            sb.AppendLine($"── {t.PersonaLabel} ──");
            sb.AppendLine(string.IsNullOrWhiteSpace(t.Content) ? "(응답 없음)" : t.Content);
            sb.AppendLine();
        }
        sb.AppendLine($"[{currentLabel}(너)의 차례 — 단계 {step + 1}]");
        sb.AppendLine("이전 에이전트들의 작업을 이어받아 네 전문 역할에서 추가 기여한다.");
        return sb.ToString().TrimEnd();
    }

    private static void AppendWikiSaveProtocol(StringBuilder sb)
    {
        sb.AppendLine("[위키 승격 프로토콜 — wiki_save]");
        sb.AppendLine("대화 중 발생한 의사결정·명세·트러블슈팅 중 **나중에 채팅을 뒤지지 않고도 조회해야 할 가치가 있는** 내용은");
        sb.AppendLine("아래 블록을 응답 본문 끝에 추가해 즉시 프로젝트 위키로 승격한다. 한 응답에 여러 블록을 쓸 수 있다.");
        sb.AppendLine("```wiki_save");
        sb.AppendLine("{\"category\": \"WIKI_ADR|WIKI_SPEC|WIKI_TROUBLE\", \"title\": \"<간결한 제목>\", \"content\": \"<마크다운 본문 — 배경·결정·근거·영향 순으로 자기충족적으로 기록>\"}");
        sb.AppendLine("```");
        sb.AppendLine("- WIKI_ADR : 아키텍처·정책·워크플로 결정 (이유·대안·트레이드오프 포함)");
        sb.AppendLine("- WIKI_SPEC : 기능·API·데이터 스키마 명세");
        sb.AppendLine("- WIKI_TROUBLE : 재발 가능성 있는 문제의 증상·원인·해결 경로");
        sb.AppendLine("- 단순 상태 보고·짧은 대화·추측은 위키로 저장하지 않는다. 6개월 뒤 다시 꺼낼 가치가 있어야 한다.");
        sb.AppendLine("- 블록은 UI 본문에서 자동 제거되므로 사용자에게 설명하듯 자연스러운 톤으로 기록한다.");
        sb.AppendLine();
    }

    // 스트리밍 프리뷰에서 내부용 펜스 블록(tool/handoff/pm_*)은 UI에 노출하지 않는다
    private static readonly string[] InternalFenceMarkers =
    [
        "```tool",
        "```handoff",
        "```pm_report",
        "```pm_intervention",
        "```discussion",
        "```discussion_summary",
        "```stance",
        "```wiki_save"
    ];

    private static string CleanStreamingPreview(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        int earliest = raw.Length;
        foreach (var m in InternalFenceMarkers)
        {
            int idx = raw.IndexOf(m, StringComparison.Ordinal);
            if (idx >= 0 && idx < earliest) earliest = idx;
        }
        return earliest < raw.Length ? raw[..earliest].TrimEnd() : raw;
    }

    private static bool IsFileWritingTool(string toolName)
    {
        var n = toolName?.ToLowerInvariant();
        return n is "write_file" or "append_file" or "edit_file" or "generate_image";
    }

    private static string? GetStringArg(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val) || val == null) return null;
        return val.ToString();
    }

    private static string BuildAutoReturnRequest(string fromLabel, string body, List<string> writtenFiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{fromLabel}] 역할의 작업이 완료되어 당신(PM)에게 자동 복귀했다.");
        sb.AppendLine();
        sb.AppendLine("역할 응답 본문:");
        sb.AppendLine(string.IsNullOrWhiteSpace(body) ? "(본문 없음)" : body.Trim());
        if (writtenFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("이번 턴에서 생성·수정된 파일:");
            foreach (var f in writtenFiles.Distinct())
                sb.AppendLine($"- {f}");
        }
        sb.AppendLine();
        sb.AppendLine("다음 행동 주체를 결정한다:");
        sb.AppendLine("1) 다음 역할에게 이어서 지시한다 → handoff 블록");
        sb.AppendLine("2) User 개입이 필요하다 → pm_intervention 블록");
        sb.AppendLine("3) 프로젝트가 완료되었다 → pm_report 블록");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> ResolveWorkspaceRootAsync(string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var project = await db.Projects.FindAsync(projectId);
        if (project != null && !string.IsNullOrWhiteSpace(project.GitRepoPath))
            return project.GitRepoPath;

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentPaw", "repos", projectId);
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private async Task<bool> ResolveAskUserEnabledAsync(OrchestratorInput input)
    {
        if (input.AskUserEnabled.HasValue) return input.AskUserEnabled.Value;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var project = await db.Projects.FindAsync(input.ProjectId);
        return project?.AskUserEnabled ?? true;
    }

    private async Task<(int maxRounds, int maxParticipants)> ResolveDiscussionSettingsAsync(string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var project = await db.Projects.FindAsync(projectId);
        return (
            project?.MaxDiscussionRounds       ?? DefaultMaxDiscussionRounds,
            project?.MaxDiscussionParticipants ?? DefaultMaxDiscussionParticipants
        );
    }

    private static bool DetectDevIntent(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var patterns = new[]
        {
            "개발해줘", "개발해 줘", "만들어줘", "만들어 줘", "구현해줘", "구현해 줘",
            "코딩해줘", "코딩해 줘", "작성해줘", "작성해 줘", "짜줘", "짜 줘",
            "개발하자", "만들자", "구현하자", "코딩하자",
            "앱 만들", "프로그램 만들", "기능 만들", "기능 추가", "기능 구현",
            "개발 부탁", "만들어달라", "구현해달라", "코드 짜", "코드 작성",
            "개발 시작", "구현 시작", "만들기 시작",
            "build me", "make me", "implement", "create a", "code a", "write a"
        };
        return patterns.Any(p => message.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string> SummarizeCancelAsync(
        string projectId, string personaId,
        IReadOnlyList<ConversationTurn> prior,
        CancellationToken ct = default)
    {
        var config = await _configLoader.GetPersonaConfigAsync(personaId, projectId, ct);

        var sb = new System.Text.StringBuilder();
        foreach (var t in prior)
        {
            var label = t.Role == "user" ? "User" : "Assistant";
            sb.AppendLine($"[{label}]\n{t.Content}\n");
        }

        var userMsg = prior.Count > 0
            ? sb + "\n---\n대화가 중단되었습니다. 지금까지 논의된 내용을 간결하게 정리하고, 현재 진행 상태와 미결 사항을 요약해주세요."
            : "대화가 중단되었습니다. 진행 내용이 없습니다.";

        return await _aiClient.ChatWithFastModelAsync(config.SystemPrompt, userMsg, maxTokens: 2048, ct);
    }

    public async Task<List<WikiDocument>> ConsolidateWikiAsync(string projectId, IProgress<string>? progress = null)
    {
        // Load last 200 conversation events
        progress?.Report("대화 기록 불러오는 중...");
        await using var db = await _dbFactory.CreateDbContextAsync();
        var events = await db.EventLogs.AsNoTracking()
            .Where(e => e.ProjectId == projectId && !e.IsDeleted
                && (e.EventType == "USER_MESSAGE" || e.EventType == "AI_RESPONSE"
                    || e.EventType == "PM_RESPONSE" || e.EventType == "PM_REPORT"
                    || e.EventType == "PM_INTERVENTION"))
            .OrderBy(e => e.CreatedAt)
            .Take(200)
            .ToListAsync();

        // Load existing wiki documents
        progress?.Report("기존 위키 목록 불러오는 중...");
        var existingDocs = await _wiki.ListWikisAsync(projectId);

        // 기존 트리의 뎁스 정보 계산
        var depthMap = BuildWikiDepthMap(existingDocs);
        int totalDepths = depthMap.Count > 0 ? depthMap.Values.Max() + 1 : 1;

        if (events.Count == 0 && existingDocs.Count == 0) return [];

        // Build conversation transcript
        var transcriptSb = new System.Text.StringBuilder();
        foreach (var e in events)
        {
            var sender = e.EventType == "USER_MESSAGE" ? "User" : "Agent";
            string content = "";
            try
            {
                using var jdoc = JsonDocument.Parse(e.Payload);
                var root = jdoc.RootElement;
                content = (root.TryGetProperty("message", out var msg) ? msg.GetString() : null)
                       ?? (root.TryGetProperty("content", out var cnt) ? cnt.GetString() : null)
                       ?? (root.TryGetProperty("text", out var txt) ? txt.GetString() : null)
                       ?? "";
            }
            catch { }
            if (!string.IsNullOrWhiteSpace(content))
                transcriptSb.AppendLine($"[{sender}] {content}");
        }

        // Build existing wiki index
        var existingSb = new System.Text.StringBuilder();
        if (existingDocs.Count > 0)
        {
            existingSb.AppendLine("=== 기존 위키 문서 목록 ===");
            foreach (var d in existingDocs)
            {
                existingSb.AppendLine($"wikiId: {d.WikiId}");
                existingSb.AppendLine($"parentId: {(d.ParentId ?? "(없음)")}");
                existingSb.AppendLine($"title: {d.Title}");
                var preview = d.Content.Length > 400 ? d.Content[..400] + "…" : d.Content;
                existingSb.AppendLine($"content: {preview}");
                existingSb.AppendLine("---");
            }
        }

        var systemPrompt = """
You are a knowledge management specialist organizing a project wiki.

You receive:
1. EXISTING wiki documents (each with a wikiId)
2. A recent conversation transcript with new knowledge

Your tasks:
- Extract meaningful new knowledge from the conversation
- MERGE duplicates or overlapping topics into the existing document (update its content)
- Detect PARENT-CHILD relationships between topics and assign a proper hierarchy
- Reorganize existing documents into a logical hierarchy when beneficial

Return ONLY a valid JSON object (no markdown, no explanation):
{
  "merges": [
    {
      "wikiId": "<existing wikiId>",
      "title": "<updated title>",
      "content": "<merged Markdown content>"
    }
  ],
  "creates": [
    {
      "tempId": "c1",
      "title": "...",
      "content": "<Markdown content>",
      "parentRef": null
    },
    {
      "tempId": "c2",
      "title": "...",
      "content": "...",
      "parentRef": "c1"
    },
    {
      "tempId": "c3",
      "title": "...",
      "content": "...",
      "parentRef": "<existing wikiId>"
    }
  ],
  "reparents": [
    {
      "wikiId": "<existing wikiId>",
      "newParentRef": "<existing wikiId or tempId or null>"
    }
  ]
}

Rules:
- parentRef / newParentRef: null = root level, tempId string = child of a newly created page, existing wikiId = child of existing page
- Only merge when content truly overlaps. Do NOT merge unrelated topics into one page.
- Only create pages for knowledge NOT already covered by existing docs.
- If nothing to do for a section, use [].
- Respond in Korean for all titles and content.
""";

        var userContent = new System.Text.StringBuilder();
        if (existingDocs.Count > 0)
            userContent.AppendLine(existingSb.ToString());
        if (transcriptSb.Length > 0)
        {
            userContent.AppendLine("=== 최근 대화 내용 ===");
            userContent.AppendLine(transcriptSb.ToString());
        }

        progress?.Report($"AI가 지식을 분석하는 중... (총 {totalDepths}뎁스)");
        var responseContent = await _aiClient.ChatWithFastModelAsync(
            systemPrompt, userContent.ToString(), maxTokens: 4096
        );

        var modified = new List<WikiDocument>();
        try
        {
            var text = responseContent.Trim();
            if (text.StartsWith("```")) text = text[(text.IndexOf('\n') + 1)..];
            if (text.EndsWith("```")) text = text[..text.LastIndexOf("```")].TrimEnd();

            using var jdoc = JsonDocument.Parse(text);
            var root = jdoc.RootElement;

            // 1. Merges — 뎁스 순으로 수집 후 깊이별 progress 보고
            if (root.TryGetProperty("merges", out var merges))
            {
                // 한 번 열거해 리스트로 수집 (뎁스별 정렬을 위해)
                var mergeOps = new List<(int Depth, string WikiId, string? Title, string? Content)>();
                foreach (var item in merges.EnumerateArray())
                {
                    var wikiId  = item.TryGetProperty("wikiId",  out var wi) ? wi.GetString() : null;
                    var title   = item.TryGetProperty("title",   out var ti) ? ti.GetString() : null;
                    var content = item.TryGetProperty("content", out var ci) ? ci.GetString() : null;
                    if (string.IsNullOrWhiteSpace(wikiId)) continue;
                    if (existingDocs.All(doc => doc.WikiId != wikiId)) continue;
                    var depthVal = depthMap.TryGetValue(wikiId, out var dv) ? dv : 0;
                    mergeOps.Add((depthVal, wikiId, title, content));
                }

                for (int d = 0; d < totalDepths; d++)
                {
                    var opsAtDepth = mergeOps.Where(m => m.Depth == d).ToList();
                    if (opsAtDepth.Count == 0) continue;
                    progress?.Report($"기존 문서 병합 중 ({d + 1}/{totalDepths}뎁스)...");
                    foreach (var (_, wikiId, title, content) in opsAtDepth)
                    {
                        var existing = existingDocs.FirstOrDefault(doc => doc.WikiId == wikiId);
                        if (existing == null) continue;
                        await _wiki.UpdateWikiAsync(wikiId, title, content, null);
                        if (title != null) existing.Title = title;
                        if (content != null) existing.Content = content;
                        modified.Add(existing);
                    }
                }
            }

            // 2. Creates — 새 페이지 (parentRef가 tempId 또는 기존 wikiId)
            var tempIdMap = new Dictionary<string, string>(); // tempId → real wikiId
            if (root.TryGetProperty("creates", out var creates))
            {
                var createList = creates.EnumerateArray().ToList();
                int createIdx = 0;
                foreach (var item in createList)
                {
                    createIdx++;
                    var tempId    = item.TryGetProperty("tempId",    out var ti) ? ti.GetString() ?? "" : "";
                    var title     = item.TryGetProperty("title",     out var t)  ? t.GetString()  ?? "" : "";
                    var content   = item.TryGetProperty("content",   out var c)  ? c.GetString()  ?? "" : "";
                    var parentRef = item.TryGetProperty("parentRef", out var pr) && pr.ValueKind != JsonValueKind.Null
                                        ? pr.GetString() : null;
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    progress?.Report($"새 문서 생성 중 ({createIdx}/{createList.Count})...");

                    string? resolvedParentId = null;
                    if (!string.IsNullOrWhiteSpace(parentRef))
                    {
                        if (tempIdMap.TryGetValue(parentRef, out var mapped))
                            resolvedParentId = mapped;
                        else if (existingDocs.Any(doc => doc.WikiId == parentRef))
                            resolvedParentId = parentRef;
                    }

                    var created = await _wiki.CreateWikiAsync(projectId, "일반", title, content, parentId: resolvedParentId);
                    if (!string.IsNullOrWhiteSpace(tempId)) tempIdMap[tempId] = created.WikiId;
                    modified.Add(created);
                }
            }

            // 3. Reparents — 계층 재배치
            if (root.TryGetProperty("reparents", out var reparents))
            {
                var reparentList = reparents.EnumerateArray().ToList();
                int reparentIdx = 0;
                foreach (var item in reparentList)
                {
                    reparentIdx++;
                    var wikiId       = item.TryGetProperty("wikiId",       out var wi)  ? wi.GetString()  : null;
                    var newParentRef = item.TryGetProperty("newParentRef",  out var npr) && npr.ValueKind != JsonValueKind.Null
                                           ? npr.GetString() : null;
                    if (string.IsNullOrWhiteSpace(wikiId)) continue;
                    if (existingDocs.All(doc => doc.WikiId != wikiId)) continue;

                    progress?.Report($"계층 재배치 중 ({reparentIdx}/{reparentList.Count})...");

                    string? resolvedParentId = null;
                    if (!string.IsNullOrWhiteSpace(newParentRef))
                    {
                        if (tempIdMap.TryGetValue(newParentRef, out var mapped))
                            resolvedParentId = mapped;
                        else if (existingDocs.Any(doc => doc.WikiId == newParentRef))
                            resolvedParentId = newParentRef;
                    }

                    await _wiki.SetParentAsync(wikiId, resolvedParentId);
                }
            }
        }
        catch { }

        return modified;
    }

    private static Dictionary<string, int> BuildWikiDepthMap(List<WikiDocument> docs)
    {
        var parentLookup = docs.ToDictionary(d => d.WikiId, d => d.ParentId);
        var depthMap = new Dictionary<string, int>(docs.Count);

        int GetDepth(string id, int guard = 0)
        {
            if (guard > 64) return 0;
            if (depthMap.TryGetValue(id, out var cached)) return cached;
            if (!parentLookup.TryGetValue(id, out var parentId) || parentId == null)
            {
                depthMap[id] = 0;
                return 0;
            }
            var depth = GetDepth(parentId, guard + 1) + 1;
            depthMap[id] = depth;
            return depth;
        }

        foreach (var doc in docs)
            GetDepth(doc.WikiId);

        return depthMap;
    }
}

public class OrchestratorInput
{
    public string ProjectId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? ForcePersonaId { get; set; }

    /// <summary>
    /// 팀 모드: 이 목록에 PersonaId가 2개 이상이면 단일 에이전트 대신 팀 파이프라인을 실행한다.
    /// </summary>
    public List<string>? TeamPersonaIds { get; set; }

    /// <summary>panel | debate | chain — TeamPersonaIds가 있을 때만 사용.</summary>
    public string? TeamMode { get; set; }

    /// <summary>
    /// 같은 채팅 창의 이전 대화 기록. 에이전트가 세션을 이어서 인식할 수 있도록 첫 번째 AI 호출에 전달된다.
    /// </summary>
    public List<ConversationTurn>? PriorConversation { get; set; }

    /// <summary>
    /// PM이 User에게 재질의(pm_intervention)를 할 수 있는지 여부.
    /// null이면 프로젝트 설정값(Project.AskUserEnabled)을 사용한다.
    /// false면 PM은 스스로 판단하여 결정하고 절대 User에게 묻지 않는다.
    /// </summary>
    public bool? AskUserEnabled { get; set; }
}

public class OrchestratorOutput
{
    public string EventId { get; set; } = string.Empty;
    public string PersonaId { get; set; } = string.Empty;
    public string PersonaLabel { get; set; } = string.Empty;
    public string PersonaAvatar { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string ModelUsed { get; set; } = string.Empty;
    public bool Verified { get; set; }
    public double Confidence { get; set; }
    public bool NeedsConfirmation { get; set; }
    public List<AgentTurn> Turns { get; set; } = [];

    // PM 허브 상태
    public string RunId { get; set; } = string.Empty;
    public bool IsEndReport { get; set; }
    public bool IsUserIntervention { get; set; }
    public string? OutputsFolder { get; set; }
    public string? ReportPath { get; set; }
    public string? CommitSha { get; set; }
    public string? InterventionReason { get; set; }
    public string? InterventionQuestion { get; set; }
}

public class AgentTurn
{
    /// <summary>event_log 에 적재될 이벤트 ID. 턴 생성 시점에 미리 발급해 Wiki sourceEventId 등 외부 참조와 동기화한다.</summary>
    public string EventId { get; set; } = Guid.NewGuid().ToString();
    public int TurnIndex { get; set; }
    public string PersonaId { get; set; } = string.Empty;
    public string PersonaName { get; set; } = string.Empty;
    public string PersonaLabel { get; set; } = string.Empty;
    public string PersonaAvatar { get; set; } = string.Empty;
    public bool IsPm { get; set; }
    public string Content { get; set; } = string.Empty;
    public string ModelUsed { get; set; } = string.Empty;
    public string? HandoffToLabel { get; set; }
    public string? HandoffToName { get; set; }
    public string? HandoffRequest { get; set; }
    public List<ToolCallRecord> ToolCalls { get; set; } = [];
    public List<string> WrittenFiles { get; set; } = [];

    // PM 전용 상태
    public bool IsEndReport { get; set; }
    public bool IsUserIntervention { get; set; }
    public bool IsPmGreeting { get; set; }   // PM 첫 수신("PM — 지시 접수") 뱃지용

    // 다자 토론(round-table) 상태 — DiscussionId가 있으면 이 턴은 토론 라운드의 일부다
    public string? DiscussionId { get; set; }
    public int? RoundIndex { get; set; }
    public int? SpeakerOrder { get; set; }
    public string? Stance { get; set; }        // agree / object / extend
    public bool IsDiscussionOpener { get; set; }
    public bool IsDiscussionSpeaker { get; set; }
    public bool IsDiscussionSummary { get; set; }
    public string? DiscussionTopic { get; set; }

    // 스트리밍 상태 — 동일 StreamKey의 프리뷰는 UI에서 이어붙여 렌더한다
    public string? StreamKey { get; set; }
    public bool IsStreamingPreview { get; set; }

}
