using BeszelAgentManager.WinUI.Services;
using Microsoft.UI.Xaml.Controls;

namespace BeszelAgentManager.WinUI.Pages;

public sealed partial class DashboardPage : Page
{
    private readonly SystemStatusService _systemStatusService = new();

    public DashboardPage()
    {
        InitializeComponent();
        ManagerVersionText.Text = $"Installed version: {AppInfo.Version}";
        InstallPathText.Text = $"Install path: {ManagerPaths.InstallDir}";
        ConfigPathText.Text = $"Config: {ManagerPaths.ConfigPath}";
        DataPathText.Text = $"Data: {ManagerPaths.DataDir}";
        Loaded += DashboardPage_Loaded;
    }

    private async void DashboardPage_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var status = await _systemStatusService.GetAgentStatusAsync();
        ServiceStateText.Text = $"State: {status.ServiceState}";
        ServicePidText.Text = status.ProcessId is int pid ? $"PID: {pid}" : "PID: -";
    }
}
