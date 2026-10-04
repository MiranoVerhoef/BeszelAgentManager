using System.Text.Json;
using BeszelAgentManager.Core;
using BeszelAgentManager.WinUI.Services;
using Xunit;

namespace BeszelAgentManager.Core.Tests;

public sealed class AgentEnvironmentValueTests
{
    [Theory]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("0", "0")]
    [InlineData("15", "15")]
    [InlineData("\"1h\"", "1h")]
    [InlineData("null", "")]
    public void FormatsSupportedValuesConsistently(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.True(AgentEnvironmentValue.TryFormat(document.RootElement, out var text));
        Assert.Equal(expected, text);

        var config = new AgentConfig();
        config.ExtraFields["docker_image_check"] = document.RootElement.Clone();
        Assert.Equal(expected, config.GetEnvironmentValue("docker_image_check"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    public void RejectsStructuredValues(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(AgentEnvironmentValue.TryFormat(document.RootElement, out _));
    }

    [Theory]
    [InlineData("SKIP_WIFI", "true", "false")]
    [InlineData("DOCKER_IMAGE_CHECK", "true", "false")]
    [InlineData("SKIP_SYSTEMD_LOGS", "\"true\"", "\"false\"")]
    [InlineData("PACKAGE_UPDATES_INTERVAL", "\"1h\"", "\"0\"")]
    public void NewValuesChangeApplyFingerprint(string name, string before, string after)
    {
        var config = new AgentConfig();
        config.EnvActiveNames.Add(name);
        using var beforeDocument = JsonDocument.Parse(before);
        using var afterDocument = JsonDocument.Parse(after);
        config.ExtraFields[name.ToLowerInvariant()] = beforeDocument.RootElement.Clone();
        var fingerprint = config.ApplyFingerprint();
        config.ExtraFields[name.ToLowerInvariant()] = afterDocument.RootElement.Clone();

        Assert.NotEqual(fingerprint, config.ApplyFingerprint());
    }
}
