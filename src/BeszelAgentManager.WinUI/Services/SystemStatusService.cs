using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BeszelAgentManager.WinUI.Services;

internal sealed partial class SystemStatusService
{
    private const string AgentServiceName = "Beszel Agent";
    private const string LegacyAgentServiceName = "BeszelAgentManager";
    private string _cachedAgentVersion = string.Empty;
    private long _cachedAgentLength = -1;
    private DateTime _cachedAgentWriteTimeUtc = DateTime.MinValue;

    public async Task<AgentStatus> GetAgentStatusAsync(CancellationToken cancellationToken = default)
    {
        var serviceName = await ResolveServiceNameAsync(cancellationToken);
        var queryOutput = await RunScAsync(["queryex", serviceName], cancellationToken);
        var serviceExists = !DoesNotExist(queryOutput);
        var state = serviceExists ? ParseState(queryOutput) : "Not installed";
        var pid = serviceExists ? ParsePid(queryOutput) : null;
        var binaryPath = serviceExists ? await GetBinaryPathAsync(serviceName, cancellationToken) : string.Empty;
        var agentExeExists = File.Exists(ManagerPaths.AgentExePath);
        var agentVersion = agentExeExists
            ? await GetCachedAgentVersionAsync(ManagerPaths.AgentExePath, cancellationToken)
            : "Not installed";

        return new AgentStatus(
            serviceExists,
            serviceName,
            state,
            pid,
            binaryPath,
            agentExeExists,
            ManagerPaths.AgentExePath,
            agentVersion);
    }

    private static async Task<string> ResolveServiceNameAsync(CancellationToken cancellationToken)
    {
        var current = await RunScAsync(["query", AgentServiceName], cancellationToken);
        if (!DoesNotExist(current))
        {
            return AgentServiceName;
        }

        var legacy = await RunScAsync(["query", LegacyAgentServiceName], cancellationToken);
        return DoesNotExist(legacy) ? AgentServiceName : LegacyAgentServiceName;
    }

    private static async Task<string> GetBinaryPathAsync(string serviceName, CancellationToken cancellationToken)
    {
        var output = await RunScAsync(["qc", serviceName], cancellationToken);
        foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line.Split(':', 2).LastOrDefault()?.Trim().Trim('"') ?? string.Empty;
            return value;
        }

        return string.Empty;
    }

    private static async Task<string> RunScAsync(string[] args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "sc.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return string.Empty;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return $"{stdout}{Environment.NewLine}{stderr}".Trim();
    }

    private static bool DoesNotExist(string output)
    {
        return output.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || output.Contains("EnumQueryServicesStatus", StringComparison.OrdinalIgnoreCase);
    }

    private static string ParseState(string output)
    {
        var match = ServiceStateRegex().Match(output);
        return match.Success ? match.Groups["name"].Value.Trim() : "Unknown";
    }

    private static int? ParsePid(string output)
    {
        var match = PidRegex().Match(output);
        return match.Success && int.TryParse(match.Groups["pid"].Value, out var pid) && pid > 0 ? pid : null;
    }

    private static string GetFileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return !string.IsNullOrWhiteSpace(info.ProductVersion)
                ? info.ProductVersion
                : info.FileVersion ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private static async Task<string> GetAgentVersionAsync(string path, CancellationToken cancellationToken)
    {
        foreach (var argument in new[] { "--version", "version", "-version" })
        {
            try
            {
                var result = await RunProcessAsync(path, [argument], TimeSpan.FromSeconds(3), cancellationToken);
                var match = VersionRegex().Match(result.Output);
                if (match.Success)
                {
                    return match.Groups["version"].Value;
                }
            }
            catch (Exception ex)
            {
                App.Logger.Debug($"Agent version command failed ({argument}): {ex.Message}");
            }
        }

        return GetFileVersion(path);
    }

    private async Task<string> GetCachedAgentVersionAsync(string path, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!string.IsNullOrWhiteSpace(_cachedAgentVersion)
            && file.Length == _cachedAgentLength
            && file.LastWriteTimeUtc == _cachedAgentWriteTimeUtc)
        {
            return _cachedAgentVersion;
        }

        _cachedAgentVersion = await GetAgentVersionAsync(path, cancellationToken);
        _cachedAgentLength = file.Length;
        _cachedAgentWriteTimeUtc = file.LastWriteTimeUtc;
        return _cachedAgentVersion;
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName,
        string[] arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return (1, string.Empty);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        await process.WaitForExitAsync(timeoutSource.Token);
        return (process.ExitCode, $"{await stdoutTask}{Environment.NewLine}{await stderrTask}".Trim());
    }

    [GeneratedRegex(@"STATE\s*:\s*\d+\s+(?<name>[A-Z_]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceStateRegex();

    [GeneratedRegex(@"PID\s*:\s*(?<pid>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PidRegex();

    [GeneratedRegex(@"\b(?<version>\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?)\b")]
    private static partial Regex VersionRegex();
}
