using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace CodexHomeManager.Services;

public sealed partial class CodexManager
{
    public void RepairTargetHome(string targetHome, string? copiedFromHome)
    {
        ValidateHomeExists(targetHome, nameof(targetHome));

        var preferredModelProvider = ResolvePreferredModelProvider(targetHome);
        NormalizeWorkspaceRoots(targetHome);
        NormalizeSessionFiles(Path.Combine(targetHome, "sessions"), preferredModelProvider);
        RepairThreadRows(targetHome, copiedFromHome, preferredModelProvider);
    }

    private static void NormalizeWorkspaceRoots(string targetHome)
    {
        var path = Path.Combine(targetHome, ".codex-global-state.json");
        if (!File.Exists(path))
        {
            return;
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))?.AsObject() ?? new JsonObject();
        }
        catch
        {
            return;
        }

        NormalizeWorkspaceRootArray(root, "electron-saved-workspace-roots");
        NormalizeWorkspaceRootArray(root, "active-workspace-roots");

        if (root["thread-workspace-root-hints"] is JsonObject hints)
        {
            foreach (var key in hints.Select(pair => pair.Key).ToList())
            {
                var value = hints[key]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                hints[key] = NormalizeWorkspaceRoot(value);
            }
        }

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static void NormalizeWorkspaceRootArray(JsonObject root, string propertyName)
    {
        if (root[propertyName] is not JsonArray array)
        {
            return;
        }

        var normalized = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in array)
        {
            var value = node?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var normalizedValue = NormalizeWorkspaceRoot(value);
            if (seen.Add(normalizedValue))
            {
                normalized.Add(normalizedValue);
            }
        }

        root[propertyName] = normalized;
    }

    private static void NormalizeSessionFiles(string sessionsRoot, string? preferredModelProvider)
    {
        if (!Directory.Exists(sessionsRoot))
        {
            return;
        }

        foreach (var sessionPath in Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            NormalizeSessionFileMetadata(sessionPath, preferredModelProvider);
        }
    }

    private static void NormalizeSessionFileMetadata(string sessionPath, string? preferredModelProvider)
    {
        if (!File.Exists(sessionPath))
        {
            return;
        }

        var tempPath = sessionPath + ".tmp";

        using (var input = new StreamReader(sessionPath, Encoding.UTF8))
        {
            var firstLine = input.ReadLine();
            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return;
            }

            JsonObject? root;
            try
            {
                root = JsonNode.Parse(firstLine)?.AsObject();
            }
            catch
            {
                return;
            }

            if (root?["type"]?.GetValue<string>() is not "session_meta"
                || root["payload"] is not JsonObject payload)
            {
                return;
            }

            var changed = false;

            var originalProvider = payload["model_provider"]?.GetValue<string>();
            var normalizedProvider = CoerceModelProvider(originalProvider, preferredModelProvider);
            if (!string.IsNullOrWhiteSpace(normalizedProvider)
                && !string.Equals(originalProvider, normalizedProvider, StringComparison.Ordinal))
            {
                payload["model_provider"] = normalizedProvider;
                changed = true;
            }

            var originalCwd = payload["cwd"]?.GetValue<string>();
            var normalizedCwd = NormalizeWorkspaceRoot(originalCwd);
            if (!string.IsNullOrWhiteSpace(originalCwd)
                && !string.Equals(originalCwd, normalizedCwd, StringComparison.Ordinal))
            {
                payload["cwd"] = normalizedCwd;
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            using var output = new StreamWriter(tempPath, append: false, new UTF8Encoding(false));
            output.WriteLine(root.ToJsonString());

            var buffer = new char[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
            }
        }

        File.Replace(tempPath, sessionPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
    }

    private static void RepairThreadRows(string targetHome, string? copiedFromHome, string? preferredModelProvider)
    {
        var dbPath = Path.Combine(targetHome, "state_5.sqlite");
        if (!File.Exists(dbPath))
        {
            return;
        }

        var sourceSessionsRoot = string.IsNullOrWhiteSpace(copiedFromHome)
            ? null
            : Path.Combine(copiedFromHome, "sessions");
        var targetSessionsRoot = Path.Combine(targetHome, "sessions");

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        EnsureThreadsTable(connection);

        var updates = new List<(string Id, string RolloutPath, string ModelProvider, string Cwd)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT id, rollout_path, model_provider, cwd FROM threads";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var rolloutPath = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var modelProvider = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                var cwd = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                var repairedPath = RewriteRolloutPath(rolloutPath, sourceSessionsRoot, targetSessionsRoot);
                var repairedProvider = CoerceModelProvider(modelProvider, preferredModelProvider);
                if (string.IsNullOrWhiteSpace(repairedProvider))
                {
                    repairedProvider = modelProvider;
                }

                var repairedCwd = NormalizeWorkspaceRoot(cwd);

                if (!string.Equals(rolloutPath, repairedPath, StringComparison.Ordinal)
                    || !string.Equals(modelProvider, repairedProvider, StringComparison.Ordinal)
                    || !string.Equals(cwd, repairedCwd, StringComparison.Ordinal))
                {
                    updates.Add((id, repairedPath, repairedProvider, repairedCwd));
                }
            }
        }

        foreach (var update in updates)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE threads
SET rollout_path = $rolloutPath,
    model_provider = $modelProvider,
    cwd = $cwd
WHERE id = $id;";
            command.Parameters.AddWithValue("$id", update.Id);
            command.Parameters.AddWithValue("$rolloutPath", update.RolloutPath);
            command.Parameters.AddWithValue("$modelProvider", update.ModelProvider);
            command.Parameters.AddWithValue("$cwd", update.Cwd);
            command.ExecuteNonQuery();
        }
    }

    private static string RewriteRolloutPath(string rolloutPath, string? sourceSessionsRoot, string targetSessionsRoot)
    {
        if (string.IsNullOrWhiteSpace(rolloutPath))
        {
            return rolloutPath;
        }

        var normalizedPath = StripVerbatimPathPrefix(rolloutPath);
        var normalizedTargetRoot = Path.GetFullPath(targetSessionsRoot);
        if (normalizedPath.StartsWith(normalizedTargetRoot, StringComparison.OrdinalIgnoreCase))
        {
            return ToVerbatimPath(normalizedPath);
        }

        if (string.IsNullOrWhiteSpace(sourceSessionsRoot))
        {
            return rolloutPath;
        }

        var normalizedSourceRoot = Path.GetFullPath(sourceSessionsRoot);
        if (!normalizedPath.StartsWith(normalizedSourceRoot, StringComparison.OrdinalIgnoreCase))
        {
            return rolloutPath;
        }

        var relativePath = Path.GetRelativePath(normalizedSourceRoot, normalizedPath);
        return ToVerbatimPath(Path.Combine(normalizedTargetRoot, relativePath));
    }

    private static string ResolvePreferredModelProvider(string codexHome)
    {
        var configuredProvider = TryReadConfiguredModelProvider(codexHome);
        if (!string.IsNullOrWhiteSpace(configuredProvider))
        {
            return configuredProvider;
        }

        return InferProviderFromAuth(codexHome);
    }

    private static string TryReadConfiguredModelProvider(string codexHome)
    {
        var configPath = Path.Combine(codexHome, "config.toml");
        if (!File.Exists(configPath))
        {
            return string.Empty;
        }

        try
        {
            var configuredProvider = TryReadTopLevelTomlStringValue(configPath, "model_provider");
            return NormalizeModelProvider(configuredProvider);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string InferProviderFromAuth(string codexHome)
    {
        var authPath = Path.Combine(codexHome, "auth.json");
        if (!File.Exists(authPath))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(authPath, Encoding.UTF8));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("OPENAI_API_KEY", out var apiKeyElement)
                && !string.IsNullOrWhiteSpace(apiKeyElement.GetString()))
            {
                return "openai";
            }
        }
        catch
        {
            // Best effort detection.
        }

        return string.Empty;
    }

    private static string? TryReadTopLevelTomlStringValue(string configPath, string key)
    {
        var inSection = false;
        foreach (var rawLine in File.ReadLines(configPath, Encoding.UTF8))
        {
            var line = StripTomlComment(rawLine).Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                inSection = true;
                continue;
            }

            if (inSection)
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var currentKey = line[..separatorIndex].Trim();
            if (!string.Equals(currentKey, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawValue = line[(separatorIndex + 1)..].Trim();
            return UnquoteTomlString(rawValue);
        }

        return null;
    }

    private static string StripTomlComment(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        var inSingleQuote = false;
        var inDoubleQuote = false;
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            if (current == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (current == '"' && !inSingleQuote)
            {
                var escaped = index > 0 && line[index - 1] == '\\';
                if (!escaped)
                {
                    inDoubleQuote = !inDoubleQuote;
                }
            }
            else if (current == '#' && !inSingleQuote && !inDoubleQuote)
            {
                return line[..index];
            }
        }

        return line;
    }

    private static string UnquoteTomlString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.Length >= 2)
        {
            if ((trimmed[0] == '"' && trimmed[^1] == '"')
                || (trimmed[0] == '\'' && trimmed[^1] == '\''))
            {
                return trimmed[1..^1];
            }
        }

        return trimmed;
    }

    private static string CoerceModelProvider(string? sourceProvider, string? preferredModelProvider)
    {
        if (!string.IsNullOrWhiteSpace(preferredModelProvider))
        {
            return NormalizeModelProvider(preferredModelProvider);
        }

        return NormalizeModelProvider(sourceProvider);
    }

    private static string NormalizeModelProvider(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }

    private static string NormalizeWorkspaceRoot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (IsLocalWindowsPath(value))
        {
            return ToVerbatimPath(value);
        }

        return value;
    }

    private static bool IsLocalWindowsPath(string value)
    {
        var path = StripVerbatimPathPrefix(value);
        return path.Length >= 3
            && char.IsLetter(path[0])
            && path[1] == ':'
            && (path[2] == '\\' || path[2] == '/');
    }

    private static string StripVerbatimPathPrefix(string value)
    {
        return value.StartsWith("\\\\?\\", StringComparison.Ordinal)
            ? value[4..]
            : value;
    }

    private static string ToVerbatimPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var fullPath = Path.GetFullPath(StripVerbatimPathPrefix(path));
        if (fullPath.Length >= 2 && char.IsLetter(fullPath[0]) && fullPath[1] == ':')
        {
            fullPath = char.ToUpperInvariant(fullPath[0]) + fullPath[1..];
        }

        return fullPath.StartsWith("\\\\?\\", StringComparison.Ordinal)
            ? fullPath
            : $"\\\\?\\{fullPath}";
    }
}
