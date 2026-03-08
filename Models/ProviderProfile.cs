namespace CodexHomeManager.Models;

public sealed class ProviderProfile
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string DirectoryPath { get; init; } = string.Empty;

    public string ModelProvider { get; init; } = string.Empty;

    public int Revision { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
