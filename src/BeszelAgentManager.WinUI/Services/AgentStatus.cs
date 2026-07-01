namespace BeszelAgentManager.WinUI.Services;

internal sealed record AgentStatus(
    bool ServiceExists,
    string ServiceName,
    string ServiceState,
    int? ProcessId,
    string BinaryPath,
    bool AgentExeExists,
    string AgentExePath,
    string AgentVersion);
