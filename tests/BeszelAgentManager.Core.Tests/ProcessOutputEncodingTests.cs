using System.Text;
using BeszelAgentManager.Core;
using Xunit;

namespace BeszelAgentManager.Core.Tests;

public sealed class ProcessOutputEncodingTests
{
    [Theory]
    [InlineData("nssm.exe")]
    [InlineData("NSSM.EXE")]
    public void NssmUsesUtf16AndPreservesUnicode(string executable)
    {
        const string output = "SYSTEM_NAME=Bärbar åäö\r\nEXTRA_FILESYSTEMS=C:\\Daten\\über\r\n";
        var encoding = ProcessOutputEncoding.ForExecutable(Path.Combine("tools", executable));
        Assert.NotNull(encoding);
        Assert.Equal(Encoding.Unicode.CodePage, encoding.CodePage);
        Assert.Equal(output, encoding.GetString(Encoding.Unicode.GetBytes(output)));
    }

    [Theory]
    [InlineData("sc.exe")]
    [InlineData("netsh.exe")]
    [InlineData("icacls.exe")]
    [InlineData("beszel-agent.exe")]
    public void OtherToolsKeepDefaultDecoding(string executable)
    {
        Assert.Null(ProcessOutputEncoding.ForExecutable(executable));
    }
}
