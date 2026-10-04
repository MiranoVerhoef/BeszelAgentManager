using System.Text.Json;

namespace BeszelAgentManager.Core;

public static class AgentEnvironmentValue
{
    // Beszel flags require lowercase true/false; false is an explicit override.
    public static bool TryFormat(JsonElement value, out string text)
    {
        text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
        return value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;
    }
}
