using System.Windows.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BeszelAgentManager.WinUI.Services;

internal sealed class TrayIconService : IDisposable
{
    private readonly TaskbarIcon _taskbarIcon;

    public TrayIconService(
        Action open,
        Func<Task> startService,
        Func<Task> stopService,
        Func<Task> restartService,
        Func<Task> updateAgent,
        Action exit)
    {
        var openCommand = new TrayCommand(open);
        var startCommand = new TrayCommand(startService);
        var stopCommand = new TrayCommand(stopService);
        var restartCommand = new TrayCommand(restartService);
        var updateCommand = new TrayCommand(updateAgent);
        var exitCommand = new TrayCommand(exit);
        var menu = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };
        var openItem = new MenuFlyoutItem
        {
            Text = "Open BeszelAgentManager",
            Width = 220,
            Command = openCommand,
            Icon = new SymbolIcon(Symbol.OpenFile),
        };
        var startItem = new MenuFlyoutItem { Text = "Start service", Command = startCommand, Icon = new SymbolIcon(Symbol.Play) };
        var stopItem = new MenuFlyoutItem { Text = "Stop service", Command = stopCommand, Icon = new SymbolIcon(Symbol.Stop) };
        var restartItem = new MenuFlyoutItem { Text = "Restart service", Command = restartCommand, Icon = new SymbolIcon(Symbol.Refresh) };
        var updateItem = new MenuFlyoutItem { Text = "Update agent", Command = updateCommand, Icon = new SymbolIcon(Symbol.Download) };
        var exitItem = new MenuFlyoutItem
        {
            Text = "Exit",
            Command = exitCommand,
            Icon = new SymbolIcon(Symbol.Cancel),
        };
        menu.Items.Add(openItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(startItem);
        menu.Items.Add(stopItem);
        menu.Items.Add(restartItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(updateItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exitItem);

        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = "BeszelAgentManager",
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.ico")),
            ContextFlyout = menu,
            ContextMenuMode = ContextMenuMode.PopupMenu,
            LeftClickCommand = new TrayCommand(open),
            NoLeftClickDelay = true,
            Visibility = Visibility.Visible,
        };
        _taskbarIcon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void ShowNotification(string title, string message, NotificationIcon icon = NotificationIcon.Info)
    {
        _taskbarIcon.ShowNotification(title, message, icon);
    }

    public void Dispose()
    {
        _taskbarIcon.Dispose();
    }
}

internal sealed class TrayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private bool _running;

    public TrayCommand(Action execute)
        : this(() =>
        {
            execute();
            return Task.CompletedTask;
        })
    {
    }

    public TrayCommand(Func<Task> execute)
    {
        _execute = execute;
    }

    public event EventHandler? CanExecuteChanged
;

    public bool CanExecute(object? parameter) => !_running;

    public async void Execute(object? parameter)
    {
        if (_running)
        {
            return;
        }

        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute();
        }
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
