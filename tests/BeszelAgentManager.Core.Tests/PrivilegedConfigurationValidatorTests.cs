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

    [Fact]
    public void RejectsControlCharactersInActiveMappedValue()
    {
        using var config = JsonDocument.Parse("""
            {"env_active_names":["SYSTEM_NAME"],"system_name":"host\nPATH=C:\\evil"}
            """);

        var result = PrivilegedConfigurationValidator.Validate(config.RootElement);

        Assert.False(result.Success);
        Assert.Contains("system_name", result.Message);
    }

    [Theory]
    [InlineData("ca.pem")]
    [InlineData("\\\\server\\share\\ca.pem")]
    [InlineData("C:ca.pem")]
    public void RejectsNonLocalCaCertificatePath(string path)
    {
        using var config = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            env_active_names = new[] { "CA_CERT_FILE" },
            ca_cert_file = path,
        }));

        Assert.False(PrivilegedConfigurationValidator.Validate(config.RootElement).Success);
    }

    [Fact]
    public void AcceptsNewAgentVariables()
    {
        using var config = JsonDocument.Parse("""
            {
              "env_active_names":["CA_CERT_FILE","ZFS_INTERVAL"],
              "ca_cert_file":"C:\\certificates\\hub-ca.pem",
              "zfs_interval":"15m"
            }
            """);

        Assert.True(PrivilegedConfigurationValidator.Validate(config.RootElement).Success);
    }

    [Fact]
    public void AcceptsAbsoluteLocalCaCertificateCustomVariable()
    {
        using var config = JsonDocument.Parse("""
            {"env_custom":[{"name":"CA_CERT_FILE","value":"D:\\Beszel\\hub-ca.pem"}]}
            """);

        Assert.True(PrivilegedConfigurationValidator.Validate(config.RootElement).Success);
    }

    [Fact]
    public void RejectsRelativeCaCertificateCustomVariable()
    {
        using var config = JsonDocument.Parse("""
            {"env_custom":[{"name":"CA_CERT_FILE","value":"hub-ca.pem"}]}
            """);

        Assert.False(PrivilegedConfigurationValidator.Validate(config.RootElement).Success);
    }
}
