using System.Text;

namespace BeszelAgentManager.Core;

public static class ProcessOutputEncoding
{
    public static Encoding? ForExecutable(string fileName) =>
        string.Equals(Path.GetFileName(fileName), "nssm.exe", StringComparison.OrdinalIgnoreCase)
            ? Encoding.Unicode
            : null;
}
