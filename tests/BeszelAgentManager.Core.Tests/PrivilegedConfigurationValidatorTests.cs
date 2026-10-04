using System.Text.Json;
using BeszelAgentManager.Core;
using Xunit;

namespace BeszelAgentManager.Core.Tests;

public sealed class PrivilegedConfigurationValidatorTests
{
    [Theory]
    [InlineData("SKIP_WIFI", "true")]
    [InlineData("DOCKER_IMAGE_CHECK", "false")]
    [InlineData("SKIP_SYSTEMD_LOGS", "true")]
    [InlineData("PACKAGE_UPDATES_INTERVAL", "1h")]
    public void AcceptsBeszel021VariablesInPickerAndCustomEntries(string name, string value)
    {
        var active = new Dictionary<string, object>
        {
            ["env_active_names"] = new[] { name },
            [name.ToLowerInvariant()] = value,
        };
        using var activeConfig = JsonDocument.Parse(JsonSerializer.Serialize(active));
        using var customConfig = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            env_custom = new[] { new { name, value } },
        }));

        Assert.True(PrivilegedConfigurationValidator.Validate(activeConfig.RootElement).Success);
        Assert.True(PrivilegedConfigurationValidator.Validate(customConfig.RootElement).Success);
    }

    [Theory]
    [InlineData("SKIP_WIFI")]
    [InlineData("DOCKER_IMAGE_CHECK")]
    [InlineData("SKIP_SYSTEMD_LOGS")]
    [InlineData("PACKAGE_UPDATES_INTERVAL")]
    public void RejectsControlCharactersInBeszel021Variables(string name)
    {
        var value = "true\nPATH=C:\\evil";
        var active = new Dictionary<string, object>
        {
            ["env_active_names"] = new[] { name },
            [name.ToLowerInvariant()] = value,
        };
        using var activeConfig = JsonDocument.Parse(JsonSerializer.Serialize(active));
        using var customConfig = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            env_custom = new[] { new { name, value } },
        }));

        Assert.False(PrivilegedConfigurationValidator.Validate(activeConfig.RootElement).Success);
        Assert.False(PrivilegedConfigurationValidator.Validate(customConfig.RootElement).Success);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("0")]
    public void AcceptsPrimitiveEnvironmentValues(string value)
    {
        using var config = JsonDocument.Parse($$"""
            {"env_active_names":["DOCKER_IMAGE_CHECK"],"docker_image_check":{{value}},
             "env_custom":[{"name":"SKIP_WIFI","value":{{value}}}]}
            """);
        Assert.True(PrivilegedConfigurationValidator.Validate(config.RootElement).Success);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    public void RejectsStructuredEnvironmentValues(string value)
    {
        using var activeConfig = JsonDocument.Parse($$"""
            {"env_active_names":["SKIP_WIFI"],"skip_wifi":{{value}}}
            """);
        using var customConfig = JsonDocument.Parse($$"""
            {"env_custom":[{"name":"SKIP_WIFI","value":{{value}}}]}
            """);
        Assert.False(PrivilegedConfigurationValidator.Validate(activeConfig.RootElement).Success);
        Assert.False(PrivilegedConfigurationValidator.Validate(customConfig.RootElement).Success);
    }

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

    [Fact]
    public void RejectsControlCharactersInListenBeforeServiceMutation()
    {
        using var config = JsonDocument.Parse("""
            {"listen":"45876\nPATH=C:\\evil"}
            """);

        var result = PrivilegedConfigurationValidator.Validate(config.RootElement);

        Assert.False(result.Success);
        Assert.Contains("listen", result.Message);
    }

    [Theory]
    [InlineData("{\"listen\":45876}")]
    [InlineData("{\"listen\":\"45876\"}")]
    [InlineData("{\"listen\":null}")]
    public void AcceptsSupportedListenRepresentations(string json)
    {
        using var config = JsonDocument.Parse(json);

        Assert.True(PrivilegedConfigurationValidator.Validate(config.RootElement).Success);
    }

    [Theory]
    [InlineData("{\"listen\":0}")]
    [InlineData("{\"listen\":65536}")]
    [InlineData("{\"listen\":\"not-a-port\"}")]
    public void RejectsInvalidListenValues(string json)
    {
        using var config = JsonDocument.Parse(json);

        Assert.False(PrivilegedConfigurationValidator.Validate(config.RootElement).Success);
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
