namespace BeszelAgentManager.Core;

public static class ServiceProcessEnvironment
{
    // NSSM AppEnvironment replaces the inherited block; AppEnvironmentExtra overlays it.
    public static Dictionary<string, string> Compose(
        IEnumerable<KeyValuePair<string, string?>> inherited,
        IReadOnlyList<string>? replacement,
        IReadOnlyList<string>? extra)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (replacement is not { Count: > 0 })
        {
            foreach (var item in inherited)
            {
                if (item.Value is not null)
                {
                    result[item.Key] = item.Value;
                }
            }
        }
        else
        {
            Overlay(replacement);
        }

        Overlay(extra);
        return result;

        void Overlay(IReadOnlyList<string>? entries)
        {
            if (entries is null)
            {
                return;
            }

            foreach (var entry in entries)
            {
                var separator = entry.IndexOf('=');
                if (separator > 0)
                {
                    result[entry[..separator]] = entry[(separator + 1)..];
                }
            }
        }
    }
}
