namespace BeszelAgentManager.WinUI.Services;

internal static class ManagerPaths
{
    private static readonly Lazy<string> WritableManagerLogPath = new(ResolveWritableManagerLogPath);

    public static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    public static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public static string InstallDir => Path.Combine(ProgramFiles, AppInfo.ProjectName);
    public static string DataDir => Path.Combine(ProgramData, AppInfo.ProjectName);
    public static string ConfigPath => Path.Combine(DataDir, "config.json");
    public static string HelperLastErrorPath => Path.Combine(DataDir, "helper-last-error.txt");
    public static string DnsFallbackStatePath => Path.Combine(DataDir, "dns-fallback-state.json");
    public static string ManagerLogPath => WritableManagerLogPath.Value;
    public static string ManagerLogDir => Path.GetDirectoryName(ManagerLogPath) ?? DataDir;
    public static string ManagerLogArchiveDir => Path.Combine(ManagerLogDir, "manager_logs");
    public static string LocalSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppInfo.ProjectName,
        "ui-settings.json");

    public static string AgentDir => Path.Combine(ProgramFiles, "Beszel-Agent");
    public static string AgentExePath => Path.Combine(AgentDir, "beszel-agent.exe");
    public static string AgentLogPath => Path.Combine(DataDir, "agent_logs", "beszel-agent.log");

    private static string ResolveWritableManagerLogPath()
    {
        var preferred = Path.Combine(DataDir, "manager.log");
        try
        {
            Directory.CreateDirectory(DataDir);
            using var stream = new FileStream(preferred, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return preferred;
        }
        catch
        {
            var fallbackDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppInfo.ProjectName);
            Directory.CreateDirectory(fallbackDir);
            return Path.Combine(fallbackDir, "manager.log");
        }
    }
}
