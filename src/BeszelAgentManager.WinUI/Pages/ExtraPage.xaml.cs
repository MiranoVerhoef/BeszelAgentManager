using BeszelAgentManager.WinUI.Services;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.DataTransfer;

namespace BeszelAgentManager.WinUI.Pages;

public sealed partial class ExtraPage : Page
{
    private const string MaskedToken = "************************";
    private readonly ConfigService _configService = new();
    private readonly GitHubTokenService _gitHubTokenService = new();
    private readonly AgentFingerprintService _fingerprintService = new();
    private readonly BackgroundServiceManagementService _backgroundServiceManagementService = new();
    private AgentConfig _config = new();
    private bool _loading = true;
    private bool _backgroundServiceInstalled;

    public ExtraPage()
    {
        InitializeComponent();
        Loaded += ExtraPage_Loaded;
    }

    private async void ExtraPage_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        _config = await _configService.LoadAsync();
        AutoRestartCheckBox.IsChecked = _config.AutoRestartEnabled;
        var unit = NormalizeRestartUnit(_config.AutoRestartIntervalUnit);
        RestartIntervalTextBox.Text = (_config.AutoRestartIntervalValue > 0
            ? _config.AutoRestartIntervalValue
            : _config.AutoRestartIntervalHours > 0
                ? _config.AutoRestartIntervalHours
                : 24).ToString();
        RestartIntervalUnitComboBox.SelectedIndex = unit == "minutes" ? 1 : 0;
        WebSocketBackoffCheckBox.IsChecked = _config.WebSocketOfflineBackoffEnabled;
        LanguageComboBox.SelectedIndex = _config.UiLanguage == "zh-Hans" ? 1 : 0;
        UpdatePeriodicRestartControlState();
        UpdateDefenderButton();
        _loading = false;
        await RefreshBackgroundServiceStatusAsync(showNotification: false);
    }

    private async void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageComboBox.SelectedItem is not ComboBoxItem { Tag: string language })
        {
            return;
        }

        var previousLanguage = _config.UiLanguage;
        if (language == previousLanguage)
        {
            return;
        }

        LanguageComboBox.IsEnabled = false;
        try
        {
            var persisted = await _configService.LoadAsync();
            persisted.UiLanguage = language;
            await _configService.SaveAsync(persisted);
            _config.UiLanguage = language;
        }
        catch (Exception ex)
        {
            _loading = true;
            LanguageComboBox.SelectedIndex = previousLanguage == "zh-Hans" ? 1 : 0;
            _loading = false;
            App.Logger.Error($"Could not save UI language: {ex}");
            App.MainWindow.ShowActionStatus(InfoBarSeverity.Error, Localization.Get("CouldNotSaveSettings"), ex.Message);
            return;
        }
        finally
        {
            LanguageComboBox.IsEnabled = true;
        }

        var restartDialog = new ContentDialog
        {
            XamlRoot = App.MainWindow.Content.XamlRoot,
            Title = Localization.Get("LanguageSaved"),
            Content = Localization.Get("RestartForLanguageChange"),
            PrimaryButtonText = Localization.Get("RestartNow"),
            CloseButtonText = Localization.Get("Later"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await restartDialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var failure = AppInstance.Restart(string.Empty);
            App.Logger.Error($"Manager restart failed after language change: {failure}");
            App.MainWindow.ShowActionStatus(InfoBarSeverity.Error,
                Localization.Get("ManagerRestartFailed"), Localization.Get("LanguageSavedRestartManually"));
        }
    }

    private async void InstallBackgroundServiceButton_Click(object sender, RoutedEventArgs e)
    {
        SetBackgroundServiceButtonsEnabled(false);
        try
        {
            App.Logger.Info("Background service repair/install requested");
            var exitCode = await _backgroundServiceManagementService.InstallAsync();
            if (exitCode != 0)
            {
                var detail = BackgroundServiceManagementService.ReadLastHelperError();
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                    ? Localization.Get("TheElevatedHelperReturnedExitCode0", exitCode)
                    : detail);
            }

            await RefreshBackgroundServiceStatusAsync(showNotification: false);
            App.MainWindow.ShowActionStatus(
                InfoBarSeverity.Success,
                Localization.Get("BackgroundServiceInstalled"),
                Localization.Get("ThePrivilegedBackgroundServiceIsInstalledAndRunning"));
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Background service installation failed: {ex}");
            App.MainWindow.ShowActionStatus(InfoBarSeverity.Error, Localization.Get("BackgroundServiceInstallationFailed"), ex.Message);
        }
        finally
        {
            SetBackgroundServiceButtonsEnabled(true);
        }
    }

    private async void UninstallBackgroundServiceButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Localization.Get("UninstallBackgroundService"),
            Content = Localization.Get("PrivilegedActionsAndSchedulesWillStopWorkingUntilThe"),
            PrimaryButtonText = Localization.Get("Uninstall"),
            CloseButtonText = Localization.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetBackgroundServiceButtonsEnabled(false);
        try
        {
            App.Logger.Info("Background service uninstall requested");
            var exitCode = await _backgroundServiceManagementService.UninstallAsync();
            if (exitCode != 0)
            {
                throw new InvalidOperationException(Localization.Get("TheElevatedHelperReturnedExitCode0", exitCode));
            }

            await RefreshBackgroundServiceStatusAsync(showNotification: false);
            App.MainWindow.ShowActionStatus(
                InfoBarSeverity.Success,
                Localization.Get("BackgroundServiceUninstalled"),
                Localization.Get("ManagerSettingsAndAgentDataWereKept"));
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Background service uninstall failed: {ex}");
            App.MainWindow.ShowActionStatus(InfoBarSeverity.Error, Localization.Get("BackgroundServiceUninstallFailed"), ex.Message);
        }
        finally
        {
            SetBackgroundServiceButtonsEnabled(true);
        }
    }

    private async void CheckBackgroundServiceButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshBackgroundServiceStatusAsync(showNotification: true);
    }

    private async Task RefreshBackgroundServiceStatusAsync(bool showNotification)
    {
        CheckBackgroundServiceButton.IsEnabled = false;
        try
        {
            var status = await _backgroundServiceManagementService.GetStatusAsync();
            _backgroundServiceInstalled = status.IsInstalled;
            BackgroundServiceStatusText.Text = Localization.Get("Status0", Localization.Status(status.State));
            UninstallBackgroundServiceButton.IsEnabled = status.IsInstalled;
            if (showNotification)
            {
                App.MainWindow.ShowActionStatus(
                    status.IsInstalled ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                    status.IsInstalled ? Localization.Get("BackgroundServiceFound") : Localization.Get("BackgroundServiceNotInstalled"),
                    status.IsInstalled ? Localization.Get("CurrentState0", Localization.Status(status.State)) : Localization.Get("ChooseInstallToRepairTheBackgroundService"));
            }
        }
        catch (Exception ex)
        {
            BackgroundServiceStatusText.Text = Localization.Get("StatusCheckFailed");
            App.Logger.Error($"Background service status check failed: {ex}");
            if (showNotification)
            {
                App.MainWindow.ShowActionStatus(InfoBarSeverity.Error, Localization.Get("BackgroundServiceCheckFailed"), ex.Message);
            }
        }
        finally
        {
            CheckBackgroundServiceButton.IsEnabled = true;
        }
    }

    private void SetBackgroundServiceButtonsEnabled(bool enabled)
    {
        InstallBackgroundServiceButton.IsEnabled = enabled;
        UninstallBackgroundServiceButton.IsEnabled = enabled && _backgroundServiceInstalled;
        CheckBackgroundServiceButton.IsEnabled = enabled;
    }

    private async void AutoRestartCheckBox_Click(object sender, RoutedEventArgs e)
    {
        UpdatePeriodicRestartControlState();
        await SavePeriodicRestartAsync();
    }

    private async void WebSocketBackoffCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _config.WebSocketOfflineBackoffEnabled = WebSocketBackoffCheckBox.IsChecked == true;
        if (_config.HasRetryStrategyConflict())
        {
            _config.WebSocketOfflineBackoffEnabled = false;
            WebSocketBackoffCheckBox.IsChecked = false;
            App.MainWindow.ShowActionStatus(
                InfoBarSeverity.Warning,
                Localization.Get("ConflictingRetrySettings"),
                Localization.Get("RemoveEXITONDNSERRORUnderEnvironmentBeforeEnabling"));
            return;
        }

        var persisted = await _configService.LoadAsync();
        _config.LastAppliedFingerprint = persisted.LastAppliedFingerprint;
        _config.LastAppliedAt = persisted.LastAppliedAt;
        _config.LastAppliedManagerTasksFingerprint = persisted.LastAppliedManagerTasksFingerprint;
        await _configService.SaveAsync(_config);
        App.Logger.Info($"WebSocket offline backoff saved: enabled={_config.WebSocketOfflineBackoffEnabled}");
        App.MainWindow.ShowActionStatus(
            InfoBarSeverity.Informational,
            Localization.Get("WebSocketBackoffSaved"),
            Localization.Get("ChooseApplySettingsToUpdateTheBackgroundService"));
    }

    private async void RestartIntervalTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        await SavePeriodicRestartAsync();
    }

    private async void RestartIntervalTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        await SavePeriodicRestartAsync();
    }

    private async void RestartIntervalUnitComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await SavePeriodicRestartAsync();
    }

    private async Task SavePeriodicRestartAsync()
    {
        if (_loading)
        {
            return;
        }

        var unit = SelectedRestartUnit();
        if (!TryGetRestartInterval(unit, out var interval))
        {
            App.MainWindow.ShowActionStatus(
                InfoBarSeverity.Warning,
                Localization.Get("InvalidRestartInterval"),
                unit == "minutes"
                    ? Localization.Get("EnterAWholeNumberFrom1To10080Minutes")
                    : Localization.Get("EnterAWholeNumberFrom1To168Hours"));
            return;
        }

        _config.AutoRestartEnabled = AutoRestartCheckBox.IsChecked == true;
        _config.AutoRestartIntervalValue = interval;
        _config.AutoRestartIntervalUnit = unit;
        _config.AutoRestartIntervalHours = unit == "hours"
            ? interval
            : Math.Clamp((int)Math.Ceiling(interval / 60d), 1, 168);

        var persisted = await _configService.LoadAsync();
        _config.LastAppliedFingerprint = persisted.LastAppliedFingerprint;
        _config.LastAppliedAt = persisted.LastAppliedAt;
        _config.LastAppliedManagerTasksFingerprint = persisted.LastAppliedManagerTasksFingerprint;
        await _configService.SaveAsync(_config);
        App.Logger.Info($"Periodic service restart saved: enabled={_config.AutoRestartEnabled}, interval={interval} {unit}");
        App.MainWindow.ShowActionStatus(
            InfoBarSeverity.Informational,
            Localization.Get("PeriodicRestartSaved"),
            Localization.Get("ChooseApplySettingsToUpdateTheBackgroundServiceSchedule"));
    }

    private void UpdatePeriodicRestartControlState()
    {
        var enabled = AutoRestartCheckBox.IsChecked == true;
        RestartIntervalTextBox.IsEnabled = enabled;
        RestartIntervalUnitComboBox.IsEnabled = enabled;
    }

    private string SelectedRestartUnit()
    {
        return RestartIntervalUnitComboBox.SelectedItem is ComboBoxItem { Tag: string tag }
            ? NormalizeRestartUnit(tag)
            : "hours";
    }

    private bool TryGetRestartInterval(string unit, out int interval)
    {
        var maximum = unit == "minutes" ? 10080 : 168;
        return int.TryParse(RestartIntervalTextBox.Text.Trim(), out interval)
            && interval >= 1
            && interval <= maximum;
    }

    private static string NormalizeRestartUnit(string? unit)
    {
        return string.Equals(unit, "minutes", StringComparison.OrdinalIgnoreCase) ? "minutes" : "hours";
    }

    private void UpdateDefenderButton()
    {
        DefenderExclusionButton.Content = _config.DefenderExclusionEnabled
            ? Localization.Get("RemoveDefenderExclusion")
            : Localization.Get("AddDefenderExclusion");
    }

    private async void DefenderExclusionButton_Click(object sender, RoutedEventArgs e)
    {
        var enabling = !_config.DefenderExclusionEnabled;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = enabling ? Localization.Get("AddDefenderExclusion2") : Localization.Get("RemoveDefenderExclusion2"),
            Content = new TextBlock
            {
                Text = enabling
                    ? Localization.Get("ThisExcludesOnlyTheBeszelAgentManagerInstallationDirectoryFromMicrosoft")
                    : Localization.Get("ThisRestoresNormalMicrosoftDefenderScanningForTheBeszelAgentManager"),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = enabling ? Localization.Get("AddExclusion") : Localization.Get("RemoveExclusion"),
            CloseButtonText = Localization.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        DefenderExclusionButton.IsEnabled = false;
        try
        {
            var exitCode = await App.Broker.SetDefenderExclusionAsync(enabling);
            if (exitCode != 0)
            {
                throw new InvalidOperationException(Localization.Get("TheBackgroundServiceReturnedCode0", exitCode));
            }

            _config.DefenderExclusionEnabled = enabling;
            await _configService.SaveAsync(_config);
            UpdateDefenderButton();
            App.Logger.Info($"Windows Defender exclusion {(enabling ? "added" : "removed")}");
            App.MainWindow.ShowActionStatus(
                InfoBarSeverity.Success,
                enabling ? Localization.Get("DefenderExclusionAdded") : Localization.Get("DefenderExclusionRemoved"),
                Localization.Get("TheMicrosoftDefenderPreferenceWasUpdatedWithoutAUAC"));
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Defender exclusion update failed: {ex}");
            App.MainWindow.ShowActionStatus(InfoBarSeverity.Error, Localization.Get("DefenderExclusionUpdateFailed"), ex.Message);
        }
        finally
        {
            DefenderExclusionButton.IsEnabled = true;
        }
    }

    private async void ThirdPartyAvButton_Click(object sender, RoutedEventArgs e)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        (string Title, string[] Values)[] sections =
        [
            (Localization.Get("ApplicationFolders"),
            [
                Path.Combine(programFiles, "BeszelAgentManager"),
                Path.Combine(programFiles, "Beszel-Agent"),
            ]),
            (Localization.Get("DataLogsAndUpdateStaging"),
            [
                Path.Combine(programData, "BeszelAgentManager"),
                Path.Combine(programData, "BeszelAgentManager.ServiceData"),
            ]),
            (Localization.Get("ExecutablesAndProcesses"),
            [
                "BeszelAgentManager.exe",
                "BeszelAgentManager.Helper.exe",
                "beszel-agent.exe",
                "nssm.exe",
                Localization.Get("BeszelAgentManagerSetupExeOrBeszelAgentManagerSetupLiteExeOnlyWhileInstalling"),
            ]),
        ];
        var instructions = string.Join(
            Environment.NewLine + Environment.NewLine,
            sections.Select(static section =>
                section.Title + Environment.NewLine +
                string.Join(Environment.NewLine, section.Values.Select(static value => $"• {value}"))));
        var content = new StackPanel { Spacing = 10, Width = 650 };
        foreach (var section in sections)
        {
            AddSection(section.Title, section.Values);
        }
        var scrollViewer = new ScrollViewer
        {
            Content = content,
            Height = 500,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Localization.Get("ThirdPartyAntivirusAllowlistInstructions"),
            Content = scrollViewer,
            PrimaryButtonText = Localization.Get("CopyInstructions"),
            CloseButtonText = Localization.Get("Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var package = new DataPackage();
            package.SetText(instructions);
            Clipboard.SetContent(package);
            App.MainWindow.ShowActionStatus(
                InfoBarSeverity.Success,
                Localization.Get("InstructionsCopied"),
                Localization.Get("TheAntivirusAllowlistInstructionsWereCopiedToTheClipboard"));
        }

        void AddSection(string title, IEnumerable<string> values)
        {
            content.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0),
            });
            foreach (var value in values)
            {
                content.Children.Add(new TextBlock
                {
                    Text = $"• {value}",
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }
    }

    private async void GitHubAuthButton_Click(object sender, RoutedEventArgs e)
    {
        App.Logger.Info("GitHub authentication settings requested");
        var passwordBox = new PasswordBox
        {
            PlaceholderText = Localization.Get("GitHubToken"),
            Width = 420,
        };

        var panel = new StackPanel { Spacing = 10 };
        var environmentToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN")?.Trim();
        var hasEnvironmentToken = !string.IsNullOrWhiteSpace(environmentToken);
        var hasSavedToken = !string.IsNullOrWhiteSpace(_config.GitHubTokenEncrypted);
        if (hasEnvironmentToken || hasSavedToken)
        {
            passwordBox.Password = MaskedToken;
        }

        if (hasEnvironmentToken)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Localization.Get("GITHUBTOKENIsSetInTheEnvironmentAndWill"),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = hasEnvironmentToken || hasSavedToken
                ? Localization.Get("AGitHubTokenIsConfiguredLeaveTheMaskedValue")
                : Localization.Get("EnterAGitHubTokenToAvoidAPIRateLimiting"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(passwordBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Localization.Get("GitHubAuthentication"),
            Content = panel,
            PrimaryButtonText = Localization.Get("Save"),
            SecondaryButtonText = Localization.Get("Clear"),
            CloseButtonText = Localization.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        try
        {
            if (result == ContentDialogResult.Primary)
            {
                var token = passwordBox.Password.Trim();
                if (token == MaskedToken && (hasEnvironmentToken || hasSavedToken))
                {
                    App.MainWindow.ShowActionStatus(InfoBarSeverity.Informational, Localization.Get("GitHubTokenUnchanged"), Localization.Get("TheConfiguredTokenWasKept"));
                    return;
                }

                if (string.IsNullOrWhiteSpace(token))
                {
                    App.MainWindow.ShowActionStatus(InfoBarSeverity.Warning, Localization.Get("GitHubTokenNotChanged"), Localization.Get("EnterATokenOrChooseClear"));
                    return;
                }

                await _gitHubTokenService.SaveTokenAsync(token);
                _config = await _configService.LoadAsync();
                App.Logger.Info("GitHub token saved");
                App.MainWindow.ShowActionStatus(InfoBarSeverity.Success, Localization.Get("GitHubTokenSaved"), Localization.Get("GitHubAPIRequestsWillUseTheSavedToken"));
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await _gitHubTokenService.ClearSavedTokenAsync();
                _config = await _configService.LoadAsync();
                App.Logger.Info("GitHub token cleared");
                App.MainWindow.ShowActionStatus(
                    InfoBarSeverity.Success,
                    Localization.Get("GitHubTokenCleared"),
                    hasEnvironmentToken
                        ? Localization.Get("TheSavedTokenWasRemovedGITHUBTOKENRemainsActive")
                        : Localization.Get("SavedGitHubAuthenticationHasBeenRemoved"));
            }
        }
        catch (Exception ex)
        {
            App.Logger.Error($"GitHub authentication update failed: {ex}");
            App.MainWindow.ShowActionStatus(InfoBarSeverity.Error, Localization.Get("GitHubAuthFailed"), ex.Message);
        }
    }

    private async void ViewFingerprintButton_Click(object sender, RoutedEventArgs e)
    {
        ViewFingerprintButton.IsEnabled = false;
        try
        {
            App.Logger.Info("Agent fingerprint view requested");
            var result = await _fingerprintService.ViewAsync();
            if (result.Success)
            {
                await ShowFingerprintDialogAsync(result.Output);
                return;
            }

            App.Logger.Warning($"Agent fingerprint view failed: {result.Output}");
            App.MainWindow.ShowActionStatus(InfoBarSeverity.Error, Localization.Get("FingerprintViewFailed"), result.Output);
        }
        finally
        {
            ViewFingerprintButton.IsEnabled = true;
        }
    }

    private async void ResetFingerprintButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Localization.Get("ResetFingerprint"),
            Content = new TextBlock
            {
                Text = Localization.Get("ThisResetsTheBeszelAgentFingerprintAndRestartsThe"),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Localization.Get("Reset"),
            CloseButtonText = Localization.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        ResetFingerprintButton.IsEnabled = false;
        try
        {
            App.Logger.Info("Agent fingerprint reset requested");
            var exitCode = await App.Broker.ResetAgentFingerprintAsync();
            if (exitCode == 0)
            {
                App.Logger.Info("Agent fingerprint reset completed successfully");
                App.MainWindow.ShowActionStatus(InfoBarSeverity.Success, Localization.Get("FingerprintReset"), Localization.Get("TheAgentFingerprintWasResetAndTheServiceWas"));
                return;
            }

            App.Logger.Error($"Agent fingerprint reset failed with helper exit code {exitCode}");
            App.MainWindow.ShowActionStatus(InfoBarSeverity.Error, Localization.Get("FingerprintResetFailed"), Localization.Get("TheBackgroundServiceReturnedExitCode0", exitCode));
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Agent fingerprint reset failed: {ex}");
            App.MainWindow.ShowActionStatus(InfoBarSeverity.Error, Localization.Get("FingerprintResetFailed"), ex.Message);
        }
        finally
        {
            ResetFingerprintButton.IsEnabled = true;
        }
    }

    private async Task ShowFingerprintDialogAsync(string fingerprint)
    {
        var textBox = new TextBox
        {
            Text = fingerprint.Trim(),
            IsReadOnly = true,
            Width = 520,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Localization.Get("AgentFingerprint"),
            Content = textBox,
            PrimaryButtonText = Localization.Get("Copy"),
            CloseButtonText = Localization.Get("Close"),
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var package = new DataPackage();
            package.SetText(textBox.Text);
            Clipboard.SetContent(package);
            App.MainWindow.ShowActionStatus(InfoBarSeverity.Success, Localization.Get("FingerprintCopied"), Localization.Get("TheAgentFingerprintWasCopiedToTheClipboard"));
        }
    }

}
