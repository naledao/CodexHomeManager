namespace CodexHomeManager.Models;

public sealed class ManagedProfileContent
{
    public long AccountId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string ModelProvider { get; init; } = string.Empty;

    public string AuthJson { get; init; } = string.Empty;

    public string ConfigToml { get; init; } = string.Empty;

    public int Revision { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
