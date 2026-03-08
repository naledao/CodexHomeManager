using System.Text;
using System.Text.Json;
using CodexHomeManager.Models;

namespace CodexHomeManager.Services;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public AppSettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexHomeManager",
            "ui-settings.json"))
    {
    }

    internal AppSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public AppPathSettings? Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_settingsPath, Encoding.UTF8);
            return JsonSerializer.Deserialize<AppPathSettings>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Save(AppPathSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_settingsPath, json, new UTF8Encoding(false));
        }
        catch
        {
            // Best effort persistence. The UI should keep working even if saving fails.
        }
    }
}