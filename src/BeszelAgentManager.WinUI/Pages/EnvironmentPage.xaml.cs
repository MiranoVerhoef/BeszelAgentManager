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
        new("DATA_DIR", "data_dir", "Changes where the Beszel Agent stores its own runtime data."),
        new("DOCKER_HOST", "docker_host", "Points the agent at a specific Docker daemon endpoint."),
        new("EXCLUDE_CONTAINERS", "exclude_containers", "Comma-separated container names or patterns to hide from monitoring."),
        new("EXCLUDE_SMART", "exclude_smart", "Disk names or patterns to skip during S.M.A.R.T. collection."),
        new("EXTRA_FILESYSTEMS", "extra_filesystems", "Additional filesystem paths the agent should collect usage for."),
        new("FILESYSTEM", "filesystem", "Overrides the root filesystem path used for disk statistics."),
        new("INTEL_GPU_DEVICE", "intel_gpu_device", "Device path used by intel_gpu_top for Intel GPU metrics."),
        new("NVML", "nvml", "Enables NVIDIA NVML GPU monitoring when set to true."),
        new("KEY_FILE", "key_file", "Reads the agent key from a file instead of the Key field."),
        new("TOKEN_FILE", "token_file", "Reads the token from a file instead of the Token field."),
        new("LHM", "lhm", "Enables LibreHardwareMonitor sensor collection on Windows."),
        new("LOG_LEVEL", "log_level", "Controls agent verbosity, for example info, warn, or debug."),
        new("MEM_CALC", "mem_calc", "Changes how memory usage is calculated."),
        new("NETWORK", "network", "Changes network collection mode."),
        new("NICS", "nics", "Limits network monitoring to specific interface names."),
        new("SENSORS", "sensors", "Selects which hardware sensors the agent reads."),
        new("SENSORS_TIMEOUT", "sensors_timeout", "Maximum time allowed for sensor collection, for example 5s."),
        new("PRIMARY_SENSOR", "primary_sensor", "Selects the primary temperature sensor shown in Beszel."),
        new("SYS_SENSORS", "sys_sensors", "Path override for system sensor data."),
        new("SERVICE_PATTERNS", "service_patterns", "Service names or patterns to monitor."),
        new("SMART_DEVICES", "smart_devices", "Specific S.M.A.R.T. devices to monitor."),
        new("SMART_INTERVAL", "smart_interval", "How often S.M.A.R.T. data is refreshed, for example 1h."),
        new("SYSTEM_NAME", "system_name", "Overrides the system name reported by the agent."),
        new("SKIP_GPU", "skip_gpu", "Skips GPU collection when set."),
        new("GPU_COLLECTOR", "gpu_collector", "Selects GPU collectors, for example nvml or amd_sysfs."),
        new("DISABLE_SSH", "disable_ssh", "Disables the agent SSH server when set to true."),
        new("DISK_USAGE_CACHE", "disk_usage_cache", "Caches disk usage results for a duration, for example 10m."),
        new("SKIP_SYSTEMD", "skip_systemd", "Skips systemd integration when set to 1."),
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
                "Environment already active",
                $"{name} is already listed.");
            return;
        }

        _config.EnvActiveNames.Add(name);
        await SaveAndReportAsync($"Environment variable added: {name}");
        RenderRows();
    }

    private void RenderRows()
    {
        EnvironmentListView.Items.Clear();

        var rows = _configService.GetActiveEnvironmentRows(_config)
            .OrderBy(static row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (rows.Count == 0)
        {
            EnvironmentListView.Items.Add(new TextBlock
            {
                Text = "No environment variables are active.",
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            return;
        }

        foreach (var row in rows)
        {
            EnvironmentListView.Items.Add(CreateEnvironmentRow(row.Name, row.ConfigKey, row.Value));
        }
    }

    private Grid CreateEnvironmentRow(string name, string configKey, string value)
    {
        var grid = new Grid
        {
            ColumnSpacing = 12,
            Height = 76,
            Margin = new Thickness(0, 0, 0, 4),
            Padding = new Thickness(8, 6, 8, 6),
            Tag = new EnvRowState(name, configKey),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

        var definition = FindDefinition(name);
        var labelPanel = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        labelPanel.Children.Add(new TextBlock
        {
            Text = $"{name}:",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        labelPanel.Children.Add(new TextBlock
        {
            Text = definition?.Description ?? "Custom environment variable.",
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
            VerticalAlignment = VerticalAlignment.Center,
        };
        var editButton = new Button { Content = "Edit", VerticalAlignment = VerticalAlignment.Center };
        var removeButton = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Center };

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
            editButton.Content = "Save";
            textBox.Focus(FocusState.Programmatic);
            textBox.SelectionStart = textBox.Text.Length;
            return;
        }

        var before = GetEnvironmentValue(state);
        SetEnvironmentValue(state, textBox.Text.Trim());
        textBox.IsReadOnly = true;
        editButton.Content = "Edit";
        await SaveAndReportAsync($"Environment variable {state.Name}: {Format(before)} -> {Format(textBox.Text.Trim())}");
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
            "Environment saved",
            "Apply settings when you are ready to restart the service with these environment changes.");
    }

    private static string Format(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(empty)" : value;
    }

    private Flyout BuildEnvironmentFlyout()
    {
        var list = new StackPanel { Spacing = 2, Width = 520 };
        foreach (var definition in Definitions)
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
            list.Children.Add(button);
        }

        var scroller = new ScrollViewer
        {
            Content = list,
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var flyout = new Flyout
        {
            Content = scroller,
            Placement = FlyoutPlacementMode.Bottom,
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

    private sealed record EnvDefinition(string Name, string ConfigKey, string Description);

    private sealed record EnvRowState(string Name, string ConfigKey);
}
