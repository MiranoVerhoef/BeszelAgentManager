namespace BeszelAgentManager.WinUI.Services;

internal sealed class AgentFingerprintService
{
    private readonly BackgroundBrokerClient _broker = new();

    public async Task<(bool Success, string Output)> ViewAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _broker.ViewAgentFingerprintAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return (false, $"Fingerprint view failed: {ex.Message}");
        }
    }
}
