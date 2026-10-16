using BeszelAgentManager.WinUI.Pages;
using BeszelAgentManager.WinUI.Services;
using BeszelAgentManager.Core.Updates;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using Windows.Graphics;
using H.NotifyIcon.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;

namespace BeszelAgentManager.WinUI;

public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan VisibleServiceStatusInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HiddenServiceStatusInterval = TimeSpan.FromMinutes(1);
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
    private readonly CancellationTokenSource _shutdown = new();
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
        ((FrameworkElement)Content).Language = Localization.Language;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ConfigureWindowSize();
        _trayIconService = new TrayIconService(
            ShowFromTray,
            OpenHubFromTray,
            () => RunTrayServiceActionAsync("start", Localization.Get("StartService")),
            () => RunTrayServiceActionAsync("stop", Localization.Get("StopService")),
            () => RunTrayServiceActionAsync("restart", Localization.Get("RestartService")),
            RunTrayAgentUpdateAsync,
            ExitFromTray);
        AppWindow.Closing += AppWindow_Closing;
        Closed += (_, _) =>
        {
            StopBackgroundWork();
            _trayIconService.Dispose();
            _shutdown.Dispose();
        };

        VersionBadgeText.Text = $"v{AppInfo.ReleaseTag}{AppInfo.RuntimeVariantVersionSuffix}";
        NavFrame.Navigate(typeof(ConnectionPage));
        _ = RefreshStatusAsync();

        _hubStatusTimer = DispatcherQueue.CreateTimer();
        _hubStatusTimer.Interval = TimeSpan.FromSeconds(15);
        _hubStatusTimer.Tick += async (_, _) => await RefreshHubStatusAsync();
        _hubStatusTimer.Start();

        _serviceStatusTimer = DispatcherQueue.CreateTimer();
        _serviceStatusTimer.Interval = VisibleServiceStatusInterval;
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
            _serviceStatusTimer.Interval = HiddenServiceStatusInterval;
            AppWindow.Hide();
            App.Logger.Debug("Manager window hidden to notification area");
        });
    }

    public void ShowFromTray()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _serviceStatusTimer.Interval = VisibleServiceStatusInterval;
            _ = RefreshServiceStatusNowAsync();
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
                _serviceStatusTimer.Interval = HiddenServiceStatusInterval;
                AppWindow.Hide();
                AppWindow.Move(_prewarmOriginalPosition);
                _hiddenPrewarmInProgress = false;
                if (!_showAfterHiddenPrewarm)
                {
                    App.Logger.Debug("Manager window pre-rendered for hidden startup");
                    return;
                }

                _showAfterHiddenPrewarm = false;
                _serviceStatusTimer.Interval = VisibleServiceStatusInterval;
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
            StopBackgroundWork();
            Close();
        });
    }

    private void StopBackgroundWork()
    {
        if (!_shutdown.IsCancellationRequested)
        {
            _shutdown.Cancel();
        }

        _hubStatusTimer.Stop();
        _serviceStatusTimer.Stop();
        _globalNotificationTimer.Stop();
        _managerUpdateTimer.Stop();
    }

    private void OpenHubFromTray()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (string.IsNullOrWhiteSpace(_hubUrl))
            {
                _trayIconService.ShowNotification(Localization.Get("HubURLNotConfigured"), Localization.Get("ConfigureAHubURLInTheConnectionTab"), NotificationIcon.Warning);
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
                throw new InvalidOperationException(Localization.Get("TheBackgroundServiceReturnedExitCode0", exitCode));
            }

            await RefreshServiceStatusNowAsync();
            var message = action switch
            {
                "start" => Localization.Get("BeszelAgentIsRunning"),
                "stop" => Localization.Get("BeszelAgentIsStopped"),
                "restart" => Localization.Get("BeszelAgentWasRestarted"),
                _ => Localization.Get("TheServiceActionCompleted"),
            };
            App.Logger.Info($"{displayName} completed successfully from tray");
            _trayIconService.ShowNotification(Localization.Get("0Completed", displayName), message);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            App.Logger.Info($"{displayName} cancelled at UAC prompt from tray");
            _trayIconService.ShowNotification(Localization.Get("0Cancelled", displayName), Localization.Get("TheAdministratorPromptWasCancelled"), NotificationIcon.Warning);
        }
        catch (Exception ex)
        {
            App.Logger.Error($"{displayName} failed from tray: {ex}");
            _trayIconService.ShowNotification(Localization.Get("0Failed", displayName), ex.Message, NotificationIcon.Error);
        }
    }

    private async Task RunTrayAgentUpdateAsync()
    {
        App.Logger.Info("Agent update check requested from tray");
        try
        {
            var status = await _systemStatusService.GetAgentStatusAsync(_shutdown.Token);
            if (!status.AgentExeExists)
            {
                _trayIconService.ShowNotification(Localization.Get("BeszelAgentIsNotInstalled"), Localization.Get("OpenBeszelAgentManagerAndInstallTheAgentFirst"), NotificationIcon.Warning);
                return;
            }

            var latest = (await _agentReleaseService.FetchStableReleasesAsync()).FirstOrDefault();
            if (latest is not null
                && !string.Equals(status.AgentVersion, "Unknown", StringComparison.OrdinalIgnoreCase)
                && VersionComparer.IsSameOrOlder(status.AgentVersion, latest.Version))
            {
                App.Logger.Info($"Tray agent update skipped: installed {status.AgentVersion}, latest {latest.Version}");
                _trayIconService.ShowNotification(Localization.Get("BeszelAgentIsUpToDate"), Localization.Get("InstalledVersion0", status.AgentVersion));
                return;
            }

            var exitCode = await App.Broker.UpdateAgentAsync();
            if (exitCode != 0)
            {
                throw new InvalidOperationException(Localization.Get("TheBackgroundServiceReturnedExitCode0", exitCode));
            }

            await RefreshServiceStatusNowAsync();
            var updated = await _systemStatusService.GetAgentStatusAsync(_shutdown.Token);
            App.Logger.Info($"Agent update completed successfully from tray: {updated.AgentVersion}");
            _trayIconService.ShowNotification(Localization.Get("BeszelAgentUpdated"), Localization.Get("InstalledVersion0", updated.AgentVersion));
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            App.Logger.Info("Agent update cancelled at UAC prompt from tray");
            _trayIconService.ShowNotification(Localization.Get("AgentUpdateCancelled"), Localization.Get("TheAdministratorPromptWasCancelled"), NotificationIcon.Warning);
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Agent update failed from tray: {ex}");
            _trayIconService.ShowNotification(Localization.Get("AgentUpdateFailed"), ex.Message, NotificationIcon.Error);
        }
    }

    private void ConfigureWindowSize()
    {
        const int logicalPreferredWidth = 1180;
        const int logicalPreferredHeight = 900;
        const int logicalMinimumWidth = 1080;
        const int logicalMinimumHeight = 780;
        const int logicalWorkAreaMargin = 48;

        var scale = GetWindowScale();
        var preferredWidth = ScalePixels(logicalPreferredWidth, scale);
        var preferredHeight = ScalePixels(logicalPreferredHeight, scale);
        var minimumWidth = ScalePixels(logicalMinimumWidth, scale);
        var minimumHeight = ScalePixels(logicalMinimumHeight, scale);
        try
        {
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var workArea = display.WorkArea;
            var margin = ScalePixels(logicalWorkAreaMargin, scale);
            var maximumWidth = Math.Max(640, workArea.Width - margin);
            var maximumHeight = Math.Max(480, workArea.Height - margin);
            minimumWidth = Math.Min(minimumWidth, maximumWidth);
            minimumHeight = Math.Min(minimumHeight, maximumHeight);
            preferredWidth = Math.Clamp(preferredWidth, minimumWidth, maximumWidth);
            preferredHeight = Math.Clamp(preferredHeight, minimumHeight, maximumHeight);
        }
        catch
        {
            // Use the DPI-scaled preferred dimensions if display discovery is unavailable.
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = minimumWidth;
            presenter.PreferredMinimumHeight = minimumHeight;
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
        }

        AppWindow.Resize(new SizeInt32(preferredWidth, preferredHeight));
    }

    private double GetWindowScale()
    {
        try
        {
            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var dpi = GetDpiForWindow(windowHandle);
            return dpi > 0 ? dpi / 96d : 1d;
        }
        catch
        {
            return 1d;
        }
    }

    private static int ScalePixels(int logicalPixels, double scale) =>
        Math.Max(1, (int)Math.Round(logicalPixels * scale, MidpointRounding.AwayFromZero));

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

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
                throw new InvalidOperationException(Localization.Get("UnknownNavigationItemTag0", item.Tag));
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
        if (_refreshingServiceStatus || _shutdown.IsCancellationRequested)
        {
            return;
        }

        _refreshingServiceStatus = true;
        try
        {
            var status = await _systemStatusService.GetAgentStatusAsync(_shutdown.Token);
            HeaderServiceText.Text = Localization.Get("Service0", Localization.Status(status.ServiceState));
            HeaderAgentText.Text = Localization.Get("Agent0", Localization.Status(status.AgentVersion));
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
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            _refreshingServiceStatus = false;
        }
    }

    private async Task CheckManagerUpdateInBackgroundAsync()
    {
        if (_checkingManagerUpdate || _shutdown.IsCancellationRequested)
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
                && IsManagerUpdateAvailable(release, config.ManagerUpdateIncludePrereleases)
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
                    Localization.Get("BeszelAgentManagerUpdateAvailable"),
                    Localization.Get("Version0IsAvailable", release.Version));
                config.ManagerUpdateLastNotifiedVersion = release.Version;
            }

            await _configService.SaveAsync(config);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
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
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        var config = await _configService.LoadAsync();
        var fallbackActive = IsHubFallbackActive()
            && config.HubUrlIpFallbackEnabled
            && !string.IsNullOrWhiteSpace(config.HubUrlIpFallback);
        var status = await _hubStatusService.CheckAsync(fallbackActive ? config.HubUrlIpFallback : config.HubUrl);
        _hubUrl = status.Url;

        if (!status.IsConfigured)
        {
            HubStatusLink.Content = Localization.Get("HubNotConfigured");
        }
        else if (status.IsReachable)
        {
            HubStatusLink.Content = fallbackActive
                ? Localization.Get("HubFallbackReachable0Ms", status.LatencyMilliseconds)
                : Localization.Get("HubReachable0Ms", status.LatencyMilliseconds);
        }
        else
        {
            HubStatusLink.Content = fallbackActive ? Localization.Get("HubFallbackUnreachable") : Localization.Get("HubUnreachable");
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
        DownloadManagerButton.Content = Localization.Get("Checking");
        App.Logger.Info("Manager update check requested");

        try
        {
            var config = await _configService.LoadAsync();
            var release = await _managerUpdateService.FetchLatestReleaseAsync(config.ManagerUpdateIncludePrereleases);

            if (release is null)
            {
                ShowGlobalStatus(
                    InfoBarSeverity.Warning,
                    Localization.Get("NoUsableReleaseFound"),
                    Localization.Get("GitHubDidNotReturnAManagerReleaseContaining0", AppInfo.InstallerAssetName));
                return;
            }

            if (!IsManagerUpdateAvailable(release, config.ManagerUpdateIncludePrereleases))
            {
                ShowGlobalStatus(
                    InfoBarSeverity.Success,
                    Localization.Get("BeszelAgentManagerIsUpToDate"),
                    Localization.Get("Installed0LatestOnGitHub12", AppInfo.ReleaseTag, release.Version, release.Tag));
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = NavView.XamlRoot,
                Title = Localization.Get("ManagerUpdateAvailable"),
                Content = BuildUpdateAvailableContent(release),
                PrimaryButtonText = Localization.Get("UpdateManager"),
                CloseButtonText = Localization.Get("Cancel"),
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
            ShowGlobalStatus(InfoBarSeverity.Warning, Localization.Get("CouldNotCheckForUpdates"), Localization.Get("PleaseTryAgainLaterTechnicalDetailsAreAvailableIn"));
        }
        finally
        {
            DownloadManagerButton.IsEnabled = true;
            DownloadManagerButton.Content = Localization.Get("UpdateManager");
        }
    }

    private async void InstallAgentButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureBackgroundServiceAvailableAsync())
        {
            return;
        }

        var status = await _systemStatusService.GetAgentStatusAsync();
        var confirmationTitle = status.AgentExeExists
            ? Localization.Get("AgentAlreadyInstalled")
            : Localization.Get("InstallBeszelAgent");
        var confirmationMessage = status.AgentExeExists
            ? Localization.Get("BeszelAgent0IsAlreadyInstalledDoYouReally", status.AgentVersion)
            : Localization.Get("ThisWillDownloadTheLatestStableBeszelAgentInstall");

        await RunAgentOperationAsync(
            InstallAgentButton,
            Localization.Get("Installing"),
            Localization.Get("InstallAgent"),
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
            Localization.Get("BeszelAgentWasInstalledAndConfigured"),
            afterSuccess: OfferDefenderExclusionAsync);
    }

    private async Task<bool> EnsureBackgroundServiceAvailableAsync()
    {
        var serviceManager = new BackgroundServiceManagementService();
        var serviceStatus = await serviceManager.GetStatusAsync();
        if (serviceStatus.IsInstalled
            && string.Equals(serviceStatus.State, "RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = NavView.XamlRoot,
            Title = Localization.Get("BackgroundServiceRequired"),
            Content = serviceStatus.IsInstalled
                ? Localization.Get("TheBeszelAgentManagerBackgroundServiceIsInstalledButNotRunning")
                : Localization.Get("TheBeszelAgentManagerBackgroundServiceIsNotInstalledInstallIt"),
            PrimaryButtonText = Localization.Get("InstallNow"),
            SecondaryButtonText = Localization.Get("OpenExtra"),
            CloseButtonText = Localization.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary)
        {
            NavigateToExtraPage();
            return false;
        }

        if (result != ContentDialogResult.Primary)
        {
            return false;
        }

        try
        {
            InstallAgentButton.IsEnabled = false;
            InstallAgentButton.Content = Localization.Get("InstallingService");
            var exitCode = await serviceManager.InstallAsync();
            var refreshedStatus = await serviceManager.GetStatusAsync();
            if (exitCode == 0
                && refreshedStatus.IsInstalled
                && string.Equals(refreshedStatus.State, "RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                ShowGlobalStatus(
                    InfoBarSeverity.Success,
                    Localization.Get("BackgroundServiceInstalled"),
                    Localization.Get("ContinuingWithBeszelAgentInstallation"));
                return true;
            }

            var helperError = BackgroundServiceManagementService.ReadLastHelperError();
            ShowGlobalStatus(
                InfoBarSeverity.Error,
                Localization.Get("BackgroundServiceInstallationFailed"),
                string.IsNullOrWhiteSpace(helperError)
                    ? Localization.Get("TheHelperReturnedExitCode0OpenExtraTo", exitCode)
                    : helperError);
            NavigateToExtraPage();
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ShowGlobalStatus(
                InfoBarSeverity.Warning,
                Localization.Get("BackgroundServiceInstallationCancelled"),
                Localization.Get("InstallTheBackgroundServiceFromExtraBeforeInstallingBeszel"));
            return false;
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Background service pre-check failed: {ex}");
            ShowGlobalStatus(
                InfoBarSeverity.Error,
                Localization.Get("BackgroundServiceInstallationFailed"),
                ex.Message);
            NavigateToExtraPage();
            return false;
        }
        finally
        {
            InstallAgentButton.IsEnabled = true;
            InstallAgentButton.Content = Localization.Get("InstallAgent");
        }
    }

    private void NavigateToExtraPage()
    {
        var extraItem = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), "extra", StringComparison.Ordinal));
        if (extraItem is not null)
        {
            NavView.SelectedItem = extraItem;
        }
        else
        {
            NavFrame.Navigate(typeof(ExtraPage));
        }
    }

    private async void UpdateAgentButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateAgentButton.IsEnabled = false;
        UpdateAgentButton.Content = Localization.Get("Checking");
        App.Logger.Info("Agent update check requested");
        try
        {
            var status = await _systemStatusService.GetAgentStatusAsync();
            if (!status.AgentExeExists)
            {
                ShowGlobalStatus(
                    InfoBarSeverity.Warning,
                    Localization.Get("BeszelAgentIsNotInstalled"),
                    Localization.Get("UseInstallAgentFirstToInstallAndConfigureBeszel"));
                return;
            }

            var latest = (await _agentReleaseService.FetchStableReleasesAsync()).FirstOrDefault();
            if (latest is null)
            {
                ShowGlobalStatus(
                    InfoBarSeverity.Warning,
                    Localization.Get("NoAgentVersionsFound"),
                    Localization.Get("GitHubDidNotReturnAnyStableBeszelAgentReleases"));
                return;
            }

            if (!string.Equals(status.AgentVersion, "Unknown", StringComparison.OrdinalIgnoreCase)
                && VersionComparer.IsSameOrOlder(status.AgentVersion, latest.Version))
            {
                App.Logger.Info($"Agent update skipped: installed {status.AgentVersion}, latest {latest.Version}");
                ShowGlobalStatus(
                    InfoBarSeverity.Success,
                    Localization.Get("BeszelAgentIsUpToDate"),
                    Localization.Get("Installed0LatestOnGitHub12", status.AgentVersion, latest.Version, latest.Tag));
                return;
            }
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Agent update check failed: {ex}");
            ShowGlobalStatus(
                InfoBarSeverity.Warning,
                Localization.Get("CouldNotCheckAgentVersion"),
                Localization.Get("ContinuingWithTheUpdateWillLetTheBackgroundService"));
        }
        finally
        {
            UpdateAgentButton.IsEnabled = true;
            UpdateAgentButton.Content = Localization.Get("UpdateAgent");
        }

        await RunAgentOperationAsync(
            UpdateAgentButton,
            Localization.Get("Updating"),
            Localization.Get("UpdateAgent"),
            Localization.Get("UpdateBeszelAgent"),
            Localization.Get("ThisWillDownloadTheLatestStableBeszelAgentAnd"),
            App.Broker.UpdateAgentAsync,
            Localization.Get("BeszelAgentWasUpdated"),
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
                Localization.Get("BeszelAgentIsNotInstalled"),
                Localization.Get("ThereIsNoInstalledAgentExecutableOrWindowsService"));
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
            Localization.Get("Uninstalling"),
            Localization.Get("UninstallAgent"),
            Localization.Get("UninstallBeszelAgent"),
            string.Empty,
            () => App.Broker.UninstallAgentAsync(keepAgentLogs.Value),
            keepAgentLogs.Value
                ? Localization.Get("BeszelAgentWasUninstalledHistoricalAgentLogsWereKept")
                : Localization.Get("BeszelAgentWasUninstalledHistoricalAgentLogsWereRemoved"),
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
        ManageAgentVersionButton.Content = Localization.Get("Loading");
        App.Logger.Info("Opening agent version manager");

        try
        {
            var releases = await _agentReleaseService.FetchStableReleasesAsync();
            if (releases.Count == 0)
            {
                ShowGlobalStatus(
                    InfoBarSeverity.Warning,
                    Localization.Get("NoAgentVersionsFound"),
                    Localization.Get("GitHubDidNotReturnAnyStableBeszelAgentReleases"));
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
            panel.Children.Add(new TextBlock { Text = Localization.Get("SelectAgentVersion"), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(picker);
            panel.Children.Add(notes);

            var dialog = new ContentDialog
            {
                XamlRoot = NavView.XamlRoot,
                Title = Localization.Get("ManageAgentVersion"),
                Content = panel,
                PrimaryButtonText = Localization.Get("Install"),
                CloseButtonText = Localization.Get("Cancel"),
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
                Localization.Get("Installing"),
                Localization.Get("InstallAgentVersion"),
                Localization.Get("InstallBeszelAgent0", selected.Version),
                Localization.Get("ThisWillInstallTheSelectedVersionAndRestartThe"),
                () => App.Broker.InstallAgentVersionAsync(selected.Version),
                Localization.Get("BeszelAgent0WasInstalled", selected.Version),
                skipConfirmation: true,
                afterSuccess: LogInstalledAgentVersionAsync);
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Agent version manager failed: {ex}");
            ShowGlobalStatus(InfoBarSeverity.Error, Localization.Get("CouldNotLoadAgentVersions"), ex.Message);
        }
        finally
        {
            ManageAgentVersionButton.IsEnabled = true;
            ManageAgentVersionButton.Content = Localization.Get("ManageAgentVersion2");
        }
    }

    private async void ApplySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsButton.IsEnabled = false;
        ApplySettingsButton.Content = Localization.Get("Applying");
        App.Logger.Info("Apply settings requested");
        try
        {
            if (NavFrame.Content is ConnectionPage connectionPage)
            {
                await connectionPage.SaveCurrentConfigAsync(logNoChanges: false);
            }

            var config = await _configService.LoadAsync();
            if (config.HasRetryStrategyConflict())
            {
                App.Logger.Warning("Apply settings blocked: EXIT_ON_DNS_ERROR conflicts with WebSocket offline backoff");
                ShowGlobalStatus(
                    InfoBarSeverity.Error,
                    Localization.Get("ConflictingRetrySettings"),
                    Localization.Get("EXITONDNSERRORCannotBeEnabledTogetherWith"));
                return;
            }

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
                    Localization.Get("NoChangesToApply"),
                    Localization.Get("TheCurrentSettingsAndAutomaticUpdateScheduleAreAlready"));
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
                    throw new InvalidOperationException(Localization.Get("TheAutomaticUpdateScheduleCouldNotBeUpdatedHelper", exitCode));
                }

                config.LastAppliedManagerTasksFingerprint = currentManagerTasksFingerprint;
                await _configService.SaveAsync(config);
            }

            App.Logger.Info("Settings applied successfully");
            ShowGlobalStatus(
                InfoBarSeverity.Success,
                Localization.Get("SettingsApplied"),
                serviceChangesPending
                    ? managerTaskChangesPending
                        ? Localization.Get("TheBeszelAgentServiceConfigurationAndAutomaticUpdateSchedule")
                        : Localization.Get("TheConfigurationWasSavedAndTheBeszelAgentService")
                    : Localization.Get("TheAutomaticUpdateScheduleWasUpdatedTheBeszelAgent"));
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            App.Logger.Info("Apply settings cancelled at UAC prompt");
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Apply settings failed: {ex}");
            ShowGlobalStatus(InfoBarSeverity.Error, Localization.Get("ApplySettingsFailed"), ex.Message);
        }
        finally
        {
            ApplySettingsButton.IsEnabled = true;
            ApplySettingsButton.Content = Localization.Get("ApplySettings");
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
                PrimaryButtonText = Localization.Get("Continue"),
                CloseButtonText = Localization.Get("Cancel"),
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
            Localization.Get("0InProgress", operationName),
            Localization.Get("TheBackgroundServiceIsCompletingTheOperationThisCan"));

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
            ShowGlobalStatus(InfoBarSeverity.Error, Localization.Get("0Failed", operationName), ex.Message);
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
                        ? Localization.Get("0Truncated", detail[..1200])
                        : detail;
                }
            }
            catch
            {
            }
        }

        return exitCode switch
        {
            3 => Localization.Get("TheAgentOrConfigurationFileCouldNotBeFound"),
            4 => Localization.Get("TheBackgroundServiceCouldNotApplyTheBeszelAgent"),
            55 => Localization.Get("EXITONDNSERRORCannotBeEnabledTogetherWith2"),
            57 => Localization.Get("TheConfigurationWasRejectedByTheSecurityPolicyRemove"),
            53 => Localization.Get("NSSMCouldNotBeFoundReinstallBeszelAgentManagerOrPlace"),
            _ => Localization.Get("TheBackgroundServiceReturnedExitCode0", exitCode),
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
            Content = Localization.Get("KeepHistoricalAgentLogging"),
            IsChecked = true,
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = Localization.Get("ThisWillStopAndRemoveTheBeszelAgentService"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = Localization.Get("InstalledVersion0", installedVersion),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(keepLogsCheckBox);

        var dialog = new ContentDialog
        {
            XamlRoot = NavView.XamlRoot,
            Title = Localization.Get("UninstallBeszelAgent"),
            Content = panel,
            PrimaryButtonText = Localization.Get("Uninstall"),
            CloseButtonText = Localization.Get("Cancel"),
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
            Title = Localization.Get("OptionalMicrosoftDefenderExclusion"),
            Content = new TextBlock
            {
                Text = Localization.Get("AddAnExclusionOnlyForTheBeszelAgentManagerInstallationFolder"),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Localization.Get("AddExclusion"),
            CloseButtonText = Localization.Get("NotNow"),
            DefaultButton = ContentDialogButton.Close,
        };
        var add = await dialog.ShowAsync() == ContentDialogResult.Primary;
        config.DefenderExclusionPrompted = true;
        if (add)
        {
            var exitCode = await App.Broker.SetDefenderExclusionAsync(true);
            if (exitCode != 0)
            {
                throw new InvalidOperationException(Localization.Get("TheBackgroundServiceCouldNotAddTheDefenderExclusion", exitCode));
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
            3 => Localization.Get("TheAgentOrConfigurationFileCouldNotBeFound"),
            20 => Localization.Get("CouldNotFindAUsableBeszelAgentReleaseOn"),
            21 => Localization.Get("TheDownloadedArchiveDidNotContainBeszelAgentExe"),
            22 => Localization.Get("TheAgentCouldNotBeDownloadedOrInstalledCheck"),
            24 => Localization.Get("TheBeszelAgentArchiveChecksumIsMissingOrDoes"),
            23 => Localization.Get("OneOrMoreAgentFoldersCouldNotBeRemoved"),
            53 => Localization.Get("NSSMCouldNotBeFoundReinstallBeszelAgentManagerOrPlace"),
            56 => Localization.Get("TheBackgroundServiceBlockedAVersionDowngradeInstallOlder"),
            _ => Localization.Get("TheBackgroundServiceReturnedErrorCode0", exitCode),
        };
    }

    private static string BuildReleaseNotesPreview(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Localization.Get("NoReleaseNotes");
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
            lines = lines.Take(18).Append("...").Append(Localization.Get("Truncated")).ToList();
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
                ShowGlobalStatus(InfoBarSeverity.Warning, Localization.Get("NoManagerVersionsFound"), Localization.Get("NoUsableInstallerReleasesWereReturnedByGitHub"));
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

            var versionSection = new StackPanel { Spacing = 10 };
            versionSection.Children.Add(new TextBlock { Text = Localization.Get("OpenAnInstallerRelease"), FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            versionSection.Children.Add(new TextBlock
            {
                Text = Localization.Get("VerifiedUpdatesInstallThroughTheBackgroundServiceOtherVersions"),
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
                Title = Localization.Get("ManageManagerVersion"),
                Content = panel,
                PrimaryButtonText = Localization.Get("Continue"),
                CloseButtonText = Localization.Get("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
            };
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            var selected = releases[Math.Max(0, picker.SelectedIndex)];
            if (selected.Tag == releases[0].Tag) await InstallManagerReleaseAsync(selected);
            else
            {
                await ShowManualManagerDownloadAsync(selected);
            }
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Manager version manager failed: {ex}");
            ShowGlobalStatus(InfoBarSeverity.Warning, Localization.Get("CouldNotLoadManagerVersions"), Localization.Get("PleaseTryAgainLaterTechnicalDetailsAreAvailableIn"));
        }
    }

    public async Task ShowManagerUpdateSettingsAsync()
    {
        var config = await _configService.LoadAsync();
        var notify = new CheckBox { Content = Localization.Get("NotifyMeWhenAManagerUpdateIsAvailable"), IsChecked = config.ManagerUpdateNotifyEnabled };
        var badge = new CheckBox { Content = Localization.Get("ShowUpdateAvailabilityInTheTrayStatus"), IsChecked = config.ManagerUpdateTrayBadgeEnabled };
        var prereleases = new CheckBox { Content = Localization.Get("IncludeReleaseCandidatesAndOtherPrereleases"), IsChecked = config.ManagerUpdateIncludePrereleases };
        var skipVersion = new TextBox
        {
            Header = Localization.Get("SkipNotificationsForVersionOptional"),
            Text = config.ManagerUpdateSkipVersion,
            PlaceholderText = Localization.Get("Example401"),
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
        intervalRow.Children.Add(new TextBlock { Text = Localization.Get("CheckEvery"), VerticalAlignment = VerticalAlignment.Center });
        intervalRow.Children.Add(interval);
        intervalRow.Children.Add(new TextBlock { Text = Localization.Get("CheckIntervalHours"), VerticalAlignment = VerticalAlignment.Center });
        var panel = new StackPanel { Spacing = 10, Width = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = Localization.Get("ControlAutomaticGitHubChecksAndUpdateNotificationsUpdatesAre"),
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
            Title = Localization.Get("ManagerUpdateSettings"),
            Content = panel,
            PrimaryButtonText = Localization.Get("SaveSettings"),
            CloseButtonText = Localization.Get("Cancel"),
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
        ShowGlobalStatus(InfoBarSeverity.Success, Localization.Get("ManagerUpdateSettingsSaved"), Localization.Get("TheNewUpdatePreferencesAreActive"));
        _ = CheckManagerUpdateInBackgroundAsync();
    }

    private static string FullReleaseNotes(string? body)
    {
        return string.IsNullOrWhiteSpace(body) ? Localization.Get("NoReleaseNotes") : body.Trim();
    }

    private static bool IsManagerUpdateAvailable(ManagerRelease release, bool includePrereleases)
    {
        if (VersionComparer.IsUpdateAvailable(AppInfo.ReleaseTag, release.Version))
        {
            return true;
        }

        // RC1-RC4 predate the installed channel marker. Explicit prerelease opt-in
        // lets those builds move once to a newer RC in the same version family.
        return includePrereleases
            && release.IsPrerelease
            && string.Equals(AppInfo.ReleaseChannel, "stable", StringComparison.OrdinalIgnoreCase)
            && VersionComparer.HasSameCore(AppInfo.Version, release.Version)
            && !string.Equals(AppInfo.ReleaseTag, release.Tag, StringComparison.OrdinalIgnoreCase);
    }

    private async Task InstallManagerReleaseAsync(ManagerRelease release)
    {
        if (!release.HasUpdateMetadata)
        {
            await ShowManualManagerDownloadAsync(release);
            return;
        }
        if (!await EnsureBackgroundServiceAvailableAsync()) return;
        ShowGlobalStatus(InfoBarSeverity.Informational, Localization.Get("VerifyingSignedUpdate"), Localization.Get("DownloadingAndVerifyingTheLatestOfficialReleaseForThis"));
        ManagerUpdateStage staged;
        try
        {
            staged = await App.Broker.PrepareManagerUpdateAsync();
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Signed manager update verification failed: {ex}");
            ShowGlobalStatus(InfoBarSeverity.Error, Localization.Get("UpdateCouldNotBeVerified"), Localization.Get("InstallationWasNotStartedPleaseTryAgainLaterTechnical"));
            return;
        }
        var confirm = new ContentDialog
        {
            XamlRoot = NavView.XamlRoot,
            Title = Localization.Get("HashesVerified"),
            Content = new TextBlock
            {
                Text = Localization.Get("BeszelAgentManager0IsReadyToInstall", staged.Tag),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Localization.Get("BeginUpdate"),
            CloseButtonText = Localization.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await App.Broker.CommitManagerUpdateAsync(staged.OperationId);
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Signed manager update commit failed: {ex}");
            ShowGlobalStatus(InfoBarSeverity.Error, Localization.Get("UpdateCouldNotBeStarted"), Localization.Get("TheManagerWillRemainOpenCheckTheLogBefore"));
            return;
        }
        App.Logger.Info($"Signed manager update accepted for {staged.Tag}; completion pending installer verification.");
        ExitFromTray();
    }

    private async Task ShowManualManagerDownloadAsync(ManagerRelease release)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = NavView.XamlRoot,
            Title = Localization.Get("DownloadBeszelAgentManager0", release.Version),
            Content = new TextBlock
            {
                Text = Localization.Get("ThisVersionNeedsToBeInstalledManuallyOpenThe", AppInfo.InstallerAssetName),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Localization.Get("OpenReleasePage"),
            CloseButtonText = Localization.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            OpenUrl($"https://github.com/{AppInfo.ManagerRepo}/releases/tag/{Uri.EscapeDataString(release.Tag)}");
        }
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
            CloseButtonText = Localization.Get("OK"),
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
            Text = Localization.Get("Installed0Available12", AppInfo.ReleaseTag, release.Version, release.Tag),
            TextWrapping = TextWrapping.Wrap,
        });

        if (!string.IsNullOrWhiteSpace(release.Body))
        {
            var notes = release.Body.Length > 1500
                ? Localization.Get("0Truncated", release.Body[..1500])
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
