using BeszelAgentManager.WinUI.Services;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace BeszelAgentManager.WinUI.Pages;

public sealed partial class ExtraPage : Page
{
    private const string MaskedToken = "************************";
    private readonly ConfigService _configService = new();
    private readonly GitHubTokenService _gitHubTokenService = new();
    private readonly AgentFingerprintService _fingerprintService = new();
    private AgentConfig _config = new();
    private bool _loading = true;

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
        UpdatePeriodicRestartControlState();
        UpdateDefenderButton();
        _loading = false;
    }

    private async void AutoRestartCheckBox_Click(object sender, RoutedEventArgs e)
    {
        UpdatePeriodicRestartControlState();
        await SavePeriodicRestartAsync();
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
                "Invalid restart interval",
                unit == "minutes"
                    ? "Enter a whole number from 1 to 10080 minutes."
                    : "Enter a whole number from 1 to 168 hours.");
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
            "Periodic restart saved",
            "Choose Apply settings to update the background-service schedule.");
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
            ? "Remove Defender exclusion"
            : "Add Defender exclusion";
    }

    private async void DefenderExclusionButton_Click(object sender, RoutedEventArgs e)
    {
        var enabling = !_config.DefenderExclusionEnabled;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = enabling ? "Add Defender exclusion?" : "Remove Defender exclusion?",
            Content = new TextBlock
            {
                Text = enabling
                    ? "This excludes only the BeszelAgentManager installation directory from Microsoft Defender scanning. Enable it only if Defender interferes with the application."
                    : "This restores normal Microsoft Defender scanning for the BeszelAgentManager installation directory.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = enabling ? "Add exclusion" : "Remove exclusion",
            CloseButtonText = "Cancel",
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
                throw new InvalidOperationException($"The background service returned code {exitCode}.");
            }

            _config.DefenderExclusionEnabled = enabling;
            await _configService.SaveAsync(_config);
            UpdateDefenderButton();
            App.Logger.Info($"Windows Defender exclusion {(enabling ? "added" : "removed")}");
            App.MainWindow.ShowActionStatus(
                InfoBarSeverity.Success,
                enabling ? "Defender exclusion added" : "Defender exclusion removed",
                "The Microsoft Defender preference was updated without a UAC prompt.");
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Defender exclusion update failed: {ex}");
            App.MainWindow.ShowActionStatus(InfoBarSeverity.Error, "Defender exclusion update failed", ex.Message);
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
            ("Application folders",
            [
                Path.Combine(programFiles, "BeszelAgentManager"),
                Path.Combine(programFiles, "Beszel-Agent"),
            ]),
            ("Data, logs, and update staging",
            [
                Path.Combine(programData, "BeszelAgentManager"),
                Path.Combine(programData, "BeszelAgentManager", "agent_logs"),
                Path.Combine(programData, "BeszelAgentManager", "tmp_agent"),
                Path.Combine(programData, "BeszelAgentManager", "manager-update"),
            ]),
            ("Executables and processes",
            [
                "BeszelAgentManager.exe",
                "BeszelAgentManager.Helper.exe",
                "beszel-agent.exe",
                "nssm.exe",
                "BeszelAgentManagerSetup.exe (only while installing or updating)",
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
            Title = "Third-party antivirus allowlist instructions",
            Content = scrollViewer,
            PrimaryButtonText = "Copy instructions",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var package = new DataPackage();
            package.SetText(instructions);
            Clipboard.SetContent(package);
            App.MainWindow.ShowActionStatus(
                InfoBarSeverity.Success,
                "Instructions copied",
                "The antivirus allowlist instructions were copied to the clipboard.");
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
            PlaceholderText = "GitHub token",
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
                Text = "GITHUB_TOKEN is set in the environment and will override any saved token.",
                TextWrapping = TextWrapping.Wrap,
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = hasEnvironmentToken || hasSavedToken
                ? "A GitHub token is configured. Leave the masked value unchanged to keep it, or enter a new token. Choose Clear to remove the saved token."
                : "Enter a GitHub token to avoid API rate limiting.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(passwordBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "GitHub Authentication",
            Content = panel,
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Clear",
            CloseButtonText = "Cancel",
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
                    App.MainWindow.ShowActionStatus(InfoBarSeverity.Informational, "GitHub token unchanged", "The configured token was kept.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(token))
                {
                    App.MainWindow.ShowActionStatus(InfoBarSeverity.Warning, "GitHub token not changed", "Enter a token or choose Clear.");
                    return;
                }

                await _gitHubTokenService.SaveTokenAsync(token);
                _config = await _configService.LoadAsync();
                App.Logger.Info("GitHub token saved");
                App.MainWindow.ShowActionStatus(InfoBarSeverity.Success, "GitHub token saved", "GitHub API requests will use the saved token.");
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await _gitHubTokenService.ClearSavedTokenAsync();
                _config = await _configService.LoadAsync();
                App.Logger.Info("GitHub token cleared");
                App.MainWindow.ShowActionStatus(
                    InfoBarSeverity.Success,
                    "GitHub token cleared",
                    hasEnvironmentToken
                        ? "The saved token was removed. GITHUB_TOKEN remains active from the environment."
                        : "Saved GitHub authentication has been removed.");
            }
        }
        catch (Exception ex)
        {
            App.Logger.Error($"GitHub authentication update failed: {ex}");
            App.MainWindow.ShowActionStatus(InfoBarSeverity.Error, "GitHub auth failed", ex.Message);
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
            App.MainWindow.ShowActionStatus(InfoBarSeverity.Error, "Fingerprint view failed", result.Output);
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
            Title = "Reset fingerprint",
            Content = new TextBlock
            {
                Text = "This resets the Beszel Agent fingerprint and restarts the service. The machine may need to be re-registered in the hub.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
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
                App.MainWindow.ShowActionStatus(InfoBarSeverity.Success, "Fingerprint reset", "The agent fingerprint was reset and the service was started.");
                return;
            }

            App.Logger.Error($"Agent fingerprint reset failed with helper exit code {exitCode}");
            App.MainWindow.ShowActionStatus(InfoBarSeverity.Error, "Fingerprint reset failed", $"The background service returned exit code {exitCode}.");
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Agent fingerprint reset failed: {ex}");
            App.MainWindow.ShowActionStatus(InfoBarSeverity.Error, "Fingerprint reset failed", ex.Message);
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
            Title = "Agent Fingerprint",
            Content = textBox,
            PrimaryButtonText = "Copy",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var package = new DataPackage();
            package.SetText(textBox.Text);
            Clipboard.SetContent(package);
            App.MainWindow.ShowActionStatus(InfoBarSeverity.Success, "Fingerprint copied", "The agent fingerprint was copied to the clipboard.");
        }
    }

}
