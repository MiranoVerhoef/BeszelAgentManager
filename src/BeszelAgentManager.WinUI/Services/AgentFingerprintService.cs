using System.Diagnostics;

namespace BeszelAgentManager.WinUI.Services;

internal sealed class AgentFingerprintService
{
    private readonly ConfigService _configService = new();

    public async Task<(bool Success, string Output)> ViewAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ManagerPaths.AgentExePath))
        {
            return (false, "Agent is not installed.");
        }

        var config = await _configService.LoadAsync(cancellationToken);
        try
        {
            var result = await RunAgentAsync(["fingerprint", "view"], config, cancellationToken);
            if (result.ExitCode == 0)
            {
                return (true, string.IsNullOrWhiteSpace(result.Output) ? "OK" : result.Output.Trim());
            }

            return (false, string.IsNullOrWhiteSpace(result.Output)
                ? $"Fingerprint view failed with exit code {result.ExitCode}."
                : result.Output.Trim());
        }
        catch (Exception ex)
        {
            return (false, $"Fingerprint view failed: {ex.Message}");
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAgentAsync(
        string[] arguments,
        AgentConfig config,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo.FileName = ManagerPaths.AgentExePath;
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        foreach (var item in BuildEnvironment(config))
        {
            process.StartInfo.Environment[item.Key] = item.Value;
        }

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = $"{await outputTask}\n{await errorTask}".Trim();
        return (process.ExitCode, output);
    }

    private static Dictionary<string, string> BuildEnvironment(AgentConfig config)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Add("KEY", config.Key);
        Add("TOKEN", config.Token);
        Add("HUB_URL", config.HubUrl);
        if (config.Listen is > 0)
        {
            Add("LISTEN", config.Listen.Value.ToString());
        }

        foreach (var row in new ConfigService().GetActiveEnvironmentRows(config))
        {
            Add(row.Name, row.Value);
        }

        return values;

        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[name] = value.Trim();
            }
        }
    }
}
