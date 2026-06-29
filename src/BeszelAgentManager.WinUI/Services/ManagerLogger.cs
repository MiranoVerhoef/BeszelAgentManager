namespace BeszelAgentManager.WinUI.Services;

internal sealed class ManagerLogger
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public bool DebugEnabled { get; private set; }

    public void SetDebugEnabled(bool enabled)
    {
        DebugEnabled = enabled;
        Info($"Debug logging set to {enabled}");
    }

    public void Info(string message) => _ = WriteAsync("INFO", message);

    public void Debug(string message)
    {
        if (DebugEnabled)
        {
            _ = WriteAsync("DEBUG", message);
        }
    }

    public void Warning(string message) => _ = WriteAsync("WARN", message);

    public void Error(string message) => _ = WriteAsync("ERROR", message);

    private async Task WriteAsync(string level, string message)
    {
        await _writeLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(ManagerPaths.ManagerLogDir);
            RotateIfNeeded();
            var line = $"{DateTime.Now:yyyy/MM/dd HH:mm:ss} {level} {message}{Environment.NewLine}";
            await File.AppendAllTextAsync(ManagerPaths.ManagerLogPath, line);
        }
        catch
        {
            // Logging must never block or crash the UI.
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static void RotateIfNeeded()
    {
        var markerPath = Path.Combine(ManagerPaths.ManagerLogDir, "manager_log_last_date.txt");
        var archiveDir = ManagerPaths.ManagerLogArchiveDir;
        var today = DateTime.Today;
        var lastDate = today;

        if (File.Exists(markerPath)
            && DateTime.TryParse(File.ReadAllText(markerPath).Trim(), out var parsed))
        {
            lastDate = parsed.Date;
        }

        if (lastDate == today)
        {
            if (!File.Exists(markerPath))
            {
                File.WriteAllText(markerPath, today.ToString("yyyy-MM-dd"));
            }
            return;
        }

        if (File.Exists(ManagerPaths.ManagerLogPath) && new FileInfo(ManagerPaths.ManagerLogPath).Length > 0)
        {
            Directory.CreateDirectory(archiveDir);
            var archivePath = Path.Combine(archiveDir, $"manager-{lastDate:yyyy-MM-dd}.txt");
            File.AppendAllText(archivePath, File.ReadAllText(ManagerPaths.ManagerLogPath));
            File.WriteAllText(ManagerPaths.ManagerLogPath, string.Empty);
        }

        File.WriteAllText(markerPath, today.ToString("yyyy-MM-dd"));
    }
}
