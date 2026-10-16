using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace BeszelAgentManager.WinUI.Services;

internal static class Localization
{
    private static readonly ResourceManager Manager = new();
    private static ResourceMap _resources = null!;
    private static ResourceContext _context = null!;

    public static string Language { get; private set; } = "en-US";

    public static void Initialize(string language)
    {
        Language = language == "zh-Hans" ? "zh-Hans" : "en-US";
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(Language);
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture;
        // An explicit MRT context also supports the unpackaged installer edition.
        _context = Manager.CreateResourceContext();
        _context.QualifierValues["Language"] = Language;
        _resources = Manager.MainResourceMap.GetSubtree("Resources");
    }

    public static string Get(string key) => _resources.GetValue(key, _context).ValueAsString;

    public static string Get(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    // Service and version identifiers remain unchanged outside the display layer.
    public static string Status(string value) => Language == "en-US" ? value : value.ToUpperInvariant() switch
    {
        "RUNNING" => Get("StateRunning"),
        "STOPPED" => Get("StateStopped"),
        "START_PENDING" => Get("StateStarting"),
        "STOP_PENDING" => Get("StateStopping"),
        "CONTINUE_PENDING" => Get("StateContinuing"),
        "PAUSE_PENDING" => Get("StatePausing"),
        "PAUSED" => Get("StatePaused"),
        "UNKNOWN" => Get("StateUnknown"),
        "NOT INSTALLED" => Get("StateNotInstalled"),
        _ => value,
    };

    public static string LogFilter(string value) => Language == "en-US" ? value.ToLowerInvariant() : value.ToLowerInvariant() switch
    {
        "all" => Get("All"),
        "info" => Get("Info"),
        "warning" => Get("Warning"),
        "warnings" => Get("Warnings"),
        "error" => Get("Error"),
        "errors" => Get("Errors"),
        "debug" => Get("Debug"),
        "task" => Get("Task"),
        _ => value,
    };
}
