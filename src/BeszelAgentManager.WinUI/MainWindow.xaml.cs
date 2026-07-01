using BeszelAgentManager.WinUI.Pages;
using BeszelAgentManager.WinUI.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using Windows.Graphics;
using H.NotifyIcon.Core;
using System.Diagnostics;
using Microsoft.UI.Xaml.Media;

namespace BeszelAgentManager.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly SystemStatusService _systemStatusService = new();
    private readonly ManagerUpdateService _managerUpdateService = new();
    private readonly AgentReleaseService _agentReleaseService = new();
    private readonly ConfigService _configService = new();
    private readonly HubStatusService _hubStatusService = new();
    private readonly TrayIconService _trayIconService;
    private readonly DispatcherQueueTimer _hubStatusTimer;
    private readonly DispatcherQueueTimer _serviceStatusTimer;
    private readonly DispatcherQueueTimer _globalNotificationTimer;
    private readonly DispatcherQueueTimer _managerUpdateTimer;
    private string _hubUrl = string.Empty;
    private bool _refreshingServiceStatus;
    private string _lastServiceState = string.Empty;
    private string _lastHubState = string.Empty;
    private bool _exitRequested;
    private bool _managerUpdateAvailable;
    private bool _checkingManagerUpdate;
    private bool _firstFrameRendered;
    private bool _hiddenPrewarmInProgress;
    private bool _showAfterHiddenPrewarm;
    private PointInt32 _prewarmOriginalPosition;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 1080;
            presenter.PreferredMinimumHeight = 780;
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
        }
        AppWindow.Resize(GetDefaultWindowSize());
        _trayIconService = new TrayIconService(
            ShowFromTray,
            OpenHubFromTray,
            () => RunTrayServiceActionAsync("start", "Start service"),
            () => RunTrayServiceActionAsync("stop", "Stop service"),
            () => RunTrayServiceActionAsync("restart", "Restart service"),
            RunTrayAgentUpdateAsync,
            ExitFromTray);
        AppWindow.Closing += AppWindow_Closing;
        Closed += (_, _) => _trayIconService.Dispose();

        VersionBadgeText.Text = $"v{AppInfo.Version}";
        NavFrame.Navigate(typeof(ConnectionPage));
        _ = RefreshStatusAsync();

        _hubStatusTimer = DispatcherQueue.CreateTimer();
        _hubStatusTimer.Interval = TimeSpan.FromSeconds(15);
        _hubStatusTimer.Tick += async (_, _) => await RefreshHubStatusAsync();
        _hubStatusTimer.Start();

        _serviceStatusTimer = DispatcherQueue.CreateTimer();
        _serviceStatusTimer.Interval = TimeSpan.FromSeconds(2);
        _serviceStatusTimer.Tick += async (_, _) => await RefreshServiceStatusAsync();
        _serviceStatusTimer.Start();

        _globalNotificationTimer = DispatcherQueue.CreateTimer();
        _globalNotificationTimer.Interval = TimeSpan.FromSeconds(10);
        _globalNotificationTimer.Tick += (_, _) =>
        {
            _globalNotificationTimer.Stop();
            GlobalActionInfoBar.IsOpen = false;
            GlobalActionInfoBar.Visibility = Visibility.Collapsed;
        };

        _managerUpdateTimer = DispatcherQueue.CreateTimer();
        _managerUpdateTimer.Interval = TimeSpan.FromMinutes(15);
        _managerUpdateTimer.Tick += async (_, _) => await CheckManagerUpdateInBackgroundAsync();
        _managerUpdateTimer.Start();
        _ = CheckManagerUpdateInBackgroundAsync();

    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exitRequested)
        {
            return;
        }

        args.Cancel = true;
        HideToTray();
    }

    public void HideToTray()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AppWindow.Hide();
            App.Logger.Debug("Manager window hidden to notification area");
        });
    }

    public void ShowFromTray()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_firstFrameRendered)
            {
                ShowInitialWindow();
                return;
            }

            AppWindow.Show();
            Activate();
            App.Logger.Debug("Manager window restored from notification area");
        });
    }

    public void ShowInitialWindow()
    {
        if (_firstFrameRendered)
        {
            AppWindow.Show();
            Activate();
            return;
        }

        if (_hiddenPrewarmInProgress)
        {
            _showAfterHiddenPrewarm = true;
            return;
        }

        SubscribeToFirstFrame();
        Activate();
    }

    public void PrepareHiddenWindow()
    {
        if (_firstFrameRendered || _hiddenPrewarmInProgress)
        {
            return;
        }

        _hiddenPrewarmInProgress = true;
        _prewarmOriginalPosition = AppWindow.Position;
        AppWindow.Move(new PointInt32(-32000, -32000));
        SubscribeToFirstFrame();
        Activate();
    }

    private void SubscribeToFirstFrame()
    {
        if (Content is FrameworkElement root)
        {
            root.Loaded -= MainContent_Loaded;
            root.Loaded += MainContent_Loaded;
        }
    }

    private void MainContent_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement root)
        {
            root.Loaded -= MainContent_Loaded;
        }

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _firstFrameRendered = true;
            if (_hiddenPrewarmInProgress)
            {
                AppWindow.Hide();
                AppWindow.Move(_prewarmOriginalPosition);
                _hiddenPrewarmInProgress = false;
                if (!_showAfterHiddenPrewarm)
                {
                    App.Logger.Debug("Manager window pre-rendered for hidden startup");
                    return;
                }

                _showAfterHiddenPrewarm = false;
                AppWindow.Show();
            }
            Activate();
        });
    }

    private void ExitFromTray()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _exitRequested = true;
            Close();
        });
    }

    private void OpenHubFromTray()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (string.IsNullOrWhiteSpace(_hubUrl))
            {
                _trayIconService.ShowNotification("Hub URL not configured", "Configure a Hub URL in the Connection tab.", NotificationIcon.Warning);
                return;
            }

            App.Logger.Info("Opening hub URL from tray");
            OpenUrl(_hubUrl);
        });
    }

    private async Task RunTrayServiceActionAsync(string action, string displayName)
    {
        App.Logger.Info($"{displayName} requested from tray");
        try
        {
            var exitCode = await App.Broker.RunServiceActionAsync(action);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"The background service returned exit code {exitCode}.");
            }

            await RefreshServiceStatusNowAsync();
            var message = action switch
            {
                "start" => "Beszel Agent is running.",
                "stop" => "Beszel Agent is stopped.",
                "restart" => "Beszel Agent was restarted.",
                _ => "The service action completed.",
            };
            App.Logger.Info($"{displayName} completed successfully from tray");
            _trayIconService.ShowNotification($"{displayName} completed", message);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            App.Logger.Info($"{displayName} cancelled at UAC prompt from tray");
            _trayIconService.ShowNotification($"{displayName} cancelled", "The administrator prompt was cancelled.", NotificationIcon.Warning);
        }
        catch (Exception ex)
        {
            App.Logger.Error($"{displayName} failed from tray: {ex}");
            _trayIconService.ShowNotification($"{displayName} failed", ex.Message, NotificationIcon.Error);
        }
    }

    private async Task RunTrayAgentUpdateAsync()
    {
        App.Logger.Info("Agent update check requested from tray");
        try
        {
            var status = await _systemStatusService.GetAgentStatusAsync();
            if (!status.AgentExeExists)
            {
                _trayIconService.ShowNotification("Beszel Agent is not installed", "Open BeszelAgentManager and install the agent first.", NotificationIcon.Warning);
                return;
            }

            var latest = (await _agentReleaseService.FetchStableReleasesAsync()).FirstOrDefault();
            if (latest is not null
                && !string.Equals(status.AgentVersion, "Unknown", StringComparison.OrdinalIgnoreCase)
                && VersionComparer.IsSameOrOlder(status.AgentVersion, latest.Version))
            {
                App.Logger.Info($"Tray agent update skipped: installed {status.AgentVersion}, latest {latest.Version}");
                _trayIconService.ShowNotification("Beszel Agent is up to date", $"Installed version: {status.AgentVersion}");
                return;
            }

            var exitCode = await App.Broker.UpdateAgentAsync();
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"The background service returned exit code {exitCode}.");
            }

            await RefreshServiceStatusNowAsync();
            var updated = await _systemStatusService.GetAgentStatusAsync();
            App.Logger.Info($"Agent update completed successfully from tray: {updated.AgentVersion}");
            _trayIconService.ShowNotification("Beszel Agent updated", $"Installed version: {updated.AgentVersion}");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            App.Logger.Info("Agent update cancelled at UAC prompt from tray");
            _trayIconService.ShowNotification("Agent update cancelled", "The administrator prompt was cancelled.", NotificationIcon.Warning);
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Agent update failed from tray: {ex}");
            _trayIconService.ShowNotification("Agent update failed", ex.Message, NotificationIcon.Error);
        }
    }

    private SizeInt32 GetDefaultWindowSize()
    {
        const int preferredWidth = 1180;
        const int preferredHeight = 900;
        try
        {
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var workArea = display.WorkArea;
            return new SizeInt32(
                Math.Clamp(preferredWidth, 1080, Math.Max(1080, workArea.Width - 80)),
                Math.Clamp(preferredHeight, 780, Math.Max(780, workArea.Height - 80)));
        }
        catch
        {
            return new SizeInt32(preferredWidth, preferredHeight);
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        switch (item.Tag)
        {
            case "connection":
                App.Logger.Debug("Opened Connection tab");
                NavFrame.Navigate(typeof(ConnectionPage));
                break;
            case "environment":
                App.Logger.Debug("Opened Environment Tables tab");
                NavFrame.Navigate(typeof(EnvironmentPage));
                break;
            case "logging":
                App.Logger.Debug("Opened Logging tab");
                NavFrame.Navigate(typeof(LogsPage));
                break;
            case "agentLogging":
                App.Logger.Debug("Opened Agent Logging tab");
                NavFrame.Navigate(typeof(AgentLoggingPage));
                break;
            case "extra":
                App.Logger.Debug("Opened Extra tab");
                NavFrame.Navigate(typeof(ExtraPage));
                break;
            default:
                throw new InvalidOperationException($"Unknown navigation item tag: {item.Tag}");
        }
    }

    public async Task RefreshStatusAsync()
    {
        await RefreshServiceStatusAsync();
        await RefreshHubStatusAsync();
    }

    public Task RefreshServiceStatusNowAsync()
    {
        return RefreshServiceStatusAsync();
    }

    private async Task RefreshServiceStatusAsync()
    {
        if (_refreshingServiceStatus)
        {
            return;
        }

        _refreshingServiceStatus = true;
        try
        {
            var status = await _systemStatusService.GetAgentStatusAsync();
            HeaderServiceText.Text = $"Service: {status.ServiceState}";
            HeaderAgentText.Text = $"Agent: {status.AgentVersion}";
            if (NavFrame.Content is ConnectionPage connectionPage)
            {
                connectionPage.ApplyServiceStatus(status);
            }
            _trayIconService.SetStatus(status.ServiceState, _managerUpdateAvailable);
            if (!string.Equals(_lastServiceState, status.ServiceState, StringComparison.OrdinalIgnoreCase))
            {
                App.Logger.Debug($"Service status changed: {_lastServiceState} -> {status.ServiceState}");
                _lastServiceState = status.ServiceState;
            }
        }
        finally
        {
            _refreshingServiceStatus = false;
        }
    }

    private async Task CheckManagerUpdateInBackgroundAsync()
    {
        if (_checkingManagerUpdate)
        {
            return;
        }

        _checkingManagerUpdate = true;
        try
        {
            var config = await _configService.LoadAsync();
            if (!config.ManagerUpdateNotifyEnabled)
            {
                _managerUpdateAvailable = false;
                _trayIconService.SetStatus(_lastServiceState, false);
                return;
            }

            if (DateTimeOffset.TryParse(config.ManagerUpdateLastCheckAt, out var lastCheck)
                && DateTimeOffset.Now - lastCheck < TimeSpan.FromHours(Math.Clamp(config.ManagerUpdateCheckIntervalHours, 1, 168)))
            {
                return;
            }

            var release = await _managerUpdateService.FetchLatestReleaseAsync(config.ManagerUpdateIncludePrereleases);
            config.ManagerUpdateLastCheckAt = DateTimeOffset.Now.ToString("O");
            _managerUpdateAvailable = release is not null
                && !VersionComparer.IsSameOrOlder(AppInfo.Version, release.Version)
                && !string.Equals(
                    VersionComparer.Normalize(config.ManagerUpdateSkipVersion),
                    VersionComparer.Normalize(release.Version),
                    StringComparison.OrdinalIgnoreCase);
            _trayIconService.SetStatus(_lastServiceState, _managerUpdateAvailable && config.ManagerUpdateTrayBadgeEnabled);
            if (_managerUpdateAvailable
                && release is not null
                && !string.Equals(config.ManagerUpdateLastNotifiedVersion, release.Version, StringComparison.OrdinalIgnoreCase))
            {
                _trayIconService.ShowNotification(
                    "BeszelAgentManager update available",
                    $"Version {release.Version} is available.");
                config.ManagerUpdateLastNotifiedVersion = release.Version;
            }

            await _configService.SaveAsync(config);
        }
        catch (Exception ex)
        {
            App.Logger.Debug($"Background manager update check failed: {ex.Message}");
        }
        finally
        {
            _checkingManagerUpdate = false;
        }
    }

    private async Task RefreshHubStatusAsync()
    {
        var config = await _configService.LoadAsync();
        var fallbackActive = IsHubFallbackActive()
            && config.HubUrlIpFallbackEnabled
            && !string.IsNullOrWhiteSpace(config.HubUrlIpFallback);
        var status = await _hubStatusService.CheckAsync(fallbackActive ? config.HubUrlIpFallback : config.HubUrl);
        _hubUrl = status.Url;

        if (!status.IsConfigured)
        {
            HubStatusLink.Content = "Hub: Not configured";
        }
        else if (status.IsReachable)
        {
            HubStatusLink.Content = fallbackActive
                ? $"Hub: Fallback reachable ({status.LatencyMilliseconds} ms)"
                : $"Hub: Reachable ({status.LatencyMilliseconds} ms)";
        }
        else
        {
            HubStatusLink.Content = fallbackActive ? "Hub: Fallback unreachable" : "Hub: Unreachable";
        }

        var hubState = HubStatusLink.Content?.ToString() ?? string.Empty;
        if (!string.Equals(_lastHubState, hubState, StringComparison.Ordinal))
        {
            App.Logger.Debug($"Hub status changed: {hubState}");
            _lastHubState = hubState;
        }
    }

    private async void DownloadManagerButton_Click(object sender, RoutedEventArgs e)
    {
        DownloadManagerButton.IsEnabled = false;
        DownloadManagerButton.Content = "Checking...";
        App.Logger.Info("Manager update check requested");

        try
        {
            var config = await _configService.LoadAsync();
            var includePrereleases = config.ExtraFields.TryGetValue("manager_update_include_prereleases", out var includeValue)
                && includeValue.ValueKind is System.Text.Json.JsonValueKind.True;
            var release = await _managerUpdateService.FetchLatestReleaseAsync(includePrereleases);

            if (release is null)
            {
                ShowGlobalStatus(
                    InfoBarSeverity.Warning,
                    "No usable release found",
                    $"GitHub did not return a manager release containing {AppInfo.InstallerAssetName}.");
                return;
            }

            if (VersionComparer.IsSameOrOlder(AppInfo.Version, release.Version))
            {
                ShowGlobalStatus(
                    InfoBarSeverity.Success,
                    "BeszelAgentManager is up to date",
                    $"Installed: {AppInfo.Version}\nLatest on GitHub: {release.Version} ({release.Tag})");
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = NavView.XamlRoot,
                Title = "Manager update available",
                Content = BuildUpdateAvailableContent(release),
                PrimaryButtonText = "Download installer",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await InstallManagerReleaseAsync(release);
            }
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Manager update check failed: {ex}");
            ShowGlobalStatus(InfoBarSeverity.Error, "Update check failed", ex.Message);
        }
        finally
        {
            DownloadManagerButton.IsEnabled = true;
            DownloadManagerButton.Content = "Download manager";
        }
    }

    private async void InstallAgentButton_Click(object sender, RoutedEventArgs e)
    {
        var status = await _systemStatusService.GetAgentStatusAsync();
        var confirmationTitle = status.AgentExeExists
            ? "Agent already installed"
            : "Install Beszel Agent?";
        var confirmationMessage = status.AgentExeExists
            ? $"Beszel Agent {status.AgentVersion} is already installed. Do you really want to reinstall the latest version?"
            : "This will download the latest stable Beszel Agent, install it to Program Files, configure the Windows service, and apply the current Connection settings.";

        await RunAgentOperationAsync(
            InstallAgentButton,
            "Installing...",
            "Install agent",
            confirmationTitle,
            confirmationMessage,
            async () =>
            {
                await SaveConnectionConfigIfActiveAsync();
                var exitCode = await App.Broker.InstallAgentAsync();
                if (exitCode == 0)
                {
                    await MarkCurrentSettingsAppliedAsync();
                    var status = await _systemStatusService.GetAgentStatusAsync();
                    App.Logger.Info($"Beszel Agent installed version: {status.AgentVersion}");
                }

                return exitCode;
            },
            "Beszel Agent was installed and configured.",
            afterSuccess: OfferDefenderExclusionAsync);
    }

    private async void UpdateAgentButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateAgentButton.IsEnabled = false;
        UpdateAgentButton.Content = "Checking...";
        App.Logger.Info("Agent update check requested");
        try
        {
            var status = await _systemStatusService.GetAgentStatusAsync();
            if (!status.AgentExeExists)
            {
                ShowGlobalStatus(
                    InfoBarSeverity.Warning,
                    "Beszel Agent is not installed",
                    "Use Install agent first to install and configure Beszel Agent.");
                return;
            }

            var latest = (await _agentReleaseService.FetchStableReleasesAsync()).FirstOrDefault();
            if (latest is null)
            {
                ShowGlobalStatus(
                    InfoBarSeverity.Warning,
                    "No agent versions found",
                    "GitHub did not return any stable Beszel Agent releases with the Windows agent asset.");
                return;
            }

            if (!string.Equals(status.AgentVersion, "Unknown", StringComparison.OrdinalIgnoreCase)
                && VersionComparer.IsSameOrOlder(status.AgentVersion, latest.Version))
            {
                App.Logger.Info($"Agent update skipped: installed {status.AgentVersion}, latest {latest.Version}");
                ShowGlobalStatus(
                    InfoBarSeverity.Success,
                    "Beszel Agent is up to date",
                    $"Installed: {status.AgentVersion}\nLatest on GitHub: {latest.Version} ({latest.Tag})");
                return;
            }
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Agent update check failed: {ex}");
            ShowGlobalStatus(
                InfoBarSeverity.Warning,
                "Could not check agent version",
                "Continuing with the update will let the background service install the latest stable agent.");
        }
        finally
        {
            UpdateAgentButton.IsEnabled = true;
            UpdateAgentButton.Content = "Update agent";
        }

        await RunAgentOperationAsync(
            UpdateAgentButton,
            "Updating...",
            "Update agent",
            "Update Beszel Agent?",
            "This will download the latest stable Beszel Agent and restart the service.",
            App.Broker.UpdateAgentAsync,
            "Beszel Agent was updated.",
            afterSuccess: LogInstalledAgentVersionAsync);
    }

    private async void UninstallAgentButton_Click(object sender, RoutedEventArgs e)
    {
        var status = await _systemStatusService.GetAgentStatusAsync();
        if (!status.AgentExeExists && !status.ServiceExists)
        {
            App.Logger.Info("Uninstall agent skipped: Beszel Agent is not installed");
            ShowGlobalStatus(
                InfoBarSeverity.Informational,
                "Beszel Agent is not installed",
                "There is no installed agent executable or Windows service to remove.");
            return;
        }

        var installedVersion = status.AgentVersion;
        var keepAgentLogs = await ConfirmAgentUninstallAsync(installedVersion);
        if (keepAgentLogs is null)
        {
            return;
        }

        await RunAgentOperationAsync(
            UninstallAgentButton,
            "Uninstalling...",
            "Uninstall agent",
            "Uninstall Beszel Agent?",
            string.Empty,
            () => App.Broker.UninstallAgentAsync(keepAgentLogs.Value),
            keepAgentLogs.Value
                ? "Beszel Agent was uninstalled. Historical agent logs were kept."
                : "Beszel Agent was uninstalled. Historical agent logs were removed.",
            skipConfirmation: true,
            afterSuccess: async () =>
            {
                App.Logger.Info($"Beszel Agent uninstalled; removed version: {installedVersion}");
                App.Logger.Info(keepAgentLogs.Value
                    ? "Historical agent logs kept during uninstall"
                    : "Historical agent logs removed during uninstall");
                await Task.CompletedTask;
            });
    }

    private async void ManageAgentVersionButton_Click(object sender, RoutedEventArgs e)
    {
        ManageAgentVersionButton.IsEnabled = false;
        ManageAgentVersionButton.Content = "Loading...";
        App.Logger.Info("Opening agent version manager");

        try
        {
            var releases = await _agentReleaseService.FetchStableReleasesAsync();
            if (releases.Count == 0)
            {
                ShowGlobalStatus(
                    InfoBarSeverity.Warning,
                    "No agent versions found",
                    "GitHub did not return any stable Beszel Agent releases with the Windows agent asset.");
                return;
            }

            var picker = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = releases.Select(static release => $"{release.Version} ({release.Tag})").ToList(),
                SelectedIndex = 0,
            };
            var notes = new TextBlock
            {
                Text = BuildReleaseNotesPreview(releases[0].Body),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 260,
            };
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedIndex >= 0 && picker.SelectedIndex < releases.Count)
                {
                    notes.Text = BuildReleaseNotesPreview(releases[picker.SelectedIndex].Body);
                }
            };

            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = "Select agent version:", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(picker);
            panel.Children.Add(notes);

            var dialog = new ContentDialog
            {
                XamlRoot = NavView.XamlRoot,
                Title = "Manage Agent Version",
                Content = panel,
                PrimaryButtonText = "Install",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary || picker.SelectedIndex < 0)
            {
                return;
            }

            var selected = releases[picker.SelectedIndex];
            await RunAgentOperationAsync(
                ManageAgentVersionButton,
                "Installing...",
                "Install agent version",
                $"Install Beszel Agent {selected.Version}?",
                "This will install the selected version and restart the service.",
                () => App.Broker.InstallAgentVersionAsync(selected.Version),
                $"Beszel Agent {selected.Version} was installed.",
                skipConfirmation: true,
                afterSuccess: LogInstalledAgentVersionAsync);
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Agent version manager failed: {ex}");
            ShowGlobalStatus(InfoBarSeverity.Error, "Could not load agent versions", ex.Message);
        }
        finally
        {
            ManageAgentVersionButton.IsEnabled = true;
            ManageAgentVersionButton.Content = "Manage Agent Version...";
        }
    }

    private async void ApplySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsButton.IsEnabled = false;
        ApplySettingsButton.Content = "Applying...";
        App.Logger.Info("Apply settings requested");
        try
        {
            if (NavFrame.Content is ConnectionPage connectionPage)
            {
                await connectionPage.SaveCurrentConfigAsync(logNoChanges: false);
            }

            var config = await _configService.LoadAsync();
            var currentFingerprint = config.ApplyFingerprint();
            var currentManagerTasksFingerprint = config.ManagerTasksFingerprint();
            var serviceChangesPending = string.IsNullOrWhiteSpace(config.LastAppliedFingerprint)
                || !string.Equals(config.LastAppliedFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase);
            var managerTaskChangesPending = string.IsNullOrWhiteSpace(config.LastAppliedManagerTasksFingerprint)
                || !string.Equals(config.LastAppliedManagerTasksFingerprint, currentManagerTasksFingerprint, StringComparison.OrdinalIgnoreCase);
            if (!serviceChangesPending && !managerTaskChangesPending)
            {
                App.Logger.Info("Apply settings skipped: no pending changes detected");
                ShowGlobalStatus(
                    InfoBarSeverity.Informational,
                    "No changes to apply",
                    "The current settings and automatic update schedule are already applied.");
                return;
            }

            if (serviceChangesPending)
            {
                var exitCode = await App.Broker.ApplyConfigurationAsync();
                if (exitCode != 0)
                {
                    throw new InvalidOperationException(GetFriendlyApplyError(exitCode));
                }

                config.LastAppliedFingerprint = currentFingerprint;
                config.LastAppliedAt = DateTime.Now.ToString("s");
                await _configService.SaveAsync(config);
                await RefreshServiceStatusNowAsync();
            }

            if (managerTaskChangesPending)
            {
                var exitCode = await App.Broker.ApplyManagerTasksAsync();
                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"The automatic update schedule could not be updated (helper exit code {exitCode}).");
                }

                config.LastAppliedManagerTasksFingerprint = currentManagerTasksFingerprint;
                await _configService.SaveAsync(config);
            }

            App.Logger.Info("Settings applied successfully");
            ShowGlobalStatus(
                InfoBarSeverity.Success,
                "Settings applied",
                serviceChangesPending
                    ? managerTaskChangesPending
                        ? "The Beszel Agent service configuration and automatic update schedule were updated."
                        : "The configuration was saved and the Beszel Agent service was restarted."
                    : "The automatic update schedule was updated. The Beszel Agent service was not restarted.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            App.Logger.Info("Apply settings cancelled at UAC prompt");
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Apply settings failed: {ex}");
            ShowGlobalStatus(InfoBarSeverity.Error, "Apply settings failed", ex.Message);
        }
        finally
        {
            ApplySettingsButton.IsEnabled = true;
            ApplySettingsButton.Content = "Apply settings";
        }
    }

    private async Task RunAgentOperationAsync(
        Button button,
        string busyText,
        string operationName,
        string confirmationTitle,
        string confirmationMessage,
        Func<Task<int>> operation,
        string successMessage,
        bool skipConfirmation = false,
        Func<Task>? afterSuccess = null)
    {
        if (!skipConfirmation)
        {
            var confirm = new ContentDialog
            {
                XamlRoot = NavView.XamlRoot,
                Title = confirmationTitle,
                Content = new TextBlock
                {
                    Text = confirmationMessage,
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        var originalContent = button.Content;
        SetAgentButtonsEnabled(false);
        button.Content = busyText;
        App.Logger.Info($"{operationName} requested");
        ShowGlobalStatus(
            InfoBarSeverity.Informational,
            $"{operationName} in progress",
            "The background service is completing the operation. This can take a moment while files are downloaded or services are updated.");

        try
        {
            var exitCode = await operation();
            if (exitCode != 0)
            {
                throw new InvalidOperationException(GetFriendlyAgentError(exitCode));
            }

            await RefreshServiceStatusNowAsync();
            if (afterSuccess is not null)
            {
                await afterSuccess();
            }

            App.Logger.Info($"{operationName} completed successfully");
            ShowGlobalStatus(InfoBarSeverity.Success, operationName, successMessage);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            App.Logger.Info($"{operationName} cancelled at UAC prompt");
        }
        catch (Exception ex)
        {
            App.Logger.Error($"{operationName} failed: {ex}");
            ShowGlobalStatus(InfoBarSeverity.Error, $"{operationName} failed", ex.Message);
        }
        finally
        {
            button.Content = originalContent;
            SetAgentButtonsEnabled(true);
        }
    }

    private static string GetFriendlyApplyError(int exitCode)
    {
        if (exitCode == 4 && File.Exists(ManagerPaths.HelperLastErrorPath))
        {
            try
            {
                var detail = File.ReadAllText(ManagerPaths.HelperLastErrorPath).Trim();
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    return detail.Length > 1200
                        ? $"{detail[..1200]}\n\n(truncated)"
                        : detail;
                }
            }
            catch
            {
            }
        }

        return exitCode switch
        {
            3 => "The agent or configuration file could not be found.",
            4 => "The background service could not apply the Beszel Agent service configuration.",
            53 => "NSSM could not be found. Reinstall BeszelAgentManager or place nssm.exe next to the installed app.",
            _ => $"The background service returned exit code {exitCode}.",
        };
    }

    private async Task SaveConnectionConfigIfActiveAsync()
    {
        if (NavFrame.Content is ConnectionPage connectionPage)
        {
            await connectionPage.SaveCurrentConfigAsync();
        }
    }

    private async Task MarkCurrentSettingsAppliedAsync()
    {
        var config = await _configService.LoadAsync();
        config.LastAppliedFingerprint = config.ApplyFingerprint();
        config.LastAppliedAt = DateTime.Now.ToString("s");
        await _configService.SaveAsync(config);
    }

    private async Task<bool?> ConfirmAgentUninstallAsync(string installedVersion)
    {
        var keepLogsCheckBox = new CheckBox
        {
            Content = "Keep historical agent logging",
            IsChecked = true,
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "This will stop and remove the Beszel Agent service, scheduled agent tasks, firewall rule, agent files, legacy agent folder, and temporary agent files.\n\nManager settings and manager logs are kept.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Installed version: {installedVersion}",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(keepLogsCheckBox);

        var dialog = new ContentDialog
        {
            XamlRoot = NavView.XamlRoot,
            Title = "Uninstall Beszel Agent?",
            Content = panel,
            PrimaryButtonText = "Uninstall",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? keepLogsCheckBox.IsChecked == true
            : null;
    }

    private async Task LogInstalledAgentVersionAsync()
    {
        var status = await _systemStatusService.GetAgentStatusAsync();
        App.Logger.Info($"Beszel Agent installed version: {status.AgentVersion}");
    }

    private async Task OfferDefenderExclusionAsync()
    {
        var config = await _configService.LoadAsync();
        if (config.DefenderExclusionPrompted || config.DefenderExclusionEnabled)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = NavView.XamlRoot,
            Title = "Optional Microsoft Defender exclusion",
            Content = new TextBlock
            {
                Text = "Add an exclusion only for the BeszelAgentManager installation folder? This is optional and should only be enabled if Defender interferes with the application.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Add exclusion",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Close,
        };
        var add = await dialog.ShowAsync() == ContentDialogResult.Primary;
        config.DefenderExclusionPrompted = true;
        if (add)
        {
            var exitCode = await App.Broker.SetDefenderExclusionAsync(true);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"The background service could not add the Defender exclusion (code {exitCode}).");
            }

            config.DefenderExclusionEnabled = true;
            App.Logger.Info("Windows Defender exclusion added after agent installation");
        }

        await _configService.SaveAsync(config);
    }

    private void SetAgentButtonsEnabled(bool enabled)
    {
        InstallAgentButton.IsEnabled = enabled;
        UpdateAgentButton.IsEnabled = enabled;
        DownloadManagerButton.IsEnabled = enabled;
        ApplySettingsButton.IsEnabled = enabled;
        UninstallAgentButton.IsEnabled = enabled;
        ManageAgentVersionButton.IsEnabled = enabled;
    }

    private static string GetFriendlyAgentError(int exitCode)
    {
        return exitCode switch
        {
            3 => "The agent or configuration file could not be found.",
            20 => "Could not find a usable Beszel Agent release on GitHub.",
            21 => "The downloaded archive did not contain beszel-agent.exe.",
            22 => "The agent could not be downloaded or installed. Check the log and antivirus quarantine.",
            23 => "One or more agent folders could not be removed. Stop the service and close any open agent logs or folders, then try again.",
            53 => "NSSM could not be found. Reinstall BeszelAgentManager or place nssm.exe next to the installed app.",
            _ => $"The background service returned error code {exitCode}.",
        };
    }

    private static string BuildReleaseNotesPreview(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(No release notes.)";
        }

        var lines = body.Replace("\r\n", "\n").Split('\n')
            .Select(static line => line.TrimEnd())
            .SkipWhile(static line => string.IsNullOrWhiteSpace(line))
            .ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count > 18)
        {
            lines = lines.Take(18).Append("...").Append("(truncated)").ToList();
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async void ManageManagerVersionButton_Click(object sender, RoutedEventArgs e)
    {
        App.Logger.Info("Manager version manager requested");
        try
        {
            var config = await _configService.LoadAsync();
            var releases = await _managerUpdateService.FetchReleasesAsync(config.ManagerUpdateIncludePrereleases);
            if (releases.Count == 0)
            {
                ShowGlobalStatus(InfoBarSeverity.Warning, "No manager versions found", "No usable installer releases were returned by GitHub.");
                return;
            }

            var picker = new ComboBox
            {
                ItemsSource = releases.Select(static release => $"{release.Version} ({release.Tag})").ToList(),
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var notes = new TextBlock
            {
                Text = FullReleaseNotes(releases[0].Body),
                TextWrapping = TextWrapping.Wrap,
            };
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedIndex >= 0)
                {
                    notes.Text = FullReleaseNotes(releases[picker.SelectedIndex].Body);
                }
            };

            var force = new CheckBox { Content = "Force reinstall even when this version is already installed" };
            var versionSection = new StackPanel { Spacing = 10 };
            versionSection.Children.Add(new TextBlock { Text = "Install, roll back, or reinstall", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            versionSection.Children.Add(new TextBlock
            {
                Text = "Choose a release to install. Installing an older release performs a rollback.",
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
            versionSection.Children.Add(picker);
            versionSection.Children.Add(new ScrollViewer
            {
                Content = notes,
                Height = 330,
                Padding = new Thickness(10),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            });
            versionSection.Children.Add(force);

            var panel = new StackPanel { Width = 650 };
            panel.Children.Add(new Border
            {
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                Child = versionSection,
            });

            var dialog = new ContentDialog
            {
                XamlRoot = NavView.XamlRoot,
                Title = "Manage Manager Version",
                Content = panel,
                PrimaryButtonText = "Install selected version",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            var selected = releases[Math.Max(0, picker.SelectedIndex)];
            if (!force.IsChecked.GetValueOrDefault()
                && string.Equals(VersionComparer.Normalize(selected.Version), VersionComparer.Normalize(AppInfo.Version), StringComparison.OrdinalIgnoreCase))
            {
                ShowGlobalStatus(InfoBarSeverity.Warning, "Force reinstall required", "Enable force reinstall to install the currently installed manager version.");
                return;
            }

            await InstallManagerReleaseAsync(selected);
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Manager version manager failed: {ex}");
            ShowGlobalStatus(InfoBarSeverity.Error, "Manager version manager failed", ex.Message);
        }
    }

    public async Task ShowManagerUpdateSettingsAsync()
    {
        var config = await _configService.LoadAsync();
        var notify = new CheckBox { Content = "Notify me when a manager update is available", IsChecked = config.ManagerUpdateNotifyEnabled };
        var badge = new CheckBox { Content = "Show update availability in the tray status", IsChecked = config.ManagerUpdateTrayBadgeEnabled };
        var prereleases = new CheckBox { Content = "Include release candidates and other prereleases", IsChecked = config.ManagerUpdateIncludePrereleases };
        var skipVersion = new TextBox
        {
            Header = "Skip notifications for version (optional)",
            Text = config.ManagerUpdateSkipVersion,
            PlaceholderText = "Example: 4.0.1",
        };
        var interval = new NumberBox
        {
            Minimum = 1,
            Maximum = 168,
            Value = Math.Clamp(config.ManagerUpdateCheckIntervalHours, 1, 168),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 100,
        };
        var intervalRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        intervalRow.Children.Add(new TextBlock { Text = "Check every", VerticalAlignment = VerticalAlignment.Center });
        intervalRow.Children.Add(interval);
        intervalRow.Children.Add(new TextBlock { Text = "hour(s)", VerticalAlignment = VerticalAlignment.Center });
        var panel = new StackPanel { Spacing = 10, Width = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = "Control automatic GitHub checks and update notifications. Updates are installed only when you explicitly choose a version or approve Download manager.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        panel.Children.Add(notify);
        panel.Children.Add(badge);
        panel.Children.Add(prereleases);
        panel.Children.Add(intervalRow);
        panel.Children.Add(skipVersion);

        var dialog = new ContentDialog
        {
            XamlRoot = NavView.XamlRoot,
            Title = "Manager update settings",
            Content = panel,
            PrimaryButtonText = "Save settings",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        config.ManagerUpdateNotifyEnabled = notify.IsChecked == true;
        config.ManagerUpdateTrayBadgeEnabled = badge.IsChecked == true;
        config.ManagerUpdateIncludePrereleases = prereleases.IsChecked == true;
        config.ManagerUpdateCheckIntervalHours = double.IsNaN(interval.Value)
            ? 6
            : Math.Clamp((int)interval.Value, 1, 168);
        config.ManagerUpdateSkipVersion = skipVersion.Text.Trim();
        config.ManagerUpdateLastCheckAt = string.Empty;
        await _configService.SaveAsync(config);
        App.Logger.Info("Manager update settings saved");
        ShowGlobalStatus(InfoBarSeverity.Success, "Manager update settings saved", "The new update preferences are active.");
        _ = CheckManagerUpdateInBackgroundAsync();
    }

    private static string FullReleaseNotes(string? body)
    {
        return string.IsNullOrWhiteSpace(body) ? "(No release notes.)" : body.Trim();
    }

    private async Task InstallManagerReleaseAsync(ManagerRelease release)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = NavView.XamlRoot,
            Title = $"Install BeszelAgentManager {release.Version}?",
            Content = new TextBlock
            {
                Text = "The background service will download and verify the official installer, close this app, install silently, and reopen the manager.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Install",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var relauncher = StartUpdateRelauncher();
        try
        {
            ShowGlobalStatus(InfoBarSeverity.Informational, "Preparing manager update", "Downloading and verifying the official installer.");
            var exitCode = await App.Broker.InstallManagerVersionAsync(release.Tag);
            if (exitCode != 0)
            {
                throw new InvalidOperationException(exitCode switch
                {
                    30 => "The selected release or its required installer/checksum assets could not be found.",
                    31 => "The downloaded manager installer failed SHA-256 verification.",
                    _ => $"The background service could not stage the manager installer (code {exitCode}).",
                });
            }

            App.Logger.Info($"Manager update {release.Tag} verified and scheduled");
            _exitRequested = true;
            Close();
        }
        catch
        {
            if (!relauncher.HasExited)
            {
                relauncher.Kill(entireProcessTree: true);
            }
            relauncher.Dispose();
            throw;
        }
    }

    private static Process StartUpdateRelauncher()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"BeszelAgentManager-relaunch-{Guid.NewGuid():N}.ps1");
        var executable = (Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "BeszelAgentManager.exe")).Replace("'", "''", StringComparison.Ordinal);
        var script = $$"""
            $deadline = (Get-Date).AddMinutes(15)
            while (Get-Process -Id {{Environment.ProcessId}} -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 1 }
            Start-Sleep -Seconds 8
            while ((Get-Date) -lt $deadline) {
              if (Test-Path -LiteralPath '{{executable}}') {
                try {
                  $stream = [System.IO.File]::Open('{{executable}}', 'Open', 'Read', 'ReadWrite')
                  $stream.Dispose()
                  Start-Process -FilePath '{{executable}}'
                  break
                } catch { }
              }
              Start-Sleep -Seconds 2
            }
            Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
            """;
        File.WriteAllText(scriptPath, script);
        return Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-File", scriptPath },
        }) ?? throw new InvalidOperationException("Could not start the manager update relauncher.");
    }

    private void VersionBadgeButton_Click(object sender, RoutedEventArgs e)
    {
        App.Logger.Info("Opening manager release link");
        OpenUrl($"https://github.com/{AppInfo.ManagerRepo}/releases");
    }

    private void HubStatusLink_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_hubUrl))
        {
            App.Logger.Info($"Opening hub URL: {_hubUrl}");
            OpenUrl(_hubUrl);
        }
    }

    private void AboutLink_Click(object sender, RoutedEventArgs e)
    {
        App.Logger.Info("Opening BeszelAgentManager GitHub page");
        OpenUrl($"https://github.com/{AppInfo.ManagerRepo}");
    }

    private void AboutBeszelLink_Click(object sender, RoutedEventArgs e)
    {
        App.Logger.Info("Opening Beszel website");
        OpenUrl("https://beszel.dev");
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = NavView.XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }

    private void ShowGlobalStatus(InfoBarSeverity severity, string title, string message)
    {
        GlobalActionInfoBar.Severity = severity;
        GlobalActionInfoBar.Title = title;
        GlobalActionInfoBar.Message = message;
        GlobalActionInfoBar.Visibility = Visibility.Visible;
        GlobalActionInfoBar.IsOpen = true;
        _globalNotificationTimer.Stop();
        _globalNotificationTimer.Start();
    }

    public void ShowActionStatus(InfoBarSeverity severity, string title, string message)
    {
        ShowGlobalStatus(severity, title, message);
    }

    private static bool IsHubFallbackActive()
    {
        try
        {
            if (!File.Exists(ManagerPaths.DnsFallbackStatePath))
            {
                return false;
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ManagerPaths.DnsFallbackStatePath));
            return document.RootElement.TryGetProperty("active", out var active)
                && active.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static FrameworkElement BuildUpdateAvailableContent(ManagerRelease release)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Installed: {AppInfo.Version}\nAvailable: {release.Version} ({release.Tag})",
            TextWrapping = TextWrapping.Wrap,
        });

        if (!string.IsNullOrWhiteSpace(release.Body))
        {
            var notes = release.Body.Length > 1500
                ? $"{release.Body[..1500]}\n\n(truncated)"
                : release.Body;
            panel.Children.Add(new TextBlock
            {
                Text = notes,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 300,
            });
        }

        return panel;
    }

    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            App.Logger.Warning($"Blocked non-HTTP URL launch: {url}");
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true,
        });
    }
}
