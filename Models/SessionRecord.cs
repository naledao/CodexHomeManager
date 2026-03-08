namespace CodexHomeManager.Models;

public sealed class SessionRecord
{
    public string Id { get; init; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Cwd { get; set; } = string.Empty;

    public string SessionPath { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string Source { get; set; } = string.Empty;

    public string ModelProvider { get; set; } = string.Empty;
}
