using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using BeszelAgentManager.Core;

namespace BeszelAgentManager.WinUI.Services;

internal sealed class BackgroundBrokerClient
{
    private const string PipeName = "BeszelAgentManager.Background.v1";
    private const int ProtocolVersion = 1;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<int> RunServiceActionAsync(string action)
    {
        if (action is not ("start" or "stop" or "restart"))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        return SendAsync($"service.{action}");
    }

    public async Task<int> OpenServiceEditorAsync()
    {
        var helperPath = GetHelperPath();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = helperPath,
            Arguments = "--edit-service",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        }) ?? throw new InvalidOperationException("Could not start the elevated service editor.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    public Task<int> ApplyConfigurationAsync() => SendAsync("config.apply");

    public Task<int> ApplyManagerTasksAsync() => SendAsync("schedules.apply");

    public Task<int> InstallAgentAsync() => SendAsync("agent.install");

    public Task<int> UpdateAgentAsync() => SendAsync("agent.update");

    public Task<int> InstallAgentVersionAsync(string version) =>
        SendAsync("agent.installVersion", new Dictionary<string, string> { ["version"] = version });

    public Task<int> UninstallAgentAsync(bool keepAgentLogs) =>
        SendAsync("agent.uninstall", new Dictionary<string, string> { ["keepLogs"] = keepAgentLogs.ToString() });

    public Task<int> RotateAgentLogsAsync() => SendAsync("agent.logs.rotate");

    public Task<int> ResetAgentFingerprintAsync() => SendAsync("agent.fingerprint.reset");

    public Task<int> SetDefenderExclusionAsync(bool enabled) =>
        SendAsync("defender.set", new Dictionary<string, string> { ["enabled"] = enabled.ToString() });

    public Task<int> InstallManagerVersionAsync(string tag) =>
        SendAsync("manager.installVersion", new Dictionary<string, string> { ["tag"] = tag });

    public async Task<string?> GetAgentVersionAsync()
    {
        var response = await SendResponseAsync("agent.version");
        return response.Success && IsAgentVersion(response.Message)
            ? response.Message
            : null;
    }

    private async Task<int> SendAsync(string action, Dictionary<string, string>? arguments = null)
    {
        var response = await SendResponseAsync(action, arguments);
        return response.Success ? 0 : response.ErrorCode == 0 ? 1 : response.ErrorCode;
    }

    private async Task<BrokerResponse> SendResponseAsync(
        string action,
        Dictionary<string, string>? arguments = null)
    {
        await _gate.WaitAsync();
        try
        {
            var request = new BrokerRequest
            {
                ProtocolVersion = ProtocolVersion,
                RequestId = Guid.NewGuid().ToString("N"),
                Action = action,
                Arguments = arguments,
            };

            await using var pipe = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Impersonation);
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await pipe.ConnectAsync(connectTimeout.Token);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
            {
                throw new InvalidOperationException(
                    "The BeszelAgentManager background service is not available. Reinstall or restart the background service.",
                    ex);
            }

            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            await using var writer = new StreamWriter(pipe, utf8, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, utf8, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, AppJsonContext.Default.BrokerRequest));
            var payload = await reader.ReadLineAsync()
                ?? throw new InvalidOperationException("The background service closed the broker connection without a response.");
            var response = JsonSerializer.Deserialize(payload, AppJsonContext.Default.BrokerResponse)
                ?? throw new InvalidOperationException("The background service returned an invalid broker response.");
            if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The background service returned a mismatched broker response.");
            }

            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsAgentVersion(string value)
    {
        var parts = value.Split('.', 4, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3
            && int.TryParse(parts[0], out _)
            && int.TryParse(parts[1], out _)
            && int.TryParse(parts[2].Split('-', '+')[0], out _);
    }

    private static string GetHelperPath()
    {
        var helperPath = Path.Combine(AppContext.BaseDirectory, "helper", "BeszelAgentManager.Helper.exe");
        return File.Exists(helperPath)
            ? helperPath
            : throw new FileNotFoundException("The privileged helper is not present in the application folder.", helperPath);
    }
}
