using BeszelAgentManager.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BeszelAgentManager.WinUI.Pages;

public sealed partial class UpdatesPage : Page
{
    private readonly ManagerUpdateService _managerUpdateService = new();

    public UpdatesPage()
    {
        InitializeComponent();
    }

    private async void CheckManagerButton_Click(object sender, RoutedEventArgs e)
    {
        CheckManagerButton.IsEnabled = false;
        ManagerUpdateInfoBar.Severity = InfoBarSeverity.Informational;
        ManagerUpdateInfoBar.Title = "Checking";
        ManagerUpdateInfoBar.Message = "Fetching BeszelAgentManager release information from GitHub.";
        ReleaseNotesTextBox.Text = string.Empty;

        try
        {
            var release = await _managerUpdateService.FetchLatestReleaseAsync(IncludePrereleasesCheckBox.IsChecked == true);
            if (release is null)
            {
                ManagerUpdateInfoBar.Severity = InfoBarSeverity.Warning;
                ManagerUpdateInfoBar.Title = "No usable release found";
                ManagerUpdateInfoBar.Message = $"Expected asset: {AppInfo.InstallerAssetName}";
                return;
            }

            if (VersionComparer.IsSameOrOlder(AppInfo.Version, release.Version))
            {
                ManagerUpdateInfoBar.Severity = InfoBarSeverity.Success;
                ManagerUpdateInfoBar.Title = "Up to date";
                ManagerUpdateInfoBar.Message = $"Installed: {AppInfo.Version}. Latest on GitHub: {release.Version} ({release.Tag}).";
                ReleaseNotesTextBox.Text = TrimReleaseNotes(release.Body);
                return;
            }

            ManagerUpdateInfoBar.Severity = InfoBarSeverity.Informational;
            ManagerUpdateInfoBar.Title = "Update available";
            ManagerUpdateInfoBar.Message = $"Installed: {AppInfo.Version}. Latest on GitHub: {release.Version} ({release.Tag}).";
            ReleaseNotesTextBox.Text = TrimReleaseNotes(release.Body);
        }
        catch (Exception ex)
        {
            ManagerUpdateInfoBar.Severity = InfoBarSeverity.Error;
            ManagerUpdateInfoBar.Title = "Update check failed";
            ManagerUpdateInfoBar.Message = ex.Message;
        }
        finally
        {
            CheckManagerButton.IsEnabled = true;
        }
    }

    private static string TrimReleaseNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var lines = body.Replace("\r\n", "\n").Split('\n').Select(static line => line.TrimEnd()).ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count > 24)
        {
            lines = lines.Take(24).Concat(new[] { "...", "(truncated)" }).ToList();
        }

        return string.Join(Environment.NewLine, lines);
    }
}
