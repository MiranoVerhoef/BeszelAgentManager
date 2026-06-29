using BeszelAgentManager.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using System.Text.RegularExpressions;

namespace BeszelAgentManager.WinUI.Pages;

public sealed partial class LogsPage : Page
{
    private readonly LogReaderService _logReaderService = new();
    private readonly ConfigService _configService = new();
    private readonly SupportBundleService _supportBundleService = new();
    private readonly DispatcherQueueTimer _refreshTimer;
    private bool _refreshing;
    private bool _loadingConfig;
    private bool _loadingLogFiles;
    private long _lastFileLength = -1;
    private DateTime _lastFileWriteUtc = DateTime.MinValue;

    public LogsPage()
    {
        InitializeComponent();
        Loaded += LogsPage_Loaded;
        Unloaded += LogsPage_Unloaded;
        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(2);
        _refreshTimer.Tick += async (_, _) => await RefreshLiveLogAsync();
    }

    private async void LogsPage_Loaded(object sender, RoutedEventArgs e)
    {
        var config = await _configService.LoadAsync();
        _loadingConfig = true;
        DebugLoggingCheckBox.IsChecked = config.DebugLogging;
        _loadingConfig = false;
        App.Logger.Debug("Logging page opened");
        ManagerLogFolderText.Text = $"Manager log folder: {ManagerPaths.ManagerLogDir}";
        ManagerLogFileText.Text = $"Current file: {ManagerPaths.ManagerLogPath}";
        LoadLogFiles();
        await RefreshLogAsync();
        _refreshTimer.Start();
    }

    private void LogsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer.Stop();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        App.Logger.Debug("Manager log refresh requested");
        LoadLogFiles();
        await RefreshLogAsync();
    }

    private async void LogSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_loadingLogFiles && LogSelector.SelectedItem is LogFileItem selected)
        {
            App.Logger.Debug($"Manager log file selected: {selected.Path}");
            await RefreshLogAsync();
        }
    }

    private async void TypeFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            App.Logger.Debug($"Manager log filter selected: {SelectedTypeFilter()}");
            await RefreshLogAsync();
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        App.Logger.Info($"Opening manager log folder: {ManagerPaths.DataDir}");
        OpenFolder(ManagerPaths.ManagerLogDir);
    }

    private async void DebugLoggingCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingConfig)
        {
            return;
        }

        var enabled = DebugLoggingCheckBox.IsChecked == true;
        await _configService.SetDebugLoggingAsync(enabled);
        App.Logger.SetDebugEnabled(enabled);
        App.Logger.Info($"Manager debug logging {(enabled ? "enabled" : "disabled")}");
        App.MainWindow.ShowActionStatus(
            InfoBarSeverity.Informational,
            enabled ? "Debug logging enabled" : "Debug logging disabled",
            enabled ? "Manager debug entries will now be written to the log." : "Manager debug entries will be hidden from the log.");
        await RefreshLogAsync();
    }

    private async void ExportSupportBundleButton_Click(object sender, RoutedEventArgs e)
    {
        ExportSupportBundleButton.IsEnabled = false;
        App.Logger.Info("Support bundle export requested");
        try
        {
            var path = await _supportBundleService.CreateAsync();
            App.Logger.Info($"Support bundle created: {path}");
            App.MainWindow.ShowActionStatus(
                InfoBarSeverity.Success,
                "Support bundle exported",
                path);
            OpenFolder(Path.GetDirectoryName(path) ?? ManagerPaths.DataDir);
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Support bundle export failed: {ex}");
            App.MainWindow.ShowActionStatus(
                InfoBarSeverity.Error,
                "Support bundle export failed",
                ex.Message);
        }
        finally
        {
            ExportSupportBundleButton.IsEnabled = true;
            LoadLogFiles();
            await RefreshLogAsync();
        }
    }

    private async void RotateNowButton_Click(object sender, RoutedEventArgs e)
    {
        App.Logger.Info("Manual manager log rotation requested");
        try
        {
            RotateManagerLog();
            App.Logger.Info("Manual manager log rotation completed");
            App.MainWindow.ShowActionStatus(
                InfoBarSeverity.Success,
                "Manager log rotated",
                "The current manager log was archived and a fresh log was started.");
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Manual manager log rotation failed: {ex}");
            App.MainWindow.ShowActionStatus(
                InfoBarSeverity.Error,
                "Manager log rotation failed",
                ex.Message);
        }

        LoadLogFiles();
        await RefreshLogAsync();
    }

    private void LoadLogFiles()
    {
        _loadingLogFiles = true;
        var selectedPath = (LogSelector.SelectedItem as LogFileItem)?.Path;
        var files = _logReaderService.ListManagerLogFiles();
        LogSelector.Items.Clear();

        foreach (var file in files)
        {
            LogSelector.Items.Add(file);
        }

        if (LogSelector.Items.Count == 0)
        {
            SetLogText("(No manager logs found yet.)");
            _loadingLogFiles = false;
            return;
        }

        var selected = files.FirstOrDefault(file => string.Equals(file.Path, selectedPath, StringComparison.OrdinalIgnoreCase)) ?? files[0];
        LogSelector.SelectedItem = selected;
        _loadingLogFiles = false;
    }

    private async Task RefreshLogAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        RefreshButton.IsEnabled = false;
        try
        {
            var selected = LogSelector.SelectedItem as LogFileItem;
            var path = selected?.Path ?? ManagerPaths.ManagerLogPath;
            ManagerLogFileText.Text = $"Current file: {path}";
            var text = await _logReaderService.ReadTailFilteredAsync(path, SelectedTypeFilter());
            SetLogText(text);
            QueueScrollToBottom();
            UpdateKnownFileState(path);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            _refreshing = false;
        }
    }

    private async Task RefreshLiveLogAsync()
    {
        if (LogSelector.SelectedItem is LogFileItem selected
            && string.Equals(selected.Path, ManagerPaths.ManagerLogPath, StringComparison.OrdinalIgnoreCase)
            && HasSelectedFileChanged(selected.Path))
        {
            await RefreshLogAsync();
        }
    }

    private string SelectedTypeFilter()
    {
        return TypeFilterComboBox.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString() ?? "All"
            : "All";
    }

    private static void OpenFolder(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Read-only migration slice: ignore shell launch failures for now.
        }
    }

    private bool HasSelectedFileChanged(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists
                && (file.Length != _lastFileLength || file.LastWriteTimeUtc != _lastFileWriteUtc);
        }
        catch
        {
            return false;
        }
    }

    private void UpdateKnownFileState(string path)
    {
        try
        {
            var file = new FileInfo(path);
            _lastFileLength = file.Exists ? file.Length : -1;
            _lastFileWriteUtc = file.Exists ? file.LastWriteTimeUtc : DateTime.MinValue;
        }
        catch
        {
            _lastFileLength = -1;
            _lastFileWriteUtc = DateTime.MinValue;
        }
    }

    private void QueueScrollToBottom()
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            LogScrollViewer.UpdateLayout();
            LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null, true);
        });
    }

    private void SetLogText(string text)
    {
        LogTextBlock.Blocks.Clear();
        var paragraph = new Paragraph();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var run = new Run
            {
                Text = index == lines.Length - 1 ? NormalizeDisplayLine(line) : $"{NormalizeDisplayLine(line)}{Environment.NewLine}",
            };
            var brush = BrushForLogLine(line);
            if (brush is not null)
            {
                run.Foreground = brush;
            }

            paragraph.Inlines.Add(run);
        }

        LogTextBlock.Blocks.Add(paragraph);
    }

    private static string NormalizeDisplayLine(string line)
    {
        var match = LegacyManagerLineRegex().Match(line);
        if (!match.Success)
        {
            return line;
        }

        var message = match.Groups["message"].Value;
        var level = message.StartsWith("ERROR ", StringComparison.OrdinalIgnoreCase)
            ? "ERROR"
            : message.StartsWith("DEBUG ", StringComparison.OrdinalIgnoreCase)
                ? "DEBUG"
                : message.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("error", StringComparison.OrdinalIgnoreCase)
                        ? "ERROR"
                        : message.Contains("warn", StringComparison.OrdinalIgnoreCase)
                            ? "WARN"
                            : "INFO";
        if (message.StartsWith($"{level} ", StringComparison.OrdinalIgnoreCase))
        {
            message = message[(level.Length + 1)..];
        }

        var timestamp = match.Groups["timestamp"].Value.Replace('-', '/');
        return $"{timestamp} {level} {message}";
    }

    private static SolidColorBrush? BrushForLogLine(string line)
    {
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("fatal", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Colors.Red);
        }

        if (line.Contains("warn", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Colors.DarkOrange);
        }

        return null;
    }

    [GeneratedRegex(@"^\[(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]\s*(?<message>.*)$")]
    private static partial Regex LegacyManagerLineRegex();

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static void RotateManagerLog()
    {
        if (!File.Exists(ManagerPaths.ManagerLogPath))
        {
            return;
        }

        var content = File.ReadAllText(ManagerPaths.ManagerLogPath);
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        var archiveDir = ManagerPaths.ManagerLogArchiveDir;
        Directory.CreateDirectory(archiveDir);
        var archivePath = Path.Combine(archiveDir, $"manager-{DateTime.Today:yyyy-MM-dd}.txt");
        File.AppendAllText(archivePath, content);
        File.WriteAllText(ManagerPaths.ManagerLogPath, string.Empty);
    }
}
