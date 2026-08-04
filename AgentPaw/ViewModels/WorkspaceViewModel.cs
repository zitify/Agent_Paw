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
    private readonly OrchestratorService _orchestrator;
    private readonly ConfigLoaderService _configLoader;
    private readonly IDbContextFactory<AgentPawDbContext> _dbFactory;
    private readonly AuthService _authService;
    private readonly GoogleDocsService _googleDocs;

    private CancellationTokenSource? _cts;

    // 응답 생성 타이머 (라이브 경과 표시 + 완료 시 총 소요 시간)
    private readonly System.Diagnostics.Stopwatch _genStopwatch = new();
    private System.Windows.Threading.DispatcherTimer? _genTimer;

    [ObservableProperty]
    private string _projectId = string.Empty;

    [ObservableProperty]
    private string _projectName = string.Empty;

    [ObservableProperty]
    private string _workspacePath = string.Empty;

    [ObservableProperty]
    private string _inputMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    // 응답 생성 중 라이브 경과 / 완료 후 총 소요 시간 ("⏱ 생성 중 2.3s" → "⏱ 완료 3.4s")
    [ObservableProperty]
    private string _generationTimer = string.Empty;

    // 타이머 배너 노출 여부 (첫 전송 이후 계속 표시되어 완료 시간을 남긴다)
    [ObservableProperty]
    private bool _showGenTimer;

    /// <summary>
    /// PM이 User에게 재질의(pm_intervention)를 할 수 있는지 여부.
    /// false면 PM은 스스로 판단하여 결정하고 절대 User에게 묻지 않는다.
    /// </summary>
    [ObservableProperty]
    private bool _askUserEnabled = true;

    [ObservableProperty]
    private string? _errorMessage;

    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public ObservableCollection<Persona> Personas { get; } = [];
    public ObservableCollection<ChatAttachment> Attachments { get; } = [];
    public ObservableCollection<Persona> MentionCandidates { get; } = [];

    // === 팀 모드 ===
    public ObservableCollection<TeamPersonaItem> TeamPickerItems { get; } = [];

    [ObservableProperty]
    private string _teamMode = "panel"; // panel | debate | chain

    public bool HasActiveTeam => TeamPickerItems.Count(x => x.IsSelected) >= 2;

    public string TeamModeLabel => TeamMode switch
    {
        "debate" => "토론",
        "chain" => "체인",
        _ => "패널"
    };

    public ControlAppearance TeamModePanelAppearance =>
        TeamMode == "panel" ? ControlAppearance.Primary : ControlAppearance.Secondary;
    public ControlAppearance TeamModeDebateAppearance =>
        TeamMode == "debate" ? ControlAppearance.Primary : ControlAppearance.Secondary;
    public ControlAppearance TeamModeChainAppearance =>
        TeamMode == "chain" ? ControlAppearance.Primary : ControlAppearance.Secondary;

    public bool HasAttachments => Attachments.Count > 0;

    [ObservableProperty]
    private bool _isMentionPopupOpen;

    [ObservableProperty]
    private int _selectedMentionIndex;

    // === Google Docs 내보내기 ===
    [ObservableProperty]
    private bool _isGoogleDocsPopupOpen;

    [ObservableProperty]
    private string _googleDocUrlInput = string.Empty;

    [ObservableProperty]
    private string? _googleDocsStatusMessage;

    [ObservableProperty]
    private bool _isGoogleDocsExporting;

    [ObservableProperty]
    private bool _isDetailOpen;

    [ObservableProperty]
    private ChatMessage? _detailMessage;

    [ObservableProperty]
    private string? _streamingPersonaId;

    [ObservableProperty]
    private string? _streamingPreview;

    public WorkspaceViewModel(
        OrchestratorService orchestrator,
        ConfigLoaderService configLoader,
        IDbContextFactory<AgentPawDbContext> dbFactory,
        AuthService authService,
        GoogleDocsService googleDocs)
    {
        _orchestrator = orchestrator;
        _configLoader = configLoader;
        _dbFactory = dbFactory;
        _authService = authService;
        _googleDocs = googleDocs;

        Attachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAttachments));

        TeamPickerItems.CollectionChanged += (_, _) =>
        {
            foreach (var item in TeamPickerItems)
                item.PropertyChanged -= OnTeamItemChanged;
            foreach (var item in TeamPickerItems)
                item.PropertyChanged += OnTeamItemChanged;
            RefreshTeamState();
        };
    }

    private void OnTeamItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TeamPersonaItem.IsSelected))
            RefreshTeamState();
    }

    private void RefreshTeamState()
    {
        OnPropertyChanged(nameof(HasActiveTeam));
    }

    partial void OnTeamModeChanged(string value)
    {
        OnPropertyChanged(nameof(TeamModeLabel));
        OnPropertyChanged(nameof(TeamModePanelAppearance));
        OnPropertyChanged(nameof(TeamModeDebateAppearance));
        OnPropertyChanged(nameof(TeamModeChainAppearance));
    }

    [RelayCommand]
    private void ClearTeam()
    {
        foreach (var item in TeamPickerItems)
            item.IsSelected = false;
        RefreshTeamState();
    }

    public async Task AddAttachmentsAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) continue;
            if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
            if (Attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

            try
            {
                var content = await System.IO.File.ReadAllTextAsync(path);
                Attachments.Add(new ChatAttachment
                {
                    Path = path,
                    Name = System.IO.Path.GetFileName(path),
                    Content = content
                });
            }
            catch (Exception ex)
            {
                ErrorMessage = $"첨부 실패: {System.IO.Path.GetFileName(path)} — {ex.Message}";
            }
        }
    }

    [RelayCommand]
    private void RemoveAttachment(ChatAttachment attachment)
    {
        if (attachment == null) return;
        Attachments.Remove(attachment);
    }

    // === @mention ===

    public void OpenMentionPopup(string filter = "")
    {
        UpdateMentionFilter(filter);
        IsMentionPopupOpen = MentionCandidates.Count > 0;
    }

    public void UpdateMentionFilter(string filter)
    {
        MentionCandidates.Clear();
        var matched = string.IsNullOrEmpty(filter)
            ? Personas
            : Personas.Where(p =>
                (p.Label?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                || (p.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        foreach (var p in matched) MentionCandidates.Add(p);

        if (MentionCandidates.Count == 0)
            IsMentionPopupOpen = false;
        else if (SelectedMentionIndex >= MentionCandidates.Count)
            SelectedMentionIndex = 0;
    }

    public void CloseMentionPopup()
    {
        IsMentionPopupOpen = false;
        MentionCandidates.Clear();
        SelectedMentionIndex = 0;
    }

    public void MoveMentionSelection(int delta)
    {
        if (MentionCandidates.Count == 0) return;
        var next = SelectedMentionIndex + delta;
        if (next < 0) next = MentionCandidates.Count - 1;
        else if (next >= MentionCandidates.Count) next = 0;
        SelectedMentionIndex = next;
    }

    public Persona? GetSelectedMentionPersona()
    {
        if (SelectedMentionIndex < 0 || SelectedMentionIndex >= MentionCandidates.Count) return null;
        return MentionCandidates[SelectedMentionIndex];
    }

    // AskUserEnabled 토글은 프로젝트 레코드에 즉시 persist 한다.
    partial void OnAskUserEnabledChanged(bool value)
    {
        if (string.IsNullOrWhiteSpace(ProjectId)) return;
        _ = PersistAskUserEnabledAsync(ProjectId, value);
    }

    private async Task PersistAskUserEnabledAsync(string projectId, bool value)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var project = await db.Projects.FindAsync(projectId);
            if (project == null || project.AskUserEnabled == value) return;
            project.AskUserEnabled = value;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        catch { /* 설정 저장 실패는 경고하지 않는다 — 다음 대화에 다시 시도 가능 */ }
    }

    // 메시지 본문에서 마지막 @<label|name>을 찾아 일치하는 Persona를 반환한다
    public Persona? ResolveMention(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"@([^\s@]+)");
        if (matches.Count == 0) return null;

        // 가장 마지막 멘션을 우선 매칭 — 여러 개면 마지막이 발화자의 최종 타겟으로 간주한다
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            var token = matches[i].Groups[1].Value;
            var matched = Personas.FirstOrDefault(p =>
                string.Equals(p.Label, token, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Name, token, StringComparison.OrdinalIgnoreCase));
            if (matched != null) return matched;
        }
        return null;
    }

    public async Task LoadWorkspaceAsync(string projectId, string projectName)
    {
        ProjectId = projectId;
        ProjectName = projectName;
        Messages.Clear();
        Personas.Clear();
        TeamPickerItems.Clear();
        ErrorMessage = null;

        // 작업 폴더 경로 resolve + 재질의 정책 로드
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var project = await db.Projects.FindAsync(projectId);
            WorkspacePath = !string.IsNullOrWhiteSpace(project?.GitRepoPath)
                ? project!.GitRepoPath
                : ProjectSettingsViewModel.DefaultWorkspacePath(projectId);
            AskUserEnabled = project?.AskUserEnabled ?? true;
            GoogleDocUrlInput = project?.GoogleDocId ?? string.Empty;
        }

        // 페르소나 로드 — Avatar가 비어있거나 렌더 불가한 SVG면 강아지 PNG로 폴백
        var personas = await _configLoader.ListPersonasAsync(projectId);
        foreach (var p in personas)
        {
            if (string.IsNullOrWhiteSpace(p.Avatar)
                || p.Avatar.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase))
            {
                p.Avatar = EngineAvatarService.ResolveAvatarForPersona(p.Name, p.Keywords, p.IsPm);
            }
            Personas.Add(p);
            TeamPickerItems.Add(new TeamPersonaItem { Persona = p });
        }

        // 이벤트 히스토리 로드
        await LoadHistoryAsync();
    }

    [RelayCommand]
    private void OpenWorkspaceFolder()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(WorkspacePath)) return;
            System.IO.Directory.CreateDirectory(WorkspacePath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = WorkspacePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

}
