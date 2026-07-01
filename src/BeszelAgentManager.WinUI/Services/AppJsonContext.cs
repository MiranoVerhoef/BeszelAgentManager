using System.Text.Json;
using System.Text.Json.Serialization;
using BeszelAgentManager.Core;

namespace BeszelAgentManager.WinUI.Services;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AgentConfig))]
[JsonSerializable(typeof(BrokerRequest))]
[JsonSerializable(typeof(BrokerResponse))]
[JsonSerializable(typeof(string))]
internal partial class AppJsonContext : JsonSerializerContext;
