namespace BeszelAgentManager.WinUI.Services;

internal sealed class LogReaderService
{
    public IReadOnlyList<LogFileItem> ListManagerLogFiles()
    {
        var files = new List<LogFileItem>();
        if (File.Exists(ManagerPaths.ManagerLogPath))
        {
            files.Add(new LogFileItem("Current (manager.log)", ManagerPaths.ManagerLogPath));
        }

        var archiveDirs = new[]
        {
            ManagerPaths.ManagerLogArchiveDir,
            Path.Combine(ManagerPaths.DataDir, "manager_logs"),
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var archiveDir in archiveDirs)
        {
            if (Directory.Exists(archiveDir))
            {
                files.AddRange(Directory
                    .EnumerateFiles(archiveDir, "manager-*.txt")
                    .Where(path => !files.Any(existing => string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(File.GetLastWriteTime)
                    .Select(path => new LogFileItem(Path.GetFileName(path), path)));
            }
        }

        return files;
    }

    public IReadOnlyList<LogFileItem> ListAgentLogFiles()
    {
        var files = new List<LogFileItem>();
        if (File.Exists(ManagerPaths.AgentLogPath))
        {
            files.Add(new LogFileItem("Current (beszel-agent.log)", ManagerPaths.AgentLogPath));
        }

        var agentLogDir = Path.GetDirectoryName(ManagerPaths.AgentLogPath) ?? string.Empty;
        if (Directory.Exists(agentLogDir))
        {
            files.AddRange(Directory
                .EnumerateFiles(agentLogDir, "*.txt")
                .OrderByDescending(File.GetLastWriteTime)
                .Select(path => new LogFileItem(Path.GetFileName(path), path)));

            files.AddRange(Directory
                .EnumerateFiles(agentLogDir, "beszel-agent-*.log")
                .Where(path => !files.Any(existing => string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(File.GetLastWriteTime)
                .Select(path => new LogFileItem(Path.GetFileName(path), path)));
        }

        return files;
    }

    public async Task<string> ReadAllAsync(string path, string typeFilter = "All", CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return $"Log file not found: {path}";
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync(cancellationToken);
            if (string.IsNullOrEmpty(content))
            {
                return $"Log file is empty: {path}";
            }

            return FilterLogText(content, typeFilter);
        }
        catch (Exception ex)
        {
            return $"Could not read log file: {path}{Environment.NewLine}{ex.Message}";
        }
    }

    public async Task<string> ReadTailAsync(
        string path,
        int maxLines = 500,
        int maxBytes = 128 * 1024,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return $"Log file not found: {path}";
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                useAsync: true);
            if (stream.Length > maxBytes)
            {
                stream.Seek(-maxBytes, SeekOrigin.End);
            }

            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync(cancellationToken);
            if (string.IsNullOrEmpty(content))
            {
                return $"Log file is empty: {path}";
            }

            var lines = content
                .Replace("\r\n", "\n")
                .Split('\n')
                .TakeLast(maxLines)
                .ToList();

            return lines.Count == 0
                ? $"Log file is empty: {path}"
                : string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return $"Could not read log file: {path}{Environment.NewLine}{ex.Message}";
        }
    }

    public async Task<string> ReadTailFilteredAsync(
        string path,
        string typeFilter = "All",
        int maxLines = 500,
        CancellationToken cancellationToken = default)
    {
        var content = await ReadTailAsync(path, maxLines, cancellationToken: cancellationToken);
        return FilterLogText(content, typeFilter);
    }

    private static string FilterLogText(string content, string typeFilter)
    {
        if (string.IsNullOrWhiteSpace(typeFilter) || string.Equals(typeFilter, "All", StringComparison.OrdinalIgnoreCase))
        {
            return content;
        }

        var needle = typeFilter.Trim().ToLowerInvariant() switch
        {
            "info" => "info",
            "warning" => "warn",
            "warnings" => "warn",
            "error" => "error",
            "errors" => "error",
            "debug" => "debug",
            "task" => "task",
            _ => string.Empty,
        };

        if (string.IsNullOrEmpty(needle))
        {
            return content;
        }

        var filtered = content
            .Replace("\r\n", "\n")
            .Split('\n')
            .Where(line => line.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return filtered.Count == 0
            ? $"No {typeFilter.ToLowerInvariant()} entries in selected log."
            : string.Join(Environment.NewLine, filtered);
    }
}
