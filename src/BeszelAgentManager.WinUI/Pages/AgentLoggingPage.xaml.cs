using BeszelAgentManager.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace BeszelAgentManager.WinUI.Pages;

public sealed partial class AgentLoggingPage : Page
{
    private readonly LogReaderService _logReader = new();
    private readonly DispatcherQueueTimer _refreshTimer;
    private bool _loadingLogFiles;
    private bool _refreshing;
    private long _lastFileLength = -1;
    private DateTime _lastFileWriteUtc = DateTime.MinValue;

    public AgentLoggingPage()
    {
        InitializeComponent();
        Loaded += AgentLoggingPage_Loaded;
        Unloaded += AgentLoggingPage_Unloaded;
        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(2);
        _refreshTimer.Tick += async (_, _) => await RefreshLiveLogAsync();
    }

    private async void AgentLoggingPage_Loaded(object sender, RoutedEventArgs e)
    {
        LoadLogFiles();
        await RefreshAsync();
        App.Logger.Debug("Agent Logging page opened");
        _refreshTimer.Start();
    }

    private void AgentLoggingPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer.Stop();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        App.Logger.Debug("Agent log refresh requested");
        LoadLogFiles();
        await RefreshAsync();
    }

    private async void RotateNowButton_Click(object sender, RoutedEventArgs e)
    {
        RotateNowButton.IsEnabled = false;
        App.Logger.Info("Manual agent log rotation requested");
        App.MainWindow.ShowActionStatus(
            InfoBarSeverity.Informational,
            "Rotating agent log",
            "Approve the administrator prompt if Windows asks.");
        try
        {
            var exitCode = await App.ElevatedHelper.RotateAgentLogsAsync();
            if (exitCode != 0)
            {
                throw new InvalidOperationException(exitCode == 24
                    ? "The agent log could not be rotated. Close any program holding the log file and try again."
                    : $"The elevated helper returned error code {exitCode}.");
            }

            App.Logger.Info("Manual agent log rotation completed");
            App.MainWindow.ShowActionStatus(
                InfoBarSeverity.Success,
                "Agent log rotated",
                "The current agent log was archived and a fresh log was started.");
            LoadLogFiles();
            await RefreshAsync();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            App.Logger.Info("Manual agent log rotation cancelled at UAC prompt");
            App.MainWindow.ShowActionStatus(
                InfoBarSeverity.Warning,
                "Agent log rotation cancelled",
                "The administrator prompt was cancelled.");
        }
        catch (Exception ex)
        {
            App.Logger.Error($"Manual agent log rotation failed: {ex}");
            App.MainWindow.ShowActionStatus(
                InfoBarSeverity.Error,
                "Agent log rotation failed",
                ex.Message);
        }
        finally
        {
            RotateNowButton.IsEnabled = true;
        }
    }

    private async void LogSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_loadingLogFiles)
        {
            await RefreshAsync();
        }
    }

    private async void TypeFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            await RefreshAsync();
        }
    }

    private void LoadLogFiles()
    {
        _loadingLogFiles = true;
        var selectedPath = (LogSelector.SelectedItem as LogFileItem)?.Path;
        var files = _logReader.ListAgentLogFiles();
        LogSelector.Items.Clear();

        foreach (var file in files)
        {
            LogSelector.Items.Add(file);
        }

        if (LogSelector.Items.Count == 0)
        {
            SetLogText("(No agent logs found yet.)");
            _loadingLogFiles = false;
            return;
        }

        var selected = files.FirstOrDefault(file => string.Equals(file.Path, selectedPath, StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(file => file.Label.Equals("Current (beszel-agent.log)", StringComparison.OrdinalIgnoreCase))
            ?? files[0];
        LogSelector.SelectedItem = selected;
        _loadingLogFiles = false;
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var selected = LogSelector.SelectedItem as LogFileItem;
            var path = selected?.Path ?? ManagerPaths.AgentLogPath;
            LogPathText.Text = $"Current capture file: {path}";
            var filter = SelectedTypeFilter();
            var text = string.Equals(path, ManagerPaths.AgentLogPath, StringComparison.OrdinalIgnoreCase)
                ? ReadCurrentAgentLogTail(path, filter)
                : await _logReader.ReadAllAsync(path, filter);
            if (!string.IsNullOrEmpty(text) && text.Length > 10_000)
            {
                text = text[^10_000..];
            }
            SetLogText(string.IsNullOrWhiteSpace(text)
                ? $"No log content loaded from: {path}"
                : text);
            QueueScrollToBottom();
            UpdateKnownFileState(path);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshLiveLogAsync()
    {
        if (LogSelector.SelectedItem is LogFileItem selected
            && string.Equals(selected.Path, ManagerPaths.AgentLogPath, StringComparison.OrdinalIgnoreCase)
            && HasSelectedFileChanged(selected.Path))
        {
            await RefreshAsync();
        }
    }

    private static string ReadCurrentAgentLogTail(string path, string typeFilter)
    {
        if (!File.Exists(path))
        {
            return $"Log file not found: {path}";
        }

        try
        {
            var content = ReadSharedTextFile(path);
            using var reader = new StringReader(content);
            var lines = new Queue<string>();

            while (reader.ReadLine() is { } line)
            {
                if (LineMatchesFilter(line, typeFilter))
                {
                    lines.Enqueue(line);
                    while (lines.Count > 500)
                    {
                        lines.Dequeue();
                    }
                }
            }

            return lines.Count == 0
                ? $"No {typeFilter.ToLowerInvariant()} entries in selected log."
                : string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return $"Could not read log file: {path}{Environment.NewLine}{ex.Message}";
        }
    }

    private static string ReadSharedTextFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0)
        {
            return string.Empty;
        }

        stream.Seek(0, SeekOrigin.Begin);
        var buffer = new byte[stream.Length];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0)
            {
                break;
            }

            offset += read;
        }

        return System.Text.Encoding.UTF8.GetString(buffer, 0, offset);
    }

    private static bool LineMatchesFilter(string line, string typeFilter)
    {
        if (string.IsNullOrWhiteSpace(typeFilter) || string.Equals(typeFilter, "All", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return typeFilter.Trim().ToLowerInvariant() switch
        {
            "info" => line.Contains("info", StringComparison.OrdinalIgnoreCase),
            "warnings" => line.Contains("warn", StringComparison.OrdinalIgnoreCase),
            "errors" => line.Contains("error", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    private string SelectedTypeFilter()
    {
        return TypeFilterComboBox.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString() ?? "All"
            : "All";
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
                Text = index == lines.Length - 1 ? line : $"{line}{Environment.NewLine}",
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
}
