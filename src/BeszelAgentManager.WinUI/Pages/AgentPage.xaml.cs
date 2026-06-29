using BeszelAgentManager.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BeszelAgentManager.WinUI.Pages;

public sealed partial class AgentPage : Page
{
    private readonly SystemStatusService _systemStatusService = new();

    public AgentPage()
    {
        InitializeComponent();
        Loaded += AgentPage_Loaded;
    }

    private async void AgentPage_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        RefreshButton.IsEnabled = false;
        try
        {
            var status = await _systemStatusService.GetAgentStatusAsync();
            ServiceNameText.Text = $"Name: {status.ServiceName}";
            ServiceStateText.Text = $"State: {status.ServiceState}";
            ServicePidText.Text = status.ProcessId is int pid ? $"PID: {pid}" : "PID: -";
            ServiceBinaryText.Text = string.IsNullOrWhiteSpace(status.BinaryPath) ? "Binary: -" : $"Binary: {status.BinaryPath}";
            AgentPathText.Text = $"Path: {status.AgentExePath}";
            AgentExistsText.Text = status.AgentExeExists ? "File: found" : "File: missing";
            AgentVersionText.Text = $"Version: {status.AgentVersion}";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }
}
