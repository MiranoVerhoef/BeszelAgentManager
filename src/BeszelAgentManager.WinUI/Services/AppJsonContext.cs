using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeszelAgentManager.WinUI.Services;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AgentConfig))]
[JsonSerializable(typeof(string))]
internal partial class AppJsonContext : JsonSerializerContext;
