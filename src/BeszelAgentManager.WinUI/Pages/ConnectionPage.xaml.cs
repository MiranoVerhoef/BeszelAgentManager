using BeszelAgentManager.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace BeszelAgentManager.WinUI.Pages;

public sealed partial class ConnectionPage : Page
{
    private readonly ConfigService _configService = new();
    private readonly AutostartService _autostartService = new();
    private AgentConfig _config = new();
    private bool _loading = true;
    private bool _autostartEnabled;
    private bool _autostartHidden;

    public ConnectionPage()
    {
        InitializeComponent();
        Loaded += ConnectionPage_Loaded;
    }

    private async void ConnectionPage_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        _config = await _configService.LoadAsync();
        PopulateConfig(_config);
        _loading = false;
    }

    private void PopulateConfig(AgentConfig config)
    {
        KeyTextBox.Text = config.Key;
        TokenBox.Password = config.Token;
        HubUrlTextBox.Text = config.HubUrl;
        FallbackUrlTextBox.Text = config.HubUrlIpFallback;
        FallbackToggleButton.Content = config.HubUrlIpFallbackEnabled ? "Disable" : "Enable";

        if (config.Listen is int listen)
        {
            PortNumberBox.Value = listen;
            ListenToggleButton.Content = "Disable";
        }
        else
        {
            PortNumberBox.Value = double.NaN;
            ListenToggleButton.Content = "Enable";
        }

        AutoUpdateCheckBox.IsChecked = config.AutoUpdateEnabled;
        UpdateIntervalTextBox.Text = NormalizeHours(config.UpdateIntervalHours).ToString();
        var autostart = _autostartService.GetState();
        _autostartEnabled = autostart.Enabled;
        _autostartHidden = autostart.StartHidden;
        AutostartCheckBox.IsChecked = autostart.Enabled;
        StartVisibleCheckBox.IsChecked = autostart.Enabled && !autostart.StartHidden;
    }

    internal async Task<bool> SaveCurrentConfigAsync(bool logNoChanges = true)
    {
        var before = Snapshot(_config, _autostartEnabled);
        var persisted = await _configService.LoadAsync();
        UpdateConfigFromControls();
        _config.LastAppliedFingerprint = persisted.LastAppliedFingerprint;
        _config.LastAppliedAt = persisted.LastAppliedAt;
        _config.LastAppliedManagerTasksFingerprint = persisted.LastAppliedManagerTasksFingerprint;
        await _configService.SaveAsync(_config);
        return LogConnectionChanges(before, Snapshot(_config, _autostartEnabled), logNoChanges);
    }

    private void UpdateConfigFromControls()
    {
        _config.Key = KeyTextBox.Text.Trim();
        _config.Token = TokenBox.Password.Trim();
        _config.HubUrl = HubUrlTextBox.Text.Trim();
        _config.HubUrlIpFallback = FallbackUrlTextBox.Text.Trim();
        _config.HubUrlIpFallbackEnabled = IsToggleEnabled(FallbackToggleButton);
        _config.Listen = IsToggleEnabled(ListenToggleButton) && !double.IsNaN(PortNumberBox.Value)
            ? (int)PortNumberBox.Value
            : null;
        _config.AutoUpdateEnabled = AutoUpdateCheckBox.IsChecked == true;
        _config.UpdateIntervalHours = NormalizeHours(UpdateIntervalTextBox.Text);
        _config.StartHidden = StartVisibleCheckBox.IsChecked != true;
        var autostartEnabled = AutostartCheckBox.IsChecked == true;
        if (autostartEnabled != _autostartEnabled || _config.StartHidden != _autostartHidden)
        {
            _autostartService.SetState(autostartEnabled, _config.StartHidden);
            _autostartEnabled = autostartEnabled;
            _autostartHidden = _config.StartHidden;
        }
    }

    private async Task SaveControlChangeAsync(string description)
    {
        if (_loading)
        {
            return;
        }

        if (description == "Automatic update interval" && !TryGetUpdateInterval(out _))
        {
            ShowServiceStatus(
                InfoBarSeverity.Warning,
                "Invalid update interval",
                "Enter a whole number from 1 to 720 hours. 720 hours is 30 days.");
            return;
        }

        try
        {
            var changed = await SaveCurrentConfigAsync();
            App.Logger.Debug($"{description} changed");
            if (changed)
            {
                ShowSavedStatus(description);
            }
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Could not save {description}: {ex}");
            ShowServiceStatus(InfoBarSeverity.Error, "Could not save settings", ex.Message);
        }
    }

    private static bool IsToggleEnabled(Button button)
    {
        return string.Equals(button.Content?.ToString(), "Disable", StringComparison.OrdinalIgnoreCase);
    }

    private static int NormalizeHours(int value)
    {
        return Math.Clamp(value, 1, 720);
    }

    private static int NormalizeHours(string text)
    {
        return int.TryParse(text.Trim(), out var parsed)
            ? NormalizeHours(parsed)
            : 24;
    }

    private async void KeyChangeButton_Click(object sender, RoutedEventArgs e)
    {
        await ToggleTextEditAsync(KeyTextBox, KeyChangeButton, "Key");
    }

    private async void TokenChangeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TokenBox.IsEnabled)
        {
            TokenBox.IsEnabled = true;
            TokenChangeButton.Content = "Save";
            TokenBox.Focus(FocusState.Programmatic);
            return;
        }

        TokenBox.IsEnabled = false;
        TokenChangeButton.Content = "Change";
        await SaveControlChangeAsync("Token");
    }

    private async void HubUrlChangeButton_Click(object sender, RoutedEventArgs e)
    {
        await ToggleTextEditAsync(HubUrlTextBox, HubUrlChangeButton, "Hub URL");
    }

    private async void FallbackChangeButton_Click(object sender, RoutedEventArgs e)
    {
        await ToggleTextEditAsync(FallbackUrlTextBox, FallbackChangeButton, "Hub URL IP fallback");
    }

    private async void PortChangeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!PortNumberBox.IsEnabled)
        {
            PortNumberBox.IsEnabled = true;
            PortChangeButton.Content = "Save";
            PortNumberBox.Focus(FocusState.Programmatic);
            return;
        }

        PortNumberBox.IsEnabled = false;
        PortChangeButton.Content = "Change";
        await SaveControlChangeAsync("Listen port");
    }

    private async Task ToggleTextEditAsync(TextBox textBox, Button button, string description)
    {
        if (textBox.IsReadOnly)
        {
            textBox.IsReadOnly = false;
            button.Content = "Save";
            textBox.Focus(FocusState.Programmatic);
            textBox.SelectionStart = textBox.Text.Length;
            return;
        }

        textBox.IsReadOnly = true;
        button.Content = "Change";
        await SaveControlChangeAsync(description);
    }

    private async void FallbackToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var enable = !IsToggleEnabled(FallbackToggleButton);
        if (enable && string.IsNullOrWhiteSpace(FallbackUrlTextBox.Text))
        {
            ShowServiceStatus(
                InfoBarSeverity.Warning,
                "Fallback URL missing",
                "Enter a Hub URL IP fallback before enabling this option.");
            return;
        }

        FallbackToggleButton.Content = IsToggleEnabled(FallbackToggleButton) ? "Enable" : "Disable";
        await SaveControlChangeAsync("Hub URL IP fallback state");
        ShowServiceStatus(
            InfoBarSeverity.Informational,
            enable ? "Fallback enabled" : "Fallback disabled",
            "Apply settings when you are ready to restart the service with this change.");
    }

    private async void ListenToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var disabling = IsToggleEnabled(ListenToggleButton);
        if (disabling)
        {
            PortNumberBox.Value = double.NaN;
            ListenToggleButton.Content = "Enable";
        }
        else
        {
            PortNumberBox.Value = double.IsNaN(PortNumberBox.Value) ? 45876 : PortNumberBox.Value;
            ListenToggleButton.Content = "Disable";
        }

        await SaveControlChangeAsync("Listen port state");
        await ApplyListenChangeAsync(disabling);
    }

    private async void SettingControl_Changed(object sender, RoutedEventArgs e)
    {
        var description = ReferenceEquals(sender, AutoUpdateCheckBox)
            ? "Automatic update setting"
            : ReferenceEquals(sender, AutostartCheckBox) || ReferenceEquals(sender, StartVisibleCheckBox)
                ? "Manager startup setting"
                : "Connection option";
        await SaveControlChangeAsync(description);
    }

    private async void UpdateIntervalTextBox_LostFocus(object sender, RoutedEventArgs args)
    {
        await SaveControlChangeAsync("Automatic update interval");
    }

    private async void UpdateIntervalTextBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter)
        {
            return;
        }

        args.Handled = true;
        await SaveControlChangeAsync("Automatic update interval");
    }

    private async void ManagerUpdateSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await App.MainWindow.ShowManagerUpdateSettingsAsync();
    }

    private async void StartServiceButton_Click(object sender, RoutedEventArgs e)
    {
        await RunServiceActionAsync("start", "Start service");
    }

    private async void StopServiceButton_Click(object sender, RoutedEventArgs e)
    {
        await RunServiceActionAsync("stop", "Stop service");
    }

    private async void RestartServiceButton_Click(object sender, RoutedEventArgs e)
    {
        await RunServiceActionAsync("restart", "Restart service");
    }

    private async void EditServiceButton_Click(object sender, RoutedEventArgs e)
    {
        SetServiceButtonsEnabled(false);
        App.Logger.Info("Open service editor requested");
        ShowServiceStatus(
            InfoBarSeverity.Informational,
            "Opening service editor",
            "Approve the administrator prompt to edit the Beszel Agent service.");

        try
        {
            var exitCode = await App.Broker.OpenServiceEditorAsync();
            if (exitCode == 0)
            {
                ShowServiceStatus(
                    InfoBarSeverity.Success,
                    "Service editor opened",
                    "You can now edit the Beszel Agent service settings.");
                App.Logger.Info("Service editor opened successfully");
            }
            else
            {
                ShowServiceStatus(
                    InfoBarSeverity.Error,
                    "Could not open service editor",
                    GetFriendlyHelperError(exitCode));
                App.Logger.Error($"Open service editor failed with helper exit code {exitCode}");
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ShowServiceStatus(
                InfoBarSeverity.Warning,
                "Service editor cancelled",
                "The administrator prompt was cancelled.");
            App.Logger.Info("Open service editor cancelled at UAC prompt");
        }
        catch (Exception ex)
        {
            ShowServiceStatus(InfoBarSeverity.Error, "Could not open service editor", ex.Message);
            App.Logger.Error($"Open service editor failed: {ex}");
        }
        finally
        {
            SetServiceButtonsEnabled(true);
        }
    }

    private async Task RunServiceActionAsync(string action, string displayName)
    {
        SetServiceButtonsEnabled(false);
        App.Logger.Info($"{displayName} requested");
        ShowServiceStatus(
            InfoBarSeverity.Informational,
            $"{displayName} in progress",
            "The background service is completing the requested action.");
        try
        {
            var exitCode = await App.Broker.RunServiceActionAsync(action);
            if (exitCode == 0)
            {
                await App.MainWindow.RefreshServiceStatusNowAsync();
                ShowServiceStatus(
                    InfoBarSeverity.Success,
                    $"{displayName} completed",
                    GetFriendlyServiceSuccess(action));
                App.Logger.Info($"{displayName} completed successfully");
            }
            else
            {
                ShowServiceStatus(
                    InfoBarSeverity.Error,
                    $"{displayName} failed",
                    GetFriendlyHelperError(exitCode));
                App.Logger.Error($"{displayName} failed with helper exit code {exitCode}");
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ShowServiceStatus(
                InfoBarSeverity.Warning,
                $"{displayName} cancelled",
                "The administrator prompt was cancelled.");
            App.Logger.Info($"{displayName} cancelled at UAC prompt");
        }
        catch (Exception ex)
        {
            ShowServiceStatus(InfoBarSeverity.Error, $"{displayName} failed", ex.Message);
            App.Logger.Error($"{displayName} failed: {ex}");
        }
        finally
        {
            SetServiceButtonsEnabled(true);
        }
    }

    private void SetServiceButtonsEnabled(bool enabled)
    {
        StartServiceButton.IsEnabled = enabled;
        StopServiceButton.IsEnabled = enabled;
        RestartServiceButton.IsEnabled = enabled;
        EditServiceButton.IsEnabled = enabled;
    }

    private void ShowServiceStatus(InfoBarSeverity severity, string title, string message)
    {
        App.MainWindow.ShowActionStatus(severity, title, message);
    }

    private void ShowSavedStatus(string description)
    {
        if (IsAutomaticUpdateSetting(description))
        {
            ShowServiceStatus(
                InfoBarSeverity.Informational,
                "Pending changes",
                "Choose Apply settings to update the automatic update schedule. The Beszel Agent service will not be restarted.");
            return;
        }

        var serviceAffecting = !IsManagerOnlySetting(description);
        ShowServiceStatus(
            InfoBarSeverity.Success,
            $"{description} saved",
            serviceAffecting
                ? "Apply settings when you are ready to restart the Beszel Agent service with this change."
                : "This setting was saved.");
    }

    private static bool IsManagerOnlySetting(string description)
    {
        return description is "Automatic update setting" or "Automatic update interval" or "Manager startup setting";
    }

    private static bool IsAutomaticUpdateSetting(string description)
    {
        return description is "Automatic update setting" or "Automatic update interval";
    }

    private bool TryGetUpdateInterval(out int hours)
    {
        hours = 0;
        return int.TryParse(UpdateIntervalTextBox.Text.Trim(), out hours) && hours is >= 1 and <= 720;
    }

    private async Task ApplyListenChangeAsync(bool disabling)
    {
        SetServiceButtonsEnabled(false);
        ShowServiceStatus(
            InfoBarSeverity.Informational,
            disabling ? "Disabling listen port" : "Enabling listen port",
            "The background service is applying the service and firewall configuration.");
        try
        {
            var exitCode = await App.Broker.ApplyConfigurationAsync();
            if (exitCode == 0)
            {
                await MarkCurrentSettingsAppliedAsync();
                ShowServiceStatus(
                    InfoBarSeverity.Success,
                    disabling ? "Listen port disabled" : "Listen port enabled",
                    disabling
                        ? "The service configuration was updated and the firewall rule was removed."
                        : "The service configuration was updated and the firewall rule was added.");
                await App.MainWindow.RefreshServiceStatusNowAsync();
            }
            else
            {
                ShowServiceStatus(
                    InfoBarSeverity.Error,
                    "Listen port change failed",
                    GetFriendlyHelperError(exitCode));
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ShowServiceStatus(
                InfoBarSeverity.Warning,
                "Listen port change cancelled",
                "The administrator prompt was cancelled.");
        }
        catch (Exception ex)
        {
            ShowServiceStatus(InfoBarSeverity.Error, "Listen port change failed", ex.Message);
            App.Logger.Error($"Listen port change failed: {ex}");
        }
        finally
        {
            SetServiceButtonsEnabled(true);
        }
    }

    private async Task MarkCurrentSettingsAppliedAsync()
    {
        var config = await _configService.LoadAsync();
        config.LastAppliedFingerprint = config.ApplyFingerprint();
        config.LastAppliedAt = DateTime.Now.ToString("s");
        await _configService.SaveAsync(config);
    }

    private static string GetFriendlyServiceSuccess(string action)
    {
        return action switch
        {
            "start" => "Beszel Agent is starting.",
            "stop" => "Beszel Agent is stopping.",
            "restart" => "Beszel Agent was restarted.",
            _ => "The service action completed.",
        };
    }

    private static string GetFriendlyHelperError(int exitCode)
    {
        return exitCode switch
        {
            2 => "The service helper received an unknown command.",
            3 => "The agent or configuration file could not be found.",
            4 => "The Beszel Agent service configuration could not be opened.",
            53 => "NSSM could not be found. Reinstall BeszelAgentManager or place nssm.exe next to the installed app.",
            1056 => "Beszel Agent is already running.",
            1062 => "Beszel Agent is not running.",
            _ => $"The service helper returned error code {exitCode}.",
        };
    }

    private static ConnectionSnapshot Snapshot(AgentConfig config, bool autostartEnabled)
    {
        return new ConnectionSnapshot(
            config.Key,
            config.Token,
            config.HubUrl,
            config.HubUrlIpFallback,
            config.HubUrlIpFallbackEnabled,
            config.Listen,
            config.AutoUpdateEnabled,
            config.UpdateIntervalHours,
            autostartEnabled,
            config.StartHidden);
    }

    private bool LogConnectionChanges(ConnectionSnapshot before, ConnectionSnapshot after, bool logNoChanges)
    {
        var changes = new List<string>();
        AddSensitive("Key", before.Key, after.Key);
        AddSensitive("Token", before.Token, after.Token);
        AddValue("Hub URL", before.HubUrl, after.HubUrl);
        AddValue("Hub URL IP fallback", before.HubFallback, after.HubFallback);
        AddValue("Hub URL IP fallback enabled", before.HubFallbackEnabled, after.HubFallbackEnabled);
        AddValue("Listen port", before.Listen, after.Listen);
        AddValue("Automatic updates", before.AutoUpdateEnabled, after.AutoUpdateEnabled);
        AddValue("Update interval hours", before.UpdateIntervalHours, after.UpdateIntervalHours);
        AddValue("Manager autostart", before.AutostartEnabled, after.AutostartEnabled);
        AddValue("Start hidden", before.StartHidden, after.StartHidden);

        if (changes.Count == 0)
        {
            if (logNoChanges)
            {
                App.Logger.Info("Connection settings saved: no changes");
            }

            return false;
        }

        App.Logger.Info($"Connection settings saved: {string.Join("; ", changes)}");
        return true;

        void AddValue<T>(string name, T beforeValue, T afterValue)
        {
            if (!EqualityComparer<T>.Default.Equals(beforeValue, afterValue))
            {
                changes.Add($"{name}: {Format(beforeValue)} -> {Format(afterValue)}");
            }
        }

        void AddSensitive(string name, string beforeValue, string afterValue)
        {
            if (!string.Equals(beforeValue, afterValue, StringComparison.Ordinal))
            {
                changes.Add($"{name}: changed");
            }
        }

        static string Format<T>(T value)
        {
            return value switch
            {
                null => "(empty)",
                string text when string.IsNullOrWhiteSpace(text) => "(empty)",
                string text => text,
                bool flag => flag ? "enabled" : "disabled",
                _ => value.ToString() ?? "(empty)",
            };
        }
    }

    private sealed record ConnectionSnapshot(
        string Key,
        string Token,
        string HubUrl,
        string HubFallback,
        bool HubFallbackEnabled,
        int? Listen,
        bool AutoUpdateEnabled,
        int UpdateIntervalHours,
        bool AutostartEnabled,
        bool StartHidden);
}
