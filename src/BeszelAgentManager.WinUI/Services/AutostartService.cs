using Microsoft.Win32;

namespace BeszelAgentManager.WinUI.Services;

internal sealed class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool MigrateLegacyInstallTarget()
    {
        var currentExecutable = Environment.ProcessPath;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(currentExecutable)
            || string.IsNullOrWhiteSpace(programFiles))
        {
            return false;
        }

        var installedAppDirectory = Path.Combine(programFiles, AppInfo.ProjectName, "app");
        if (!Path.GetFullPath(currentExecutable).StartsWith(
                Path.GetFullPath(installedAppDirectory) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        var command = key?.GetValue(AppInfo.ProjectName)?.ToString() ?? string.Empty;
        var legacyExecutable = Path.Combine(programFiles, AppInfo.ProjectName, $"{AppInfo.ProjectName}.exe");
        if (!CommandTargets(command, legacyExecutable))
        {
            return false;
        }

        var migrated = $"\"{currentExecutable}\"";
        if (command.Contains("--hidden", StringComparison.OrdinalIgnoreCase))
        {
            migrated += " --hidden";
        }

        key!.SetValue(AppInfo.ProjectName, migrated, RegistryValueKind.String);
        return true;
    }

    public (bool Enabled, bool StartHidden) GetState()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        var command = key?.GetValue(AppInfo.ProjectName)?.ToString() ?? string.Empty;
        return (
            !string.IsNullOrWhiteSpace(command),
            command.Contains("--hidden", StringComparison.OrdinalIgnoreCase));
    }

    public void SetState(bool enabled, bool startHidden)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (!enabled)
        {
            key.DeleteValue(AppInfo.ProjectName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, $"{AppInfo.ProjectName}.exe");
        var command = $"\"{executable}\"";
        if (startHidden)
        {
            command += " --hidden";
        }

        key.SetValue(AppInfo.ProjectName, command, RegistryValueKind.String);
    }

    private static bool CommandTargets(string command, string executable)
    {
        try
        {
            var trimmed = command.Trim();
            var target = trimmed.StartsWith('"')
                ? trimmed[1..].Split('"', 2)[0]
                : trimmed.Split(' ', 2)[0];
            return target.Length > 0
                && string.Equals(
                    Path.GetFullPath(target),
                    Path.GetFullPath(executable),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
