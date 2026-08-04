using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Wpf.Ui.Controls;
using AgentPaw.Data;
using AgentPaw.Models;
using AgentPaw.Orchestrator;
using AgentPaw.Services;

namespace AgentPaw.ViewModels;

public partial class WorkspaceViewModel : ObservableObject
{
    [RelayCommand]
    private void CancelDiscussion()
    {
        _cts?.Cancel();
    }

    // === 응답 생성 타이머 ===
    private void StartGenerationTimer()
    {
        _genStopwatch.Restart();
        ShowGenTimer = true;
        GenerationTimer = "⏱ 0.0s";
        _genTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _genTimer.Tick -= OnGenerationTimerTick;   // 중복 구독 방지
        _genTimer.Tick += OnGenerationTimerTick;
        _genTimer.Start();
    }

    private void OnGenerationTimerTick(object? sender, EventArgs e)
        => GenerationTimer = $"⏱ 생성 중 {_genStopwatch.Elapsed.TotalSeconds:F1}s";

    private void StopGenerationTimer()
    {
        _genTimer?.Stop();
        _genStopwatch.Stop();
        GenerationTimer = $"⏱ 완료 {_genStopwatch.Elapsed.TotalSeconds:F1}s";
    }

    [RelayCommand]
    private async Task RetryLastMessageAsync()
    {
        // 에러 메시지 제거
        while (Messages.Count > 0 && Messages[^1].Role == "error")
            Messages.RemoveAt(Messages.Count - 1);

        // 직전 user 메시지 찾기
        if (Messages.Count == 0 || Messages[^1].Role != "user") return;
        var lastUserMsg = Messages[^1];
        Messages.RemoveAt(Messages.Count - 1);

        // 동일 메시지 재전송
        InputMessage = lastUserMsg.Content;
        await SendMessageAsync();
    }

    [RelayCommand]
    private void DismissError(ChatMessage msg)
    {
        Messages.Remove(msg);
    }

    [RelayCommand]
    private void ShowMessageDetail(ChatMessage msg)
    {
        DetailMessage = msg;
        IsDetailOpen = true;
    }

    [RelayCommand]
    private void CloseMessageDetail()
    {
        IsDetailOpen = false;
        DetailMessage = null;
    }

    [RelayCommand]
    private void CopyMessage(string? content)
    {
        if (!string.IsNullOrEmpty(content))
            System.Windows.Clipboard.SetText(content);
    }

    // 전체 대화를 markdown 문자열로 직렬화해 Clipboard로 복사한다.
    [RelayCommand]
    private void CopyConversation()
    {
        if (Messages.Count == 0)
        {
            ErrorMessage = "복사할 대화가 없다.";
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {ProjectName} — 대화 사본");
        sb.AppendLine($"- 내보낸 시각: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();

        foreach (var m in Messages)
        {
            string header = m.Role switch
            {
                "user" => "## 🧑 사용자",
                "assistant" => m.TurnIndex.HasValue
                    ? $"## 🐾 {m.PersonaLabel ?? "AI"} — 단계 {m.TurnIndex.Value + 1}"
                    : $"## 🐾 {m.PersonaLabel ?? "AI"}",
                "system" => "## ⚙ 시스템",
                "error" => "## ❌ 오류",
                "pm_report" => "## ✅ PM 종료 보고",
                "pm_intervention" => "## ⚠ PM 개입 요청",
                _ => $"## {m.Role}"
            };
            sb.AppendLine(header);
            sb.AppendLine($"_{m.Timestamp:yyyy-MM-dd HH:mm:ss}_");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(m.Content))
            {
                sb.AppendLine(m.Content);
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(m.HandoffToLabel))
            {
                sb.AppendLine($"↳ **{m.HandoffToLabel}** 에게 요청: {m.HandoffRequest}");
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(m.ModelUsed))
            {
                sb.AppendLine($"_모델: {m.ModelUsed}_");
                sb.AppendLine();
            }
            sb.AppendLine("---");
            sb.AppendLine();
        }

        try
        {
            System.Windows.Clipboard.SetText(sb.ToString());
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"클립보드 복사 실패: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenGoogleDocsPopup()
    {
        GoogleDocsStatusMessage = null;
        IsGoogleDocsPopupOpen = true;
    }

    [RelayCommand]
    private async Task ExportToGoogleDocsAsync()
    {
        var urlOrId = GoogleDocUrlInput?.Trim() ?? string.Empty;
        var docId = GoogleDocsService.ExtractDocId(urlOrId);
        if (string.IsNullOrWhiteSpace(docId))
        {
            GoogleDocsStatusMessage = "문서 URL 또는 ID를 입력하세요.";
            return;
        }
        if (Messages.Count == 0)
        {
            GoogleDocsStatusMessage = "내보낼 대화가 없습니다.";
            return;
        }

        IsGoogleDocsExporting = true;
        GoogleDocsStatusMessage = null;

        try
        {
            var accessToken = await _authService.GetGoogleAccessTokenAsync();
            if (string.IsNullOrEmpty(accessToken))
            {
                GoogleDocsStatusMessage = "Google 인증 토큰을 가져올 수 없습니다. 로그아웃 후 다시 로그인하세요.";
                return;
            }

            var content = BuildConversationText();
            var (success, error) = await _googleDocs.ExportAsync(docId, accessToken, content);
            if (!success)
            {
                GoogleDocsStatusMessage = error;
                return;
            }

            // 성공 시 doc ID 저장
            await SaveGoogleDocIdAsync(docId);
            GoogleDocsStatusMessage = "내보내기 완료!";
        }
        catch (Exception ex)
        {
            GoogleDocsStatusMessage = $"오류: {ex.Message}";
        }
        finally
        {
            IsGoogleDocsExporting = false;
        }
    }

    private async Task SaveGoogleDocIdAsync(string docId)
    {
        try
        {
            using var db = _dbFactory.CreateDbContext();
            var project = await db.Projects.FindAsync(ProjectId);
            if (project != null)
            {
                project.GoogleDocId = docId;
                project.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }
        }
        catch { }
    }

    private string BuildConversationText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {ProjectName} — 대화 사본");
        sb.AppendLine($"내보낸 시각: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();

        foreach (var m in Messages)
        {
            string header = m.Role switch
            {
                "user" => "## 사용자",
                "assistant" => $"## {m.PersonaLabel ?? "AI"}",
                "system" => "## 시스템",
                "error" => "## 오류",
                "pm_report" => "## PM 종료 보고",
                "pm_intervention" => "## PM 개입 요청",
                _ => $"## {m.Role}"
            };
            sb.AppendLine(header);
            sb.AppendLine($"{m.Timestamp:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(m.Content))
            {
                sb.AppendLine(m.Content);
                sb.AppendLine();
            }
            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // PM 보고서 REPORT.md를 SaveFileDialog로 다운로드한다.
    [RelayCommand]
    private void DownloadPmReport(ChatMessage msg)
    {
        if (msg == null) return;

        var sourcePath = msg.ReportPath;
        if (string.IsNullOrWhiteSpace(sourcePath) && !string.IsNullOrWhiteSpace(msg.OutputsFolder))
            sourcePath = System.IO.Path.Combine(msg.OutputsFolder!, "pm", "REPORT.md");

        if (string.IsNullOrWhiteSpace(sourcePath) || !System.IO.File.Exists(sourcePath))
        {
            ErrorMessage = "보고서 파일을 찾을 수 없다.";
            return;
        }

        try
        {
            var runTag = !string.IsNullOrWhiteSpace(msg.RunId)
                ? msg.RunId
                : DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var defaultName = $"REPORT_{runTag}.md";
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "PM 보고서 저장",
                Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*",
                FileName = defaultName,
                DefaultExt = ".md"
            };
            if (dlg.ShowDialog() != true) return;
            System.IO.File.Copy(sourcePath, dlg.FileName, overwrite: true);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"보고서 저장 실패: {ex.Message}";
        }
    }

    private List<ConversationTurn> BuildPriorConversation()
    {
        // 방금 추가된 user 메시지(마지막)는 제외하고 이전 대화만 수집
        var result = new List<ConversationTurn>();
        var source = Messages.Take(Messages.Count - 1)
            .Where(m => m.Role is "user" or "assistant" or "pm_report" or "pm_intervention")
            .ToList();

        foreach (var msg in source)
        {
            var role = msg.Role == "user" ? "user" : "assistant";
            var body = string.IsNullOrWhiteSpace(msg.Content) ? "(응답 없음)" : msg.Content;
            // assistant 메시지에는 페르소나 이름을 prefix해서 누가 말한 것인지 구분 가능하게 함
            var content = (role == "assistant" && !string.IsNullOrWhiteSpace(msg.PersonaLabel))
                ? $"[{msg.PersonaLabel}]\n{body}"
                : body;

            // Claude API는 user/assistant 교대를 강제하므로 연속 동일 Role은 하나로 합침
            if (result.Count > 0 && result[^1].Role == role)
                result[^1].Content = result[^1].Content + "\n\n" + content;
            else
                result.Add(new ConversationTurn { Role = role, Content = content });
        }

        // Claude API 요구: 반드시 user 턴으로 시작
        while (result.Count > 0 && result[0].Role != "user")
            result.RemoveAt(0);

        // 최대 12턴(6회 교환)으로 제한
        if (result.Count > 12)
            result = result.Skip(result.Count - 12).ToList();

        // Skip 후 다시 user로 시작하도록 보정
        while (result.Count > 0 && result[0].Role != "user")
            result.RemoveAt(0);

        return result;
    }

    private int IndexOfStreamKey(string streamKey)
    {
        for (int i = Messages.Count - 1; i >= 0; i--)
        {
            if (Messages[i].StreamKey == streamKey) return i;
        }
        return -1;
    }

    private ChatMessage BuildAssistantMessage(AgentTurn turn, bool previewOnly)
    {
        var p = Personas.FirstOrDefault(x => x.PersonaId == turn.PersonaId);

        string content;
        if (previewOnly)
        {
            content = PrependDiscussionMarker(turn.Content, turn);
        }
        else
        {
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(turn.Content))
                sb.Append(turn.Content);

            if (turn.ToolCalls.Count > 0)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                foreach (var t in turn.ToolCalls)
                {
                    var mark = t.Success ? "✓" : "✗";
                    sb.Append($"🔧 {mark} {t.Name}({t.ArgsSummary}) → {t.Result}\n");
                }
            }

            if (turn.WrittenFiles.Count > 0)
            {
                sb.Append("\n\n📎 생성·수정 파일:");
                foreach (var f in turn.WrittenFiles.Distinct())
                    sb.Append($"\n  - {f}");
            }

            content = PrependDiscussionMarker(sb.ToString().TrimEnd(), turn);
        }

        return new ChatMessage
        {
            Role = "assistant",
            Content = content,
            PersonaLabel = turn.PersonaLabel,
            PersonaAvatar = !string.IsNullOrWhiteSpace(p?.Avatar) ? p!.Avatar : turn.PersonaAvatar,
            PersonaIcon = p?.Icon,
            PersonaColor = p?.Color,
            ModelUsed = previewOnly ? null : turn.ModelUsed,
            Timestamp = DateTimeOffset.UtcNow,
            IsPm = turn.IsPm,
            IsPmGreeting = turn.IsPmGreeting,
            IsEndReport = turn.IsEndReport,
            IsUserIntervention = turn.IsUserIntervention,
            WrittenFiles = previewOnly ? [] : turn.WrittenFiles.Distinct().ToList(),
            TurnIndex = previewOnly ? null : turn.TurnIndex,
            HandoffToLabel = previewOnly ? null : turn.HandoffToLabel,
            HandoffRequest = previewOnly ? null : turn.HandoffRequest,
            DiscussionId = turn.DiscussionId,
            RoundIndex = turn.RoundIndex,
            Stance = turn.Stance,
            IsDiscussionSpeaker = turn.IsDiscussionSpeaker,
            IsDiscussionOpener = turn.IsDiscussionOpener,
            IsDiscussionSummary = turn.IsDiscussionSummary,
            DiscussionTopic = turn.DiscussionTopic,
            StreamKey = turn.StreamKey,
            IsStreaming = previewOnly
        };
    }

    // 토론 참여자·개시·정리 턴에 시각적 구분선을 달아 일반 대화와 혼동되지 않도록 한다
    private static string PrependDiscussionMarker(string content, AgentTurn turn)
        => BuildDiscussionMarker(
            content, turn.IsDiscussionOpener, turn.IsDiscussionSummary,
            turn.IsDiscussionSpeaker, turn.RoundIndex, turn.Stance, turn.DiscussionTopic);

    private static string ApplyDiscussionMarker(string content, ChatMessage msg)
        => BuildDiscussionMarker(
            content, msg.IsDiscussionOpener, msg.IsDiscussionSummary,
            msg.IsDiscussionSpeaker, msg.RoundIndex, msg.Stance, msg.DiscussionTopic);

    private static string BuildDiscussionMarker(
        string content, bool isOpener, bool isSummary, bool isSpeaker,
        int? roundIndex, string? stance, string? topic)
    {
        if (isOpener)
        {
            var topicTag = string.IsNullOrWhiteSpace(topic) ? "" : $" — {topic}";
            var header = $"💬 **다자 토론 개시**{topicTag}";
            return string.IsNullOrWhiteSpace(content) ? header : header + "\n\n" + content;
        }
        if (isSummary)
        {
            return string.IsNullOrWhiteSpace(content)
                ? "💬 **다자 토론 정리**"
                : "💬 **다자 토론 정리**\n\n" + content;
        }
        if (isSpeaker)
        {
            var round = (roundIndex ?? 0) + 1;
            var stanceLabel = stance switch
            {
                "agree" => "동의",
                "object" => "반대",
                "extend" => "보완",
                _ => stance ?? ""
            };
            var header = string.IsNullOrWhiteSpace(stanceLabel)
                ? $"💬 라운드 {round}"
                : $"💬 라운드 {round} · {stanceLabel}";
            return string.IsNullOrWhiteSpace(content) ? header : header + "\n\n" + content;
        }
        return content;
    }
}

public partial class ChatMessage : ObservableObject
{
    public string Role { get; set; } = string.Empty; // user, assistant, system, error, pm_report, pm_intervention

    // 스트리밍 중 실시간 갱신되는 본문 — ObservableProperty로 바인딩 재평가를 유발한다
    [ObservableProperty]
    private string _content = string.Empty;

    public string? PersonaLabel { get; set; }
    public string? PersonaAvatar { get; set; }
    public string? PersonaIcon { get; set; }
    public string? PersonaColor { get; set; }
    public string? ModelUsed { get; set; }
    public DateTimeOffset Timestamp { get; set; }

    // PM 허브 상태
    public bool IsPm { get; set; }
    public bool IsPmGreeting { get; set; }
    public bool IsEndReport { get; set; }
    public bool IsUserIntervention { get; set; }
    public List<string> WrittenFiles { get; set; } = [];
    public string? OutputsFolder { get; set; }
    public string? ReportPath { get; set; }
    public string? CommitSha { get; set; }
    public string? InterventionReason { get; set; }
    public string? InterventionQuestion { get; set; }
    public string? RunId { get; set; }

    // 에이전트 단계 모니터링
    public int? TurnIndex { get; set; }
    public string? HandoffToLabel { get; set; }
    public string? HandoffRequest { get; set; }
    public bool HasHandoff => !string.IsNullOrWhiteSpace(HandoffToLabel);
    public bool HasTurnIndex => TurnIndex.HasValue;
    public string TurnBadge => TurnIndex.HasValue ? $"단계 {TurnIndex.Value + 1}" : string.Empty;
    public bool HasReportPath => !string.IsNullOrWhiteSpace(ReportPath) || !string.IsNullOrWhiteSpace(OutputsFolder);

    // 다자 토론(round-table)
    public string? DiscussionId { get; set; }
    public int? RoundIndex { get; set; }
    public string? Stance { get; set; }
    public bool IsDiscussionSpeaker { get; set; }
    public bool IsDiscussionOpener { get; set; }
    public bool IsDiscussionSummary { get; set; }
    public string? DiscussionTopic { get; set; }
    public bool IsDiscussion => IsDiscussionSpeaker || IsDiscussionOpener || IsDiscussionSummary;

    // 스트리밍 턴 식별자 — 프리뷰 업데이트와 최종 치환을 매칭한다
    public string? StreamKey { get; set; }

    // 스트리밍 중 여부 — UI에서 "응답 생성 중..." 표시 및 렌더러 스위칭에 사용
    [ObservableProperty]
    private bool _isStreaming;

    // === 요약 / 상세 보기 ===
    private const int SummaryThreshold = 300;

    public bool IsLong => (Content?.Length ?? 0) > SummaryThreshold;

    public string SummaryContent
    {
        get
        {
            if (!IsLong) return Content ?? string.Empty;
            var text = Content![..SummaryThreshold];
            var lastBreak = text.LastIndexOfAny(['\n', '.', ' ']);
            return (lastBreak > 200 ? text[..lastBreak] : text) + "…";
        }
    }

    // 스트리밍 완료 + 짧은 내용 → 전체 마크다운
    public bool ShowMarkdown => !IsStreaming && !IsLong;

    // 스트리밍 완료 + 긴 내용 → 요약 텍스트 + 상세히 보기 버튼
    public bool ShowSummaryText => !IsStreaming && IsLong;

    partial void OnContentChanged(string value)
    {
        OnPropertyChanged(nameof(IsLong));
        OnPropertyChanged(nameof(SummaryContent));
        OnPropertyChanged(nameof(ShowMarkdown));
        OnPropertyChanged(nameof(ShowSummaryText));
    }

    partial void OnIsStreamingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowMarkdown));
        OnPropertyChanged(nameof(ShowSummaryText));
    }
}

public class ChatAttachment
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

public partial class TeamPersonaItem : ObservableObject
{
    public Persona Persona { get; set; } = null!;

    [ObservableProperty]
    private bool _isSelected;
}
