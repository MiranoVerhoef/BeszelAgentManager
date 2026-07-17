using System.Diagnostics;

namespace BeszelAgentManager.WinUI.Services;

internal sealed class SystemStatusService
{
    private const string AgentServiceName = "Beszel Agent";
    private AgentStatus? _lastKnownStatus;

    public async Task<AgentStatus> GetAgentStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var brokerStatus = await App.Broker.GetAgentStatusAsync();
            var status = new AgentStatus(
                brokerStatus.ServiceExists,
                brokerStatus.ServiceName,
                brokerStatus.ServiceState,
                brokerStatus.ProcessId,
                brokerStatus.BinaryPath,
                brokerStatus.AgentExeExists,
                brokerStatus.AgentExePath,
                brokerStatus.AgentVersion);
            _lastKnownStatus = status;
            return status;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Logger.Debug($"Broker agent status query failed: {ex.Message}");
            if (_lastKnownStatus is not null)
            {
                return _lastKnownStatus;
            }

            var agentExists = File.Exists(ManagerPaths.AgentExePath);
            return new AgentStatus(
                false,
                AgentServiceName,
                "Unknown",
                null,
                string.Empty,
                agentExists,
                ManagerPaths.AgentExePath,
                agentExists ? GetFileVersion(ManagerPaths.AgentExePath) : "Not installed");
        }
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
}
