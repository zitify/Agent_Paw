using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using AgentPaw.Models;

namespace AgentPaw.ViewModels;

public partial class ProjectSettingsViewModel
{
    // === Instruction Linking ===

    [RelayCommand]
    private async Task OpenLinkDialogAsync()
    {
        try
        {
            var allFiles = await _instructionService.ListFilesAsync();
            var linkedIds = LinkedInstructions.Select(l => l.FileId).ToHashSet();
            var available = allFiles
                .Where(f => !linkedIds.Contains(f.FileId))
                .Select(f => new InstructionFileItem { FileId = f.FileId, Name = f.Name, GroupId = f.GroupId })
                .ToList();

            AvailableInstructions.Clear();
            foreach (var f in available) AvailableInstructions.Add(f);

            // 그룹별 묶음 구성
            await using var db = await _dbFactory.CreateDbContextAsync();
            var groups = await db.InstructionGroups.OrderBy(g => g.Name).ToListAsync();
            var byGroup = available.ToLookup(f => f.GroupId ?? "");

            AvailableInstructionGroups.Clear();
            foreach (var g in groups)
            {
                var items = byGroup[g.GroupId].ToList();
                if (items.Count == 0) continue;
                var bucket = new AvailableInstructionGroup { GroupId = g.GroupId, Name = g.Name };
                foreach (var f in items) bucket.Files.Add(f);
                AvailableInstructionGroups.Add(bucket);
            }
            // 미분류 묶음
            var ungrouped = byGroup[""].ToList();
            if (ungrouped.Count > 0)
            {
                var bucket = new AvailableInstructionGroup { GroupId = string.Empty, Name = "미분류" };
                foreach (var f in ungrouped) bucket.Files.Add(f);
                AvailableInstructionGroups.Add(bucket);
            }

            IsLinkDialogOpen = true;
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task LinkInstructionGroupAsync(AvailableInstructionGroup group)
    {
        if (string.IsNullOrEmpty(ProjectId) || group == null) return;
        try
        {
            foreach (var f in group.Files.ToList())
                await _instructionService.LinkToProjectAsync(ProjectId, f.FileId);

            await RefreshAsync();

            AvailableInstructionGroups.Remove(group);
            foreach (var f in group.Files)
            {
                var item = AvailableInstructions.FirstOrDefault(a => a.FileId == f.FileId);
                if (item != null) AvailableInstructions.Remove(item);
            }
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private void CloseLinkDialog()
    {
        IsLinkDialogOpen = false;
    }

    [RelayCommand]
    private async Task LinkInstructionAsync(InstructionFileItem file)
    {
        try
        {
            await _instructionService.LinkToProjectAsync(ProjectId, file.FileId);
            await RefreshAsync();

            var item = AvailableInstructions.FirstOrDefault(a => a.FileId == file.FileId);
            if (item != null) AvailableInstructions.Remove(item);

            foreach (var bucket in AvailableInstructionGroups.ToList())
            {
                var f = bucket.Files.FirstOrDefault(x => x.FileId == file.FileId);
                if (f != null) bucket.Files.Remove(f);
                if (bucket.Files.Count == 0)
                    AvailableInstructionGroups.Remove(bucket);
            }
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task UnlinkInstructionAsync(InstructionFileItem file)
    {
        try
        {
            await _instructionService.UnlinkFromProjectAsync(ProjectId, file.FileId);
            await RefreshAsync();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task UnlinkInstructionGroupAsync(AvailableInstructionGroup group)
    {
        if (string.IsNullOrEmpty(ProjectId) || group == null) return;
        try
        {
            foreach (var f in group.Files.ToList())
                await _instructionService.UnlinkFromProjectAsync(ProjectId, f.FileId);
            await RefreshAsync();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    // === Persona Linking (전역 페르소나 → 현 프로젝트 연결) ===

    [RelayCommand]
    private async Task OpenPersonaLinkDialogAsync()
    {
        if (string.IsNullOrEmpty(ProjectId)) return;
        try
        {
            var allGlobals = await _personaService.ListGlobalAsync();
            var linkedIds = Personas.Select(p => p.PersonaId).ToHashSet();
            var available = allGlobals.Where(p => !linkedIds.Contains(p.PersonaId)).ToList();

            AvailablePersonas.Clear();
            foreach (var p in available)
                AvailablePersonas.Add(p);

            // 그룹별 묶음 구성 (그룹 일괄 연결용)
            await using var db = await _dbFactory.CreateDbContextAsync();
            var groups = await db.PersonaGroups
                .Where(g => g.ProjectId == null)
                .OrderBy(g => g.SortOrder)
                .ToListAsync();
            var byGroup = available.ToLookup(p => p.GroupId ?? "");

            AvailablePersonaGroups.Clear();
            foreach (var g in groups)
            {
                var items = byGroup[g.GroupId].ToList();
                if (items.Count == 0) continue;
                var bucket = new AvailablePersonaGroup { GroupId = g.GroupId, Name = g.Name };
                foreach (var p in items) bucket.Personas.Add(p);
                AvailablePersonaGroups.Add(bucket);
            }
            // 미분류 묶음
            var ungrouped = byGroup[""].ToList();
            if (ungrouped.Count > 0)
            {
                var bucket = new AvailablePersonaGroup { GroupId = string.Empty, Name = "미분류" };
                foreach (var p in ungrouped) bucket.Personas.Add(p);
                AvailablePersonaGroups.Add(bucket);
            }

            IsPersonaLinkDialogOpen = true;
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task LinkPersonaGroupAsync(AvailablePersonaGroup group)
    {
        if (string.IsNullOrEmpty(ProjectId) || group == null) return;
        try
        {
            foreach (var p in group.Personas.ToList())
                await _personaService.LinkToProjectAsync(ProjectId, p.PersonaId);

            await RefreshAsync();

            // 다이얼로그 컬렉션에서 묶음 제거
            AvailablePersonaGroups.Remove(group);
            foreach (var p in group.Personas)
            {
                var item = AvailablePersonas.FirstOrDefault(a => a.PersonaId == p.PersonaId);
                if (item != null) AvailablePersonas.Remove(item);
            }
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private void ClosePersonaLinkDialog()
    {
        IsPersonaLinkDialogOpen = false;
    }

    [RelayCommand]
    private async Task LinkPersonaAsync(Persona persona)
    {
        if (string.IsNullOrEmpty(ProjectId)) return;
        try
        {
            await _personaService.LinkToProjectAsync(ProjectId, persona.PersonaId);
            await RefreshAsync();

            var item = AvailablePersonas.FirstOrDefault(a => a.PersonaId == persona.PersonaId);
            if (item != null) AvailablePersonas.Remove(item);

            foreach (var bucket in AvailablePersonaGroups.ToList())
            {
                var p = bucket.Personas.FirstOrDefault(x => x.PersonaId == persona.PersonaId);
                if (p != null) bucket.Personas.Remove(p);
                if (bucket.Personas.Count == 0)
                    AvailablePersonaGroups.Remove(bucket);
            }
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task UnlinkPersonaAsync(Persona persona)
    {
        if (string.IsNullOrEmpty(ProjectId)) return;
        try
        {
            await _personaService.UnlinkFromProjectAsync(ProjectId, persona.PersonaId);
            await RefreshAsync();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task UnlinkPersonaGroupAsync(PersonaGroupItem group)
    {
        if (string.IsNullOrEmpty(ProjectId) || group == null) return;
        try
        {
            foreach (var p in group.Personas.ToList())
                await _personaService.UnlinkFromProjectAsync(ProjectId, p.PersonaId);
            await RefreshAsync();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    // === Persona-Instruction Linking ===

    [RelayCommand]
    private async Task OpenPersonaInstructionLinkDialogAsync()
    {
        if (string.IsNullOrEmpty(EditingPersonaId)) return;
        try
        {
            var allFiles = await _instructionService.ListFilesAsync();
            var linkedIds = PersonaLinkedInstructions.Select(l => l.FileId).ToHashSet();
            var available = allFiles
                .Where(f => !linkedIds.Contains(f.FileId))
                .Select(f => new InstructionFileItem { FileId = f.FileId, Name = f.Name, GroupId = f.GroupId })
                .ToList();

            PersonaAvailableInstructions.Clear();
            foreach (var f in available) PersonaAvailableInstructions.Add(f);

            await using var db = await _dbFactory.CreateDbContextAsync();
            var groups = await db.InstructionGroups.OrderBy(g => g.Name).ToListAsync();
            var byGroup = available.ToLookup(f => f.GroupId ?? "");

            PersonaAvailableInstructionGroups.Clear();
            foreach (var g in groups)
            {
                var items = byGroup[g.GroupId].ToList();
                if (items.Count == 0) continue;
                var bucket = new AvailableInstructionGroup { GroupId = g.GroupId, Name = g.Name };
                foreach (var f in items) bucket.Files.Add(f);
                PersonaAvailableInstructionGroups.Add(bucket);
            }
            var ungrouped = byGroup[""].ToList();
            if (ungrouped.Count > 0)
            {
                var bucket = new AvailableInstructionGroup { GroupId = string.Empty, Name = "미분류" };
                foreach (var f in ungrouped) bucket.Files.Add(f);
                PersonaAvailableInstructionGroups.Add(bucket);
            }

            IsPersonaInstructionLinkDialogOpen = true;
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private void ClosePersonaInstructionLinkDialog()
    {
        IsPersonaInstructionLinkDialogOpen = false;
    }

    [RelayCommand]
    private async Task LinkInstructionToPersonaAsync(InstructionFileItem file)
    {
        if (string.IsNullOrEmpty(EditingPersonaId) || file == null) return;
        try
        {
            await _instructionService.LinkToPersonaAsync(EditingPersonaId, file.FileId);
            await LoadPersonaLinkedInstructionsAsync(EditingPersonaId);

            var item = PersonaAvailableInstructions.FirstOrDefault(a => a.FileId == file.FileId);
            if (item != null) PersonaAvailableInstructions.Remove(item);

            foreach (var bucket in PersonaAvailableInstructionGroups.ToList())
            {
                var f = bucket.Files.FirstOrDefault(x => x.FileId == file.FileId);
                if (f != null) bucket.Files.Remove(f);
                if (bucket.Files.Count == 0)
                    PersonaAvailableInstructionGroups.Remove(bucket);
            }
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task LinkInstructionGroupToPersonaAsync(AvailableInstructionGroup group)
    {
        if (string.IsNullOrEmpty(EditingPersonaId) || group == null) return;
        try
        {
            foreach (var f in group.Files.ToList())
                await _instructionService.LinkToPersonaAsync(EditingPersonaId, f.FileId);

            await LoadPersonaLinkedInstructionsAsync(EditingPersonaId);

            PersonaAvailableInstructionGroups.Remove(group);
            foreach (var f in group.Files)
            {
                var item = PersonaAvailableInstructions.FirstOrDefault(a => a.FileId == f.FileId);
                if (item != null) PersonaAvailableInstructions.Remove(item);
            }
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task UnlinkInstructionFromPersonaAsync(InstructionFileItem file)
    {
        if (string.IsNullOrEmpty(EditingPersonaId) || file == null) return;
        try
        {
            await _instructionService.UnlinkFromPersonaAsync(EditingPersonaId, file.FileId);
            await LoadPersonaLinkedInstructionsAsync(EditingPersonaId);
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

}
