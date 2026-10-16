using System.Text.Json;
using BeszelAgentManager.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace BeszelAgentManager.WinUI.Pages;

public sealed partial class EnvironmentPage : Page
{
    private static readonly IReadOnlyList<EnvDefinition> Definitions =
    [
        new("ALL_PROXY", "all_proxy", Localization.Get("SOCKS5OrSOCKS5HProxyForTheOutboundHubWebSocket")),
        new("CA_CERT_FILE", "ca_cert_file", Localization.Get("AbsoluteLocalPathToAPEMCertificateFileUsed")),
        new("DOCKER_HOST", "docker_host", Localization.Get("PointsTheAgentAtASpecificDockerDaemonEndpoint")),
        new("DOCKER_IMAGE_CHECK", "docker_image_check", Localization.Get("SetToFalseToDisableContainerImageUpdateChecks")),
        new("DOCKER_TIMEOUT", "docker_timeout", Localization.Get("DockerAPITimeoutInGoDurationFormatForExample")),
        new("EXCLUDE_CONTAINERS", "exclude_containers", Localization.Get("CommaSeparatedContainerNamesOrPatternsToHideFrom")),
        new("EXCLUDE_SMART", "exclude_smart", Localization.Get("DiskNamesOrPatternsToSkipDuringSM")),
        new("EXTRA_FILESYSTEMS", "extra_filesystems", Localization.Get("AdditionalFilesystemPathsTheAgentShouldCollectUsageFor")),
        new("FILESYSTEM", "filesystem", Localization.Get("OverridesTheRootFilesystemPathUsedForDiskStatistics")),
        new("EXIT_ON_DNS_ERROR", "exit_on_dns_error", Localization.Get("ExitsTheAgentWhenHubDNSLookupFailsDo")),
        new("INTEL_GPU_DEVICE", "intel_gpu_device", Localization.Get("DevicePathUsedByIntelGpuTopForIntel")),
        new("NVML", "nvml", Localization.Get("EnablesNVIDIANVMLGPUMonitoringWhenSetToTrue")),
        new("LHM", "lhm", Localization.Get("EnablesLibreHardwareMonitorSensorCollectionOnWindows")),
        new("LOG_LEVEL", "log_level", Localization.Get("ControlsAgentVerbosityForExampleInfoWarnOrDebug")),
        new("MEM_CALC", "mem_calc", Localization.Get("ChangesHowMemoryUsageIsCalculated")),
        new("NETWORK", "network", Localization.Get("ChangesNetworkCollectionMode")),
        new("NICS", "nics", Localization.Get("LimitsNetworkMonitoringToSpecificInterfaceNames")),
        new("PACKAGE_UPDATES_INTERVAL", "package_updates_interval", Localization.Get("LinuxOnlyPackageUpdateCheckIntervalForExample1h")),
        new("SENSORS", "sensors", Localization.Get("SelectsWhichHardwareSensorsTheAgentReads")),
        new("SENSORS_TIMEOUT", "sensors_timeout", Localization.Get("MaximumTimeAllowedForSensorCollectionForExample5s")),
        new("PRIMARY_SENSOR", "primary_sensor", Localization.Get("SelectsThePrimaryTemperatureSensorShownInBeszel")),
        new("SYS_SENSORS", "sys_sensors", Localization.Get("PathOverrideForSystemSensorData")),
        new("SERVICE_PATTERNS", "service_patterns", Localization.Get("ServiceNamesOrPatternsToMonitor")),
        new("SMART_DEVICES", "smart_devices", Localization.Get("SpecificSMARTDevicesOptionallyAs")),
        new("SMART_DEVICES_SEPARATOR", "smart_devices_separator", Localization.Get("SeparatorUsedBetweenSMARTDEVICESEntriesDefaultsToA")),
        new("SMART_INTERVAL", "smart_interval", Localization.Get("HowOftenSMARTDataIs")),
        new("SYSTEM_NAME", "system_name", Localization.Get("OverridesTheSystemNameReportedByTheAgent")),
        new("ZFS_INTERVAL", "zfs_interval", Localization.Get("ZFSPoolDetailRefreshIntervalScrubVdevsDatasetsFor")),
        new("SKIP_GPU", "skip_gpu", Localization.Get("SetToTrueToSkipGPUMetricsIncludingGPU")),
        new("GPU_COLLECTOR", "gpu_collector", Localization.Get("OrderedCollectorsSuchAsNvtopNvmlIntelSysfsIntel")),
        new("DISABLE_SSH", "disable_ssh", Localization.Get("DisablesTheAgentSSHServerWhenSetToTrue")),
        new("DISK_USAGE_CACHE", "disk_usage_cache", Localization.Get("CachesDiskUsageResultsForADurationForExample")),
        new("SKIP_SYSTEMD", "skip_systemd", Localization.Get("LinuxOnlySetToTrueToSkipSystemdIntegration")),
        new("SKIP_SYSTEMD_LOGS", "skip_systemd_logs", Localization.Get("LinuxOnlySetToTrueToDisableSystemdJournal")),
        new("SKIP_WIFI", "skip_wifi", Localization.Get("SetToTrueToSkipWiFiSignalCollection")),
    ];

    private readonly ConfigService _configService = new();
    private AgentConfig _config = new();
    private EnvDefinition _selectedDefinition = Definitions[0];

    public EnvironmentPage()
    {
        InitializeComponent();
        Loaded += EnvironmentPage_Loaded;
    }

    private async void EnvironmentPage_Loaded(object sender, RoutedEventArgs e)
    {
        SetSelectedDefinition(Definitions[0]);
        _config = await _configService.LoadAsync();
        RenderRows();
    }

    private void SelectEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = BuildEnvironmentFlyout();
        flyout.ShowAt(SelectEnvironmentButton);
    }

    private async void AddEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        var name = _selectedDefinition.Name;
        if (_config.EnvActiveNames.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
        {
            App.MainWindow.ShowActionStatus(
                InfoBarSeverity.Informational,
                Localization.Get("EnvironmentAlreadyActive"),
                Localization.Get("0IsAlreadyListed", name));
            return;
        }

        _config.EnvActiveNames.Add(name);
        await SaveAndReportAsync($"Environment variable added: {name}");
        RenderRows();
    }

    private void RenderRows()
    {
        EnvironmentListView.Items.Clear();

        var searchText = EnvironmentSearchBox?.Text?.Trim() ?? string.Empty;
        var rows = _configService.GetActiveEnvironmentRows(_config)
            .Where(row => MatchesSearch(row.Name, searchText))
            .OrderBy(static row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (rows.Count == 0)
        {
            EnvironmentListView.Items.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(searchText)
                    ? Localization.Get("NoEnvironmentVariablesAreActive")
                    : Localization.Get("NoActiveEnvironmentVariablesMatchYourSearch"),
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            return;
        }

        foreach (var row in rows)
        {
            EnvironmentListView.Items.Add(CreateEnvironmentRow(row.Name, row.ConfigKey, row.Value));
        }
    }

    private void EnvironmentSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RenderRows();
    }

    private Grid CreateEnvironmentRow(string name, string configKey, string value)
    {
        var grid = new Grid
        {
            ColumnSpacing = 8,
            Height = 64,
            Margin = new Thickness(0, 0, 0, 3),
            Padding = new Thickness(6, 4, 6, 4),
            Tag = new EnvRowState(name, configKey),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

        var definition = FindDefinition(name);
        var labelPanel = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        labelPanel.Children.Add(new TextBlock
        {
            Text = $"{name}:",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        labelPanel.Children.Add(new TextBlock
        {
            Text = definition?.Description ?? Localization.Get("CustomEnvironmentVariable"),
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            MaxLines = 2,
            TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis,
        });
        var textBox = new TextBox
        {
            Text = value,
            IsReadOnly = true,
            MinHeight = 28,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var editButton = new Button
        {
            Content = Localization.Get("Edit"),
            MinHeight = 28,
            Padding = new Thickness(10, 3, 10, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var removeButton = new Button
        {
            Content = Localization.Get("Remove"),
            MinHeight = 28,
            Padding = new Thickness(10, 3, 10, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };

        editButton.Click += async (_, _) => await EditEnvironmentRowAsync(grid, textBox, editButton);
        removeButton.Click += async (_, _) => await RemoveEnvironmentRowAsync(grid);

        Grid.SetColumn(textBox, 1);
        Grid.SetColumn(editButton, 2);
        Grid.SetColumn(removeButton, 3);
        grid.Children.Add(labelPanel);
        grid.Children.Add(textBox);
        grid.Children.Add(editButton);
        grid.Children.Add(removeButton);
        return grid;
    }

    private async Task EditEnvironmentRowAsync(Grid row, TextBox textBox, Button editButton)
    {
        if (row.Tag is not EnvRowState state)
        {
            return;
        }

        if (textBox.IsReadOnly)
        {
            textBox.IsReadOnly = false;
            editButton.Content = Localization.Get("Save");
            textBox.Focus(FocusState.Programmatic);
            textBox.SelectionStart = textBox.Text.Length;
            return;
        }

        var before = GetEnvironmentValue(state);
        SetEnvironmentValue(state, textBox.Text.Trim());
        textBox.IsReadOnly = true;
        editButton.Content = Localization.Get("Edit");
        await SaveAndReportAsync(IsSensitiveEnvironmentName(state.Name)
            ? $"Environment variable {state.Name} changed (value redacted)"
            : $"Environment variable {state.Name}: {Format(before)} -> {Format(textBox.Text.Trim())}");
    }

    private async Task RemoveEnvironmentRowAsync(Grid row)
    {
        if (row.Tag is not EnvRowState state)
        {
            return;
        }

        _config.EnvActiveNames.RemoveAll(name => string.Equals(name, state.Name, StringComparison.OrdinalIgnoreCase));
        _config.EnvCustom.RemoveAll(item => string.Equals(item.Name, state.Name, StringComparison.OrdinalIgnoreCase));
        await SaveAndReportAsync($"Environment variable removed: {state.Name}");
        RenderRows();
    }

    private string GetEnvironmentValue(EnvRowState state)
    {
        var custom = _config.EnvCustom.FirstOrDefault(item => string.Equals(item.Name, state.Name, StringComparison.OrdinalIgnoreCase));
        return custom?.Value ?? _config.GetEnvironmentValue(state.ConfigKey);
    }

    private void SetEnvironmentValue(EnvRowState state, string value)
    {
        var custom = _config.EnvCustom.FirstOrDefault(item => string.Equals(item.Name, state.Name, StringComparison.OrdinalIgnoreCase));
        if (custom is not null)
        {
            custom.Value = value;
            return;
        }

        _config.ExtraFields[state.ConfigKey] = JsonSerializer.SerializeToElement(value, AppJsonContext.Default.String);
    }

    private async Task SaveAndReportAsync(string logMessage)
    {
        var persisted = await _configService.LoadAsync();
        _config.LastAppliedFingerprint = persisted.LastAppliedFingerprint;
        _config.LastAppliedAt = persisted.LastAppliedAt;
        _config.LastAppliedManagerTasksFingerprint = persisted.LastAppliedManagerTasksFingerprint;
        await _configService.SaveAsync(_config);
        App.Logger.Info(logMessage);
        App.MainWindow.ShowActionStatus(
            InfoBarSeverity.Informational,
            Localization.Get("EnvironmentSaved"),
            Localization.Get("ApplySettingsWhenYouAreReadyToRestartThe2"));
    }

    private static string Format(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(empty)" : value;
    }

    private static bool IsSensitiveEnvironmentName(string name) =>
        string.Equals(name, "ALL_PROXY", StringComparison.OrdinalIgnoreCase);

    private Flyout BuildEnvironmentFlyout()
    {
        var optionsPanel = new StackPanel
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var searchBox = new TextBox
        {
            MinHeight = 32,
            PlaceholderText = Localization.Get("SearchNameOrDescription"),
        };

        void RenderOptions()
        {
            optionsPanel.Children.Clear();
            var searchText = searchBox.Text.Trim();
            var matchingDefinitions = Definitions.Where(definition => MatchesSearch(definition, searchText)).ToList();
            foreach (var definition in matchingDefinitions)
            {
                var button = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(10, 7, 10, 7),
                    Content = CreateEnvironmentOptionContent(definition),
                    Tag = definition,
                };
                button.Click += (_, _) =>
                {
                    SetSelectedDefinition(definition);
                    if (SelectEnvironmentButton.Flyout is FlyoutBase attachedFlyout)
                    {
                        attachedFlyout.Hide();
                    }
                };
                optionsPanel.Children.Add(button);
            }

            if (matchingDefinitions.Count == 0)
            {
                optionsPanel.Children.Add(new TextBlock
                {
                    Padding = new Thickness(10, 8, 10, 8),
                    Text = Localization.Get("NoEnvironmentVariablesMatchYourSearch"),
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
            }
        }

        searchBox.TextChanged += (_, _) => RenderOptions();
        RenderOptions();

        var scroller = new ScrollViewer
        {
            Content = optionsPanel,
            MaxHeight = 360,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var content = new StackPanel
        {
            Spacing = 8,
            Width = 520,
        };
        content.Children.Add(searchBox);
        content.Children.Add(scroller);

        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(ScrollViewer.HorizontalScrollModeProperty, ScrollMode.Disabled));
        presenterStyle.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));

        var flyout = new Flyout
        {
            Content = content,
            Placement = FlyoutPlacementMode.Bottom,
            FlyoutPresenterStyle = presenterStyle,
        };
        SelectEnvironmentButton.Flyout = flyout;
        return flyout;
    }

    private static StackPanel CreateEnvironmentOptionContent(EnvDefinition definition)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = definition.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        panel.Children.Add(new TextBlock
        {
            Text = definition.Description,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            FontSize = 12,
            MaxLines = 1,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        return panel;
    }

    private void SetSelectedDefinition(EnvDefinition definition)
    {
        _selectedDefinition = definition;
        if (SelectedEnvironmentNameText is not null)
        {
            SelectedEnvironmentNameText.Text = definition.Name;
        }
    }

    private static EnvDefinition? FindDefinition(string name)
    {
        return Definitions.FirstOrDefault(definition => string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesSearch(string name, string searchText)
    {
        var definition = FindDefinition(name);
        return string.IsNullOrEmpty(searchText) ||
            name.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
            (definition?.Description.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool MatchesSearch(EnvDefinition definition, string searchText) =>
        string.IsNullOrEmpty(searchText) ||
        definition.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
        definition.Description.Contains(searchText, StringComparison.OrdinalIgnoreCase);

    private sealed record EnvDefinition(string Name, string ConfigKey, string Description);

    private sealed record EnvRowState(string Name, string ConfigKey);
}
