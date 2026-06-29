using Microsoft.UI.Xaml;
using BeszelAgentManager.WinUI.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.AppLifecycle;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace BeszelAgentManager.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private AppInstance? _mainInstance;
    public static MainWindow MainWindow { get; private set; } = null!;
    internal static ElevatedHelperService ElevatedHelper { get; } = new();
    internal static ManagerLogger Logger { get; } = new();
    
    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var currentInstance = AppInstance.GetCurrent();
        _mainInstance = AppInstance.FindOrRegisterForKey("BeszelAgentManager.Main");
        if (!_mainInstance.IsCurrent)
        {
            await _mainInstance.RedirectActivationToAsync(currentInstance.GetActivatedEventArgs());
            Exit();
            return;
        }

        _mainInstance.Activated += MainInstance_Activated;
        var config = await new ConfigService().LoadAsync();
        Logger.SetDebugEnabled(config.DebugLogging);
        Logger.Info($"Starting WinUI manager v{AppInfo.Version}");
        MainWindow = new MainWindow();
        MainWindow.Activate();
        if (Environment.GetCommandLineArgs().Any(static argument =>
                string.Equals(argument, "--hidden", StringComparison.OrdinalIgnoreCase)))
        {
            MainWindow.HideToTray();
        }
    }

    private void MainInstance_Activated(object? sender, AppActivationArguments args)
    {
        MainWindow?.DispatcherQueue.TryEnqueue(MainWindow.ShowFromTray);
    }
}
