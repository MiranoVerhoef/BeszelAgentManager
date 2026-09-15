using System.Text.Json;
using BeszelAgentManager.Core;
using Xunit;

namespace BeszelAgentManager.Core.Tests;

public sealed class PrivilegedConfigurationValidatorTests
{
    [Fact]
    public void AcceptsSafeAgentConfiguration()
    {
        using var config = JsonDocument.Parse("""
            {
              "key": "ssh-ed25519 test",
              "token": "token",
              "hub_url": "https://hub.example.test",
              "env_active_names": ["SYSTEM_NAME", "EXTRA_FILESYSTEMS"],
              "env_custom": [{"name":"LOG_LEVEL","value":"debug"}]
            }
            """);

        Assert.True(PrivilegedConfigurationValidator.Validate(config.RootElement).Success);
    }

    [Theory]
    [InlineData("TOKEN_FILE")]
    [InlineData("KEY_FILE")]
    [InlineData("DATA_DIR")]
    public void RejectsFileBackedVariables(string name)
    {
        using var config = JsonDocument.Parse($$"""{"env_active_names":["{{name}}"]}""");

        var result = PrivilegedConfigurationValidator.Validate(config.RootElement);

        Assert.False(result.Success);
        Assert.Contains(name, result.Message);
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("TEMP")]
    [InlineData("GODEBUG")]
    [InlineData("DOTNET_ROOT")]
    [InlineData("BESZEL_AGENT_HUB_URL")]
    [InlineData("TOKEN")]
    [InlineData("SAFE_CUSTOM")]
    public void RejectsProcessControlAndReservedCustomVariables(string name)
    {
        using var config = JsonDocument.Parse($$"""{"env_custom":[{"name":"{{name}}","value":"test"}]}""");

        Assert.False(PrivilegedConfigurationValidator.Validate(config.RootElement).Success);
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("https://user:password@example.test")]
    [InlineData("not-a-url")]
    public void RejectsUnsafeHubUrls(string url)
    {
        using var config = JsonDocument.Parse($$"""{"hub_url":"{{url}}"}""");

        Assert.False(PrivilegedConfigurationValidator.Validate(config.RootElement).Success);
    }
}
