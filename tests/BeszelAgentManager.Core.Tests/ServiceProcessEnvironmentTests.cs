using BeszelAgentManager.Core;
using Xunit;

namespace BeszelAgentManager.Core.Tests;

public sealed class ServiceProcessEnvironmentTests
{
    private static readonly Dictionary<string, string?> SystemEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        ["APPDATA"] = @"C:\Windows\System32\config\systemprofile\AppData\Roaming",
        ["LOCALAPPDATA"] = @"C:\Windows\System32\config\systemprofile\AppData\Local",
        ["PATH"] = @"C:\Windows\System32",
    };

    [Fact]
    public void DefaultServiceKeepsSystemProfileWithoutCreatingDataDirOverride()
    {
        var result = ServiceProcessEnvironment.Compose(SystemEnvironment, null, null);
        Assert.Equal(SystemEnvironment["APPDATA"], result["APPDATA"]);
        Assert.Equal(SystemEnvironment["LOCALAPPDATA"], result["LOCALAPPDATA"]);
        Assert.False(result.ContainsKey("DATA_DIR"));
    }

    [Fact]
    public void AppliedDataDirOverridesInheritedValueWithoutChangingProfiles()
    {
        var inherited = new Dictionary<string, string?>(SystemEnvironment) { ["DATA_DIR"] = "inherited" };
        var result = ServiceProcessEnvironment.Compose(inherited, [], [@"DATA_DIR=C:\ProgramData\Beszel Agent\data"]);
        Assert.Equal(@"C:\ProgramData\Beszel Agent\data", result["DATA_DIR"]);
        Assert.Equal(SystemEnvironment["APPDATA"], result["APPDATA"]);
    }

    [Fact]
    public void ExtraEnvironmentOverridesReplacementCaseInsensitively()
    {
        var result = ServiceProcessEnvironment.Compose(SystemEnvironment, ["DATA_DIR=base", "ONLY_BASE=1"], ["data_dir=applied"]);
        Assert.Equal("applied", result["DATA_DIR"]);
        Assert.Equal("1", result["ONLY_BASE"]);
        Assert.False(result.ContainsKey("APPDATA"));
    }

    [Fact]
    public void PreservesUnicodeEqualsWhitespaceEmptyValuesAndPrefixedDataDir()
    {
        var result = ServiceProcessEnvironment.Compose(SystemEnvironment, null,
            ["SYSTEM_NAME=Bärbar åäö", @"DATA_DIR=C:\data\Bärbar", "BESZEL_AGENT_DATA_DIR=preferred", "TOKEN=a=b==", "EMPTY=", "SPACES=  value  ", "invalid"]);
        Assert.Equal("Bärbar åäö", result["SYSTEM_NAME"]);
        Assert.Equal(@"C:\data\Bärbar", result["DATA_DIR"]);
        Assert.Equal("preferred", result["BESZEL_AGENT_DATA_DIR"]);
        Assert.Equal("a=b==", result["TOKEN"]);
        Assert.Equal("", result["EMPTY"]);
        Assert.Equal("  value  ", result["SPACES"]);
        Assert.False(result.ContainsKey("invalid"));
        Assert.False(SystemEnvironment.ContainsKey("SYSTEM_NAME"));
    }
}
