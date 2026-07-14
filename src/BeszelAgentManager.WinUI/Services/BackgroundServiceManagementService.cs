using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BeszelAgentManager.WinUI.Services;

internal sealed partial class BackgroundServiceManagementService
{
    private const string ServiceName = "BeszelAgentManager Background";

    public async Task<BackgroundServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunScAsync(["query", ServiceName], cancellationToken);
        if (result.ExitCode == 1060
            || result.Output.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
        {
            return new BackgroundServiceStatus(false, "Not installed");
        }

        var match = ServiceStateRegex().Match(result.Output);
        var state = match.Success && int.TryParse(match.Groups["code"].Value, out var code)
            ? code switch
            {
                1 => "STOPPED",
                2 => "START_PENDING",
                3 => "STOP_PENDING",
                4 => "RUNNING",
                5 => "CONTINUE_PENDING",
                6 => "PAUSE_PENDING",
                7 => "PAUSED",
                _ => "Unknown",
            }
            : "Unknown";
        return new BackgroundServiceStatus(result.ExitCode == 0, state);
    }

    public Task<int> InstallAsync() => RunElevatedHelperAsync("--install-background-service");

    public Task<int> UninstallAsync() => RunElevatedHelperAsync("--uninstall-background-service-only");

    public static string ReadLastHelperError()
    {
        try
        {
            return File.Exists(ManagerPaths.HelperLastErrorPath)
                ? File.ReadAllText(ManagerPaths.HelperLastErrorPath).Trim()
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<int> RunElevatedHelperAsync(string argument)
    {
        var helperPath = Path.Combine(AppContext.BaseDirectory, "helper", "BeszelAgentManager.Helper.exe");
        if (!File.Exists(helperPath))
        {
            throw new FileNotFoundException("The background-service helper is missing. Reinstall BeszelAgentManager.", helperPath);
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = helperPath,
            Arguments = argument,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        }) ?? throw new InvalidOperationException("Could not start the background-service helper.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static async Task<(int ExitCode, string Output)> RunScAsync(
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not query the Windows service manager.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, $"{await outputTask}{Environment.NewLine}{await errorTask}".Trim());
    }

    [GeneratedRegex(@"STATE\s*:\s*(?<code>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceStateRegex();
}

internal readonly record struct BackgroundServiceStatus(bool IsInstalled, string State);
