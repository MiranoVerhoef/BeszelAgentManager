using System.Reflection;

namespace BeszelAgentManager.WinUI.Services;

internal static class AppInfo
{
    public const string ProjectName = "BeszelAgentManager";
    public const string ManagerRepo = "MiranoVerhoef/BeszelAgentManager";
    public const string InstallerAssetName = "BeszelAgentManagerSetup.exe";

    public static string Version => ReadBundledVersion();

    private static string ReadBundledVersion()
    {
        var baseDir = AppContext.BaseDirectory;
        var versionPath = Path.Combine(baseDir, "VERSION");
        if (File.Exists(versionPath))
        {
            var version = File.ReadAllText(versionPath).Trim();
            if (!string.IsNullOrWhiteSpace(version))
            {
                return version;
            }
        }

        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
