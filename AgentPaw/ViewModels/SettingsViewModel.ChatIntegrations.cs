using CommunityToolkit.Mvvm.Input;
using AgentPaw.Services;

namespace AgentPaw.ViewModels;

public partial class SettingsViewModel
{
    // === Service Account Upload ===

    [RelayCommand]
    private async Task UploadServiceAccountAsync(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            await _chatBotConfigService.SetConfigAsync("SERVICE_ACCOUNT_JSON", json);
            HasServiceAccount = true;
            _googleChatService.ResetAuthClient();
            SuccessMessage = "서비스 계정이 업로드되었습니다.";
            BotConfigured = await _chatBotConfigService.IsBotConfiguredAsync();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    // === Space Links ===

    private async Task LoadSpaceLinksAsync()
    {
        var links = await _chatCommandService.ListLinksAsync("google");
        SpaceLinks.Clear();
        foreach (var l in links)
        {
            SpaceLinks.Add(new SpaceLinkItem
            {
                LinkId = l.LinkId,
                SpaceName = l.SpaceName,
                DisplayName = l.SpaceDisplayName,
                Enabled = l.Enabled
            });
        }
    }

    private async Task LoadSlackChannelLinksAsync()
    {
        var links = await _chatCommandService.ListLinksAsync("slack");
        SlackChannelLinks.Clear();
        foreach (var l in links)
        {
            SlackChannelLinks.Add(new SpaceLinkItem
            {
                LinkId = l.LinkId,
                SpaceName = l.SpaceName,
                DisplayName = l.SpaceDisplayName,
                Enabled = l.Enabled
            });
        }
    }

    [RelayCommand]
    private async Task RefreshSpacesAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var spaces = await _googleChatService.ListSpacesAsync();
            foreach (var space in spaces)
            {
                await _chatCommandService.UpsertSpaceLinkAsync(space.Name, space.DisplayName, false);
            }
            await LoadSpaceLinksAsync();
            SuccessMessage = $"{spaces.Count}개 Space를 새로고침했습니다.";
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task ToggleSpaceAsync(SpaceLinkItem link)
    {
        try
        {
            var newState = !link.Enabled;
            await _chatCommandService.SetLinkEnabledAsync(link.LinkId, newState);
            link.Enabled = newState;
            await LoadSpaceLinksAsync();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task DeleteSpaceAsync(SpaceLinkItem link)
    {
        try
        {
            await _chatCommandService.DeleteLinkAsync(link.LinkId);
            await LoadSpaceLinksAsync();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }
    // === Slack Bot ===

    [RelayCommand]
    private async Task SaveSlackBotTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        try
        {
            await _chatBotConfigService.SetConfigAsync("SLACK_BOT_TOKEN", token.Trim());
            HasSlackBotToken = true;
            _slackChatService.ResetClient();
            SlackBotConfigured = await _chatBotConfigService.IsSlackConfiguredAsync();
            SuccessMessage = "Slack Bot Token이 저장되었습니다.";
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task SaveSlackAppTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        try
        {
            await _chatBotConfigService.SetConfigAsync("SLACK_APP_TOKEN", token.Trim());
            HasSlackAppToken = true;
            SlackBotConfigured = await _chatBotConfigService.IsSlackConfiguredAsync();
            SuccessMessage = "Slack App Token이 저장되었습니다.";
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task DeleteSlackTokenAsync(string key)
    {
        try
        {
            await _chatBotConfigService.DeleteConfigAsync(key);
            if (key == "SLACK_BOT_TOKEN") { HasSlackBotToken = false; _slackChatService.ResetClient(); }
            else if (key == "SLACK_APP_TOKEN") HasSlackAppToken = false;
            SlackBotConfigured = await _chatBotConfigService.IsSlackConfiguredAsync();
            SuccessMessage = "Slack 토큰이 삭제되었습니다.";
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task ToggleSlackBotEnabledAsync()
    {
        try
        {
            var newState = !SlackBotEnabled;
            await _chatBotConfigService.SetConfigAsync("SLACK_BOT_ENABLED", newState ? "true" : "false");
            SlackBotEnabled = newState;

            if (newState && SlackBotConfigured)
            {
                await _slackSocketModeService.StartAsync();
                SlackBotRunning = _slackSocketModeService.IsRunning;
            }
            else
            {
                await _slackSocketModeService.StopAsync();
                SlackBotRunning = false;
            }
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task RestartSlackAsync()
    {
        try
        {
            await _slackSocketModeService.RestartAsync();
            SlackBotRunning = _slackSocketModeService.IsRunning;
            SuccessMessage = SlackBotRunning ? "Slack 재시작 완료" : "Slack 시작 실패";
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task RefreshSlackChannelsAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var channels = await _slackChatService.ListChannelsAsync();
            foreach (var ch in channels)
            {
                await _chatCommandService.UpsertSpaceLinkAsync(ch.ChannelId, $"#{ch.ChannelName}", false, "slack");
            }
            await LoadSlackChannelLinksAsync();
            SuccessMessage = $"{channels.Count}개 Slack 채널을 새로고침했습니다.";
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task ToggleSlackChannelAsync(SpaceLinkItem link)
    {
        try
        {
            var newState = !link.Enabled;
            await _chatCommandService.SetLinkEnabledAsync(link.LinkId, newState);
            link.Enabled = newState;
            await LoadSlackChannelLinksAsync();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task DeleteSlackChannelAsync(SpaceLinkItem link)
    {
        try
        {
            await _chatCommandService.DeleteLinkAsync(link.LinkId);
            await LoadSlackChannelLinksAsync();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    // === Telegram Bot ===

    private async Task LoadTelegramChatLinksAsync()
    {
        var links = await _chatCommandService.ListLinksAsync("telegram");
        TelegramChatLinks.Clear();
        foreach (var l in links)
        {
            TelegramChatLinks.Add(new SpaceLinkItem
            {
                LinkId = l.LinkId,
                SpaceName = l.SpaceName,
                DisplayName = l.SpaceDisplayName,
                Enabled = l.Enabled
            });
        }
    }

    [RelayCommand]
    private async Task SaveTelegramBotTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        try
        {
            await _chatBotConfigService.SetConfigAsync("TELEGRAM_BOT_TOKEN", token.Trim());
            _telegramChatService.ResetClient();
            HasTelegramBotToken = true;
            TelegramBotConfigured = await _chatBotConfigService.IsTelegramConfiguredAsync();

            // Bot 식별자(username) 검증·캐시
            try
            {
                var me = await _telegramChatService.GetMeAsync();
                TelegramBotUsername = me?.Username;
                SuccessMessage = me != null
                    ? $"Telegram Bot Token 저장 완료 — @{me.Username}"
                    : "Telegram Bot Token이 저장되었습니다.";
            }
            catch (Exception ex)
            {
                ErrorMessage = $"토큰은 저장되었지만 Telegram API 호출 실패: {ex.Message}";
            }
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task DeleteTelegramBotTokenAsync()
    {
        try
        {
            await _chatBotConfigService.DeleteConfigAsync("TELEGRAM_BOT_TOKEN");
            _telegramChatService.ResetClient();
            HasTelegramBotToken = false;
            TelegramBotUsername = null;
            TelegramBotConfigured = await _chatBotConfigService.IsTelegramConfiguredAsync();
            SuccessMessage = "Telegram Bot Token이 삭제되었습니다.";
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task ToggleTelegramBotEnabledAsync()
    {
        try
        {
            var newState = !TelegramBotEnabled;
            await _chatBotConfigService.SetConfigAsync("TELEGRAM_BOT_ENABLED", newState ? "true" : "false");
            TelegramBotEnabled = newState;

            if (newState && TelegramBotConfigured)
            {
                await _telegramPollingService.StartAsync();
                TelegramBotRunning = _telegramPollingService.IsRunning;
            }
            else
            {
                await _telegramPollingService.StopAsync();
                TelegramBotRunning = false;
            }
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task RestartTelegramAsync()
    {
        try
        {
            await _telegramPollingService.RestartAsync();
            TelegramBotRunning = _telegramPollingService.IsRunning;
            SuccessMessage = TelegramBotRunning ? "Telegram 재시작 완료" : "Telegram 시작 실패";
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task RefreshTelegramChatsAsync()
    {
        try
        {
            await LoadTelegramChatLinksAsync();
            SuccessMessage = $"{TelegramChatLinks.Count}개 Telegram 채팅을 불러왔습니다.";
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task ToggleTelegramChatAsync(SpaceLinkItem link)
    {
        try
        {
            var newState = !link.Enabled;
            await _chatCommandService.SetLinkEnabledAsync(link.LinkId, newState);
            link.Enabled = newState;
            await LoadTelegramChatLinksAsync();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task DeleteTelegramChatAsync(SpaceLinkItem link)
    {
        try
        {
            await _chatCommandService.DeleteLinkAsync(link.LinkId);
            await LoadTelegramChatLinksAsync();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }
}
