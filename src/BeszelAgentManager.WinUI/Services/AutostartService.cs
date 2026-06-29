using Microsoft.Win32;

namespace BeszelAgentManager.WinUI.Services;

internal sealed class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

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
}
