namespace CodexHomeManager.Models;

public sealed class RuntimeSyncResult
{
    public string SharedStoreHome { get; init; } = string.Empty;

    public string RuntimeHome { get; init; } = string.Empty;

    public string EffectiveProvider { get; init; } = string.Empty;

    public int SessionCount { get; init; }

    public string LastImportedSessionId { get; init; } = string.Empty;
}