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
        RestartIntervalNumberBox.Value = _config.AutoRestartIntervalHours > 0
            ? _config.AutoRestartIntervalHours
            : 24;
        _loading = false;
    }

    private async void AutoRestartCheckBox_Click(object sender, RoutedEventArgs e)
    {
        await SavePeriodicRestartAsync();
    }

    private async void RestartIntervalNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        await SavePeriodicRestartAsync();
    }

    private async Task SavePeriodicRestartAsync()
    {
        if (_loading)
        {
            return;
        }

        _config.AutoRestartEnabled = AutoRestartCheckBox.IsChecked == true;
        _config.AutoRestartIntervalHours = double.IsNaN(RestartIntervalNumberBox.Value)
            ? 24
            : Math.Clamp((int)RestartIntervalNumberBox.Value, 1, 168);

        var persisted = await _configService.LoadAsync();
        _config.LastAppliedFingerprint = persisted.LastAppliedFingerprint;
        _config.LastAppliedAt = persisted.LastAppliedAt;
        _config.LastAppliedManagerTasksFingerprint = persisted.LastAppliedManagerTasksFingerprint;
        await _configService.SaveAsync(_config);
        App.Logger.Debug("Periodic service restart settings changed");
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
            var exitCode = await App.ElevatedHelper.ResetAgentFingerprintAsync();
            if (exitCode == 0)
            {
                App.Logger.Info("Agent fingerprint reset completed successfully");
                App.MainWindow.ShowActionStatus(InfoBarSeverity.Success, "Fingerprint reset", "The agent fingerprint was reset and the service was started.");
                return;
            }

            App.Logger.Error($"Agent fingerprint reset failed with helper exit code {exitCode}");
            App.MainWindow.ShowActionStatus(InfoBarSeverity.Error, "Fingerprint reset failed", $"The elevated helper returned exit code {exitCode}.");
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
