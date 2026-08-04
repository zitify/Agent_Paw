using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AgentPaw.Orchestrator;

namespace AgentPaw.Services;

public partial class MobileApiService
{
    private async Task HandleMeAsync(NetworkStream stream)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(_devUserId);
        if (user == null) { await WriteJsonAsync(stream, 404, new { error = "User not found" }); return; }
        await WriteJsonAsync(stream, 200, new { userId = user.UserId, email = user.Email, displayName = user.DisplayName, profileImageUrl = user.ProfileImageUrl });
    }

    private async Task HandleListProjectsAsync(NetworkStream stream)
    {
        var projects = await _projectService.ListProjectsForUserAsync(_devUserId);
        await WriteJsonAsync(stream, 200, projects.Select(p => new { projectId = p.ProjectId, projectName = p.ProjectName, description = p.Description, status = p.Status, createdAt = p.CreatedAt }));
    }

    private async Task HandleCreateProjectAsync(NetworkStream stream, HttpReq req)
    {
        var text = Encoding.UTF8.GetString(req.Body);
        using var doc = JsonDocument.Parse(text.Length > 0 ? text : "{}");
        var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var desc = doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) { await WriteJsonAsync(stream, 400, new { error = "name is required" }); return; }
        var project = await _projectService.CreateProjectAsync(_devUserId, name, desc);
        await WriteJsonAsync(stream, 201, new { projectId = project.ProjectId, projectName = project.ProjectName, description = project.Description, status = project.Status, createdAt = project.CreatedAt });
    }

    private async Task HandleGetProjectAsync(NetworkStream stream, string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var project = await db.Projects.FindAsync(projectId);
        if (project == null) { await WriteJsonAsync(stream, 404, new { error = "Project not found" }); return; }
        await WriteJsonAsync(stream, 200, new { projectId = project.ProjectId, projectName = project.ProjectName, description = project.Description, gitRepoPath = project.GitRepoPath, askUserEnabled = project.AskUserEnabled, maxDiscussionRounds = project.MaxDiscussionRounds, maxDiscussionParticipants = project.MaxDiscussionParticipants, googleDocId = project.GoogleDocId, status = project.Status, createdAt = project.CreatedAt, updatedAt = project.UpdatedAt });
    }

    private async Task HandlePatchProjectSettingsAsync(NetworkStream stream, HttpReq req, string projectId)
    {
        var text = Encoding.UTF8.GetString(req.Body);
        using var doc = JsonDocument.Parse(text.Length > 0 ? text : "{}");
        await using var db = await _dbFactory.CreateDbContextAsync();
        var project = await db.Projects.FindAsync(projectId);
        if (project == null) { await WriteJsonAsync(stream, 404, new { error = "Project not found" }); return; }
        if (doc.RootElement.TryGetProperty("askUserEnabled", out var a) && (a.ValueKind == JsonValueKind.True || a.ValueKind == JsonValueKind.False)) project.AskUserEnabled = a.GetBoolean();
        if (doc.RootElement.TryGetProperty("maxDiscussionRounds", out var r) && r.ValueKind == JsonValueKind.Number) project.MaxDiscussionRounds = Math.Clamp(r.GetInt32(), 1, 50);
        if (doc.RootElement.TryGetProperty("maxDiscussionParticipants", out var p) && p.ValueKind == JsonValueKind.Number) project.MaxDiscussionParticipants = Math.Clamp(p.GetInt32(), 2, 10);
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await WriteJsonAsync(stream, 200, new { projectId = project.ProjectId, askUserEnabled = project.AskUserEnabled, maxDiscussionRounds = project.MaxDiscussionRounds, maxDiscussionParticipants = project.MaxDiscussionParticipants });
    }

    private async Task HandleMessagesAsync(NetworkStream stream, HttpReq req, string projectId)
    {
        var q = ParseQuery(req.QueryString);
        var limit = int.TryParse(q.GetValueOrDefault("limit"), out var l) ? Math.Clamp(l, 1, 200) : 50;
        var before = q.GetValueOrDefault("before") ?? "";
        await using var db = await _dbFactory.CreateDbContextAsync();
        var query = db.EventLogs.AsNoTracking().Where(e => e.ProjectId == projectId && !e.IsDeleted && (e.EventType == "USER_MESSAGE" || e.EventType == "AI_RESPONSE" || e.EventType == "PM_RESPONSE" || e.EventType == "PM_REPORT" || e.EventType == "PM_INTERVENTION"));
        if (!string.IsNullOrEmpty(before)) { var pivot = await db.EventLogs.AsNoTracking().Where(e => e.EventId == before).Select(e => e.CreatedAt).FirstOrDefaultAsync(); if (pivot != default) query = query.Where(e => e.CreatedAt < pivot); }
        var events = await query.OrderByDescending(e => e.CreatedAt).Take(limit).OrderBy(e => e.CreatedAt).ToListAsync();
        await WriteJsonAsync(stream, 200, events.Select(e => new { eventId = e.EventId, eventType = e.EventType, payload = e.Payload, modelUsed = e.ModelUsed, createdAt = e.CreatedAt }));
    }

    private async Task HandleChatAsync(NetworkStream stream, HttpReq req, string projectId)
    {
        var text = Encoding.UTF8.GetString(req.Body);
        using var doc = JsonDocument.Parse(text.Length > 0 ? text : "{}");
        var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(message)) { await WriteJsonAsync(stream, 400, new { error = "message is required" }); return; }
        List<string>? teamIds = null;
        if (doc.RootElement.TryGetProperty("teamIds", out var ti) && ti.ValueKind == JsonValueKind.Array) teamIds = ti.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
        string? teamMode = doc.RootElement.TryGetProperty("teamMode", out var tm) ? tm.GetString() : null;
        string? forcePersonaId = doc.RootElement.TryGetProperty("forcePersonaId", out var fp) ? fp.GetString() : null;
        var turns = new List<object>();
        var input = new OrchestratorInput { ProjectId = projectId, UserId = _devUserId, Message = message, ForcePersonaId = forcePersonaId, TeamPersonaIds = teamIds?.Count >= 2 ? teamIds : null, TeamMode = teamMode ?? "panel" };
        var progress = new Progress<AgentTurn>(turn => { if (!turn.IsStreamingPreview) turns.Add(new { personaId = turn.PersonaId, personaLabel = turn.PersonaLabel, personaAvatar = turn.PersonaAvatar, content = turn.Content, modelUsed = turn.ModelUsed, isPm = turn.IsPm, turnIndex = turn.TurnIndex, isStreamingPreview = false }); });
        var result = await _orchestrator.RunPipelineAsync(input, progress);
        await WriteJsonAsync(stream, 200, new { eventId = result.EventId, personaId = result.PersonaId, personaLabel = result.PersonaLabel, content = result.Content, turns });
    }

    private async Task HandlePersonasAsync(NetworkStream stream, string projectId)
    {
        var personas = await _configLoader.ListPersonasAsync(projectId);
        await WriteJsonAsync(stream, 200, personas.Select(p => new { personaId = p.PersonaId, name = p.Name, label = p.Label, description = p.Description, avatar = p.Avatar, icon = p.Icon, color = p.Color, isPm = p.IsPm, primaryModel = p.PrimaryModel, fallbackModel = p.FallbackModel, temperature = p.Temperature, maxTokens = p.MaxTokens }));
    }

    private async Task HandlePatchPersonaModelAsync(NetworkStream stream, HttpReq req, string projectId, string personaId)
    {
        var text = Encoding.UTF8.GetString(req.Body);
        using var doc = JsonDocument.Parse(text.Length > 0 ? text : "{}");
        await using var db = await _dbFactory.CreateDbContextAsync();
        var persona = await db.Personas.FindAsync(personaId);
        if (persona == null) { await WriteJsonAsync(stream, 404, new { error = "Persona not found" }); return; }
        if (doc.RootElement.TryGetProperty("primaryModel", out var pm) && pm.ValueKind == JsonValueKind.String) { var v = pm.GetString()?.Trim(); if (!string.IsNullOrEmpty(v)) persona.PrimaryModel = v; }
        if (doc.RootElement.TryGetProperty("fallbackModel", out var fm)) persona.FallbackModel = fm.ValueKind == JsonValueKind.String ? fm.GetString()?.Trim() : null;
        if (doc.RootElement.TryGetProperty("temperature", out var t) && t.ValueKind == JsonValueKind.Number) persona.Temperature = Math.Clamp((float)t.GetDouble(), 0f, 1f);
        if (doc.RootElement.TryGetProperty("maxTokens", out var mt) && mt.ValueKind == JsonValueKind.Number) persona.MaxTokens = Math.Clamp(mt.GetInt32(), 256, 32768);
        persona.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        _configLoader.InvalidateAll();
        await WriteJsonAsync(stream, 200, new { personaId = persona.PersonaId, primaryModel = persona.PrimaryModel, fallbackModel = persona.FallbackModel, temperature = persona.Temperature, maxTokens = persona.MaxTokens });
    }

    private async Task HandleWikiListAsync(NetworkStream stream, string projectId)
    {
        var wikis = await _wikiService.ListWikisAsync(projectId);
        await WriteJsonAsync(stream, 200, wikis.Select(w => new { wikiId = w.WikiId, category = w.Category, title = w.Title, version = w.Version, updatedAt = w.UpdatedAt }));
    }

    private async Task HandleWikiDetailAsync(NetworkStream stream, string projectId, string wikiId)
    {
        var wiki = await _wikiService.GetWikiAsync(wikiId);
        if (wiki == null || wiki.ProjectId != projectId) { await WriteJsonAsync(stream, 404, new { error = "Not found" }); return; }
        await WriteJsonAsync(stream, 200, new { wikiId = wiki.WikiId, category = wiki.Category, title = wiki.Title, content = wiki.Content, version = wiki.Version, sourceEventId = wiki.SourceEventId, createdAt = wiki.CreatedAt, updatedAt = wiki.UpdatedAt });
    }

    private async Task HandleWikiConsolidateAsync(NetworkStream stream, string projectId)
    {
        var created = await _orchestrator.ConsolidateWikiAsync(projectId);
        await WriteJsonAsync(stream, 200, created.Select(w => new { wikiId = w.WikiId, category = w.Category, title = w.Title, version = w.Version, updatedAt = w.UpdatedAt }));
    }

    private async Task HandleWikiUpdateAsync(NetworkStream stream, HttpReq req, string projectId, string wikiId)
    {
        var text = Encoding.UTF8.GetString(req.Body);
        using var doc = JsonDocument.Parse(text.Length > 0 ? text : "{}");
        var wiki = await _wikiService.GetWikiAsync(wikiId);
        if (wiki == null || wiki.ProjectId != projectId) { await WriteJsonAsync(stream, 404, new { error = "Not found" }); return; }
        string? title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
        string? content = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() : null;
        string? category = doc.RootElement.TryGetProperty("category", out var ca) ? ca.GetString() : null;
        await _wikiService.UpdateWikiAsync(wikiId, title, content, category);
        var updated = await _wikiService.GetWikiAsync(wikiId);
        await WriteJsonAsync(stream, 200, new { wikiId = updated!.WikiId, category = updated.Category, title = updated.Title, content = updated.Content, version = updated.Version, updatedAt = updated.UpdatedAt });
    }

    private async Task HandleWikiDeleteAsync(NetworkStream stream, string projectId, string wikiId)
    {
        var wiki = await _wikiService.GetWikiAsync(wikiId);
        if (wiki == null || wiki.ProjectId != projectId) { await WriteJsonAsync(stream, 404, new { error = "Not found" }); return; }
        await _wikiService.DeleteWikiAsync(wikiId);
        await WriteJsonAsync(stream, 200, new { deleted = true });
    }

    private async Task HandleTimelineAsync(NetworkStream stream, HttpReq req, string projectId)
    {
        var q = ParseQuery(req.QueryString);
        var limit = int.TryParse(q.GetValueOrDefault("limit"), out var l) ? Math.Clamp(l, 1, 100) : 30;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var events = await db.EventLogs.AsNoTracking().Where(e => e.ProjectId == projectId && !e.IsDeleted).OrderByDescending(e => e.CreatedAt).Take(limit).OrderBy(e => e.CreatedAt).ToListAsync();
        await WriteJsonAsync(stream, 200, events.Select(e => new { eventId = e.EventId, eventType = e.EventType, modelUsed = e.ModelUsed, triggeredBy = e.TriggeredBy, createdAt = e.CreatedAt }));
    }
}
