using System.Reflection;

namespace BeszelAgentManager.WinUI.Services;

internal static class AppInfo
{
    public const string ProjectName = "BeszelAgentManager";
    public const string ManagerRepo = "MiranoVerhoef/BeszelAgentManager";
    public const string BundledInstallerAssetName = "BeszelAgentManagerSetup.exe";
    public const string LiteInstallerAssetName = "BeszelAgentManagerSetup-Lite.exe";

    public static string Version => ReadBundledVersion();
    public static string RuntimeVariant => ReadRuntimeVariant();
    public static bool IsLiteRuntimeVariant =>
        string.Equals(RuntimeVariant, "lite", StringComparison.OrdinalIgnoreCase);
    public static string RuntimeVariantDisplayName => IsLiteRuntimeVariant ? "Lite" : "Bundled";
    public static string InstallerAssetName => IsLiteRuntimeVariant
        ? LiteInstallerAssetName
        : BundledInstallerAssetName;

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

    private static string ReadRuntimeVariant()
    {
        try
        {
            var variantPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                ProjectName,
                "manager-runtime-variant.txt");
            if (File.Exists(variantPath)
                && string.Equals(File.ReadAllText(variantPath).Trim(), "lite", StringComparison.OrdinalIgnoreCase))
            {
                return "lite";
            }
        }
        catch
        {
            // Existing installations have no variant marker and remain bundled.
        }

        return "bundled";
    }
}
