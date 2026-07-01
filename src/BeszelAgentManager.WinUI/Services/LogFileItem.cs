namespace BeszelAgentManager.WinUI.Services;

internal sealed record LogFileItem(string Label, string Path)
{
    public override string ToString() => Label;
}
