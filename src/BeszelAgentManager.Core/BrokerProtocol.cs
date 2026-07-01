namespace BeszelAgentManager.Core;

public sealed class BrokerRequest
{
    public int ProtocolVersion { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public Dictionary<string, string>? Arguments { get; set; }
}

public sealed class BrokerResponse
{
    public int ProtocolVersion { get; set; } = 1;
    public string RequestId { get; set; } = string.Empty;
    public string State { get; set; } = "completed";
    public bool Success { get; set; }
    public int ErrorCode { get; set; }
    public string Message { get; set; } = string.Empty;

    public static BrokerResponse Completed(string requestId, string? message = null) => new()
    {
        RequestId = requestId,
        Success = true,
        Message = message ?? "The privileged action completed.",
    };

    public static BrokerResponse Failed(string requestId, int errorCode, string message) => new()
    {
        RequestId = requestId,
        Success = false,
        ErrorCode = errorCode,
        Message = message,
    };
}
