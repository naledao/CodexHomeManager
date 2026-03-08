namespace CodexHomeManager.Models;

public sealed class AppPathSettings
{
    public string StateHome { get; set; } = string.Empty;

    public string AuthHome { get; set; } = string.Empty;

    public string ProfilesRoot { get; set; } = string.Empty;

    public string SelectedProfile { get; set; } = string.Empty;

    public string DefaultLaunchProfile { get; set; } = string.Empty;

    public Dictionary<string, string> SharedStoreDefaultLaunchProfiles { get; set; } = new();

    public string SharedStoreHome { get; set; } = string.Empty;

    public string TargetHome { get; set; } = string.Empty;

    public string AppExePath { get; set; } = string.Empty;

    public bool AutoSyncConfigChanges { get; set; } = true;
}
