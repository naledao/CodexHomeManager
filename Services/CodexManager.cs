using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodexHomeManager.Models;
using Microsoft.Data.Sqlite;

namespace CodexHomeManager.Services;

public sealed partial class CodexManager
{
    private static readonly string[] StatePaths =
    [
        "sessions",
        "history.jsonl",
        "session_index.jsonl",
        ".codex-global-state.json",
        "state_5.sqlite",
        "state_5.sqlite-shm",
        "state_5.sqlite-wal"
    ];

    private static readonly Regex SessionIdRegex = new(
        @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string DefaultCodexHome => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex");

    public string? FindCodexAppExecutable()
    {
        var running = Process.GetProcessesByName("Codex")
            .Select(process =>
            {
                try
                {
                    return process.MainModule?.FileName;
                }
                catch
                {
                    return null;
                }
            })
            .FirstOrDefault(path => path is not null && path.Contains("OpenAI.Codex", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(running))
        {
            return running;
        }

        var windowsApps = @"C:\Program Files\WindowsApps";
        if (!Directory.Exists(windowsApps))
        {
            return null;
        }

        return Directory.GetDirectories(windowsApps, "OpenAI.Codex_*")
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => Path.Combine(path, "app", "Codex.exe"))
            .FirstOrDefault(File.Exists);
    }

    public bool IsCodexAppRunning()
    {
        return Process.GetProcessesByName("Codex")
            .Any(process =>
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    return path is not null && path.Contains("OpenAI.Codex", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            });
    }

    public int CloseRunningCodexApp()
    {
        var processes = Process.GetProcessesByName("Codex")
            .Where(process =>
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    return path is not null && path.Contains("OpenAI.Codex", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            })
            .ToList();

        foreach (var process in processes)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }

        return processes.Count;
    }

    public IReadOnlyList<SessionRecord> LoadSessions(string codexHome)
    {
        ValidateHomeExists(codexHome, nameof(codexHome));

        var sessionsRoot = Path.Combine(codexHome, "sessions");
        var indexById = LoadSessionIndex(codexHome);
        var dbById = LoadThreadRows(codexHome);
        var records = new Dictionary<string, SessionRecord>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(sessionsRoot))
        {
            foreach (var path in Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories))
            {
                var sessionId = ExtractSessionId(path);
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    continue;
                }

                records[sessionId] = BuildSessionRecord(path, sessionId, indexById, dbById);
            }
        }

        foreach (var pair in indexById)
        {
            if (!records.ContainsKey(pair.Key))
            {
                records[pair.Key] = new SessionRecord
                {
                    Id = pair.Key,
                    Title = pair.Value.Title,
                    UpdatedAt = pair.Value.UpdatedAt,
                    CreatedAt = pair.Value.UpdatedAt,
                    Source = "index"
                };
            }
        }

        foreach (var pair in dbById)
        {
            if (!records.TryGetValue(pair.Key, out var record))
            {
                record = new SessionRecord
                {
                    Id = pair.Key,
                    Source = "sqlite"
                };
                records[pair.Key] = record;
            }

            ApplyThreadRow(record, pair.Value);
        }

        return records.Values
            .OrderByDescending(record => record.UpdatedAt)
            .ThenBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void PrepareHybridHome(string stateFromHome, string authFromHome, string targetHome, bool overwrite)
    {
        ValidateHomeExists(stateFromHome, nameof(stateFromHome));
        ValidateHomeExists(authFromHome, nameof(authFromHome));

        if (Directory.Exists(targetHome))
        {
            if (!overwrite)
            {
                throw new InvalidOperationException($"Target home already exists: {targetHome}");
            }
        }
        else
        {
            Directory.CreateDirectory(targetHome);
        }

        foreach (var relativePath in StatePaths)
        {
            CopyPathIfExists(Path.Combine(stateFromHome, relativePath), Path.Combine(targetHome, relativePath));
        }

        CopyPathIfExists(Path.Combine(authFromHome, "auth.json"), Path.Combine(targetHome, "auth.json"));
        CopyPathIfExists(Path.Combine(authFromHome, "config.toml"), Path.Combine(targetHome, "config.toml"));
        RepairTargetHome(targetHome, stateFromHome);
    }

    public SessionRecord ImportSession(string sourceHome, string targetHome, string sessionId, bool refreshUpdatedAt, bool addWorkspaceHint)
    {
        ValidateHomeExists(sourceHome, nameof(sourceHome));
        ValidateHomeExists(targetHome, nameof(targetHome));

        var sessions = LoadSessions(sourceHome);
        var sourceSession = sessions.FirstOrDefault(item => item.Id.Equals(sessionId, StringComparison.OrdinalIgnoreCase));
        if (sourceSession is null || string.IsNullOrWhiteSpace(sourceSession.SessionPath) || !File.Exists(sourceSession.SessionPath))
        {
            throw new FileNotFoundException($"Session not found in source home: {sessionId}");
        }

        var sourceSessionsRoot = Path.Combine(sourceHome, "sessions");
        var relativePath = Path.GetRelativePath(sourceSessionsRoot, sourceSession.SessionPath);
        var targetSessionPath = Path.Combine(targetHome, "sessions", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetSessionPath)!);
        File.Copy(sourceSession.SessionPath, targetSessionPath, overwrite: true);
        NormalizeSessionFileMetadata(targetSessionPath, ResolvePreferredModelProvider(targetHome));

        UpsertSessionIndex(targetHome, sourceSession, refreshUpdatedAt);
        UpsertThreadRow(targetHome, sourceSession, targetSessionPath, refreshUpdatedAt);
        AppendHistoryLines(sourceHome, targetHome, sessionId);

        if (addWorkspaceHint && !string.IsNullOrWhiteSpace(sourceSession.Cwd))
        {
            AddWorkspaceRoot(targetHome, sourceSession.Cwd);
        }

        return LoadSessions(targetHome)
            .First(record => record.Id.Equals(sessionId, StringComparison.OrdinalIgnoreCase));
    }

    public void LaunchCodexApp(string codexHome, string? appExePath)
    {
        ValidateHomeExists(codexHome, nameof(codexHome));

        if (IsCodexAppRunning())
        {
            throw new InvalidOperationException("Codex app is already running. Close it first.");
        }

        var executable = appExePath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            executable = FindCodexAppExecutable();
        }

        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            throw new FileNotFoundException("Unable to locate Codex app executable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
        };
        startInfo.EnvironmentVariables["CODEX_HOME"] = codexHome;

        var apiKey = ReadOpenAiApiKey(codexHome);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            startInfo.EnvironmentVariables["OPENAI_API_KEY"] = apiKey;
        }

        Process.Start(startInfo);
    }

    public string? ReadOpenAiApiKey(string codexHome)
    {
        var authPath = Path.Combine(codexHome, "auth.json");
        if (!File.Exists(authPath))
        {
            return null;
        }

        using var stream = File.OpenRead(authPath);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("OPENAI_API_KEY", out var keyElement))
        {
            return null;
        }

        return keyElement.GetString();
    }

    private static void ValidateHomeExists(string path, string paramName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"{paramName} does not exist: {path}");
        }
    }

    private static string? ExtractSessionId(string path)
    {
        var fileName = Path.GetFileName(path);
        return SessionIdRegex.Match(fileName).Value;
    }

    private static void CopyPathIfExists(string source, string target)
    {
        if (Directory.Exists(source))
        {
            CopyDirectory(source, target);
            return;
        }

        if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static Dictionary<string, (string Title, DateTimeOffset UpdatedAt)> LoadSessionIndex(string codexHome)
    {
        var path = Path.Combine(codexHome, "session_index.jsonl");
        var result = new Dictionary<string, (string Title, DateTimeOffset UpdatedAt)>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return result;
        }

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var id = root.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var title = root.TryGetProperty("thread_name", out var titleElement)
                    ? titleElement.GetString() ?? string.Empty
                    : string.Empty;
                var updatedAtText = root.TryGetProperty("updated_at", out var updatedElement)
                    ? updatedElement.GetString()
                    : null;
                var updatedAt = DateTimeOffset.TryParse(updatedAtText, out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;
                result[id] = (title, updatedAt);
            }
            catch
            {
                // Skip malformed JSONL entries.
            }
        }

        return result;
    }

    private static Dictionary<string, ThreadRow> LoadThreadRows(string codexHome)
    {
        var dbPath = Path.Combine(codexHome, "state_5.sqlite");
        var result = new Dictionary<string, ThreadRow>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(dbPath))
        {
            return result;
        }

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        EnsureThreadsTable(connection);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, cwd, source, model_provider, created_at, updated_at FROM threads";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            result[id] = new ThreadRow(
                id,
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.IsDBNull(5) ? 0L : reader.GetInt64(5),
                reader.IsDBNull(6) ? 0L : reader.GetInt64(6));
        }

        return result;
    }

    private static SessionRecord BuildSessionRecord(
        string sessionPath,
        string sessionId,
        IReadOnlyDictionary<string, (string Title, DateTimeOffset UpdatedAt)> indexById,
        IReadOnlyDictionary<string, ThreadRow> dbById)
    {
        var updatedAt = File.GetLastWriteTimeUtc(sessionPath);
        var createdAt = updatedAt;
        var title = string.Empty;
        var cwd = string.Empty;
        var modelProvider = string.Empty;

        if (indexById.TryGetValue(sessionId, out var indexEntry))
        {
            title = indexEntry.Title;
            if (indexEntry.UpdatedAt != DateTimeOffset.MinValue)
            {
                updatedAt = indexEntry.UpdatedAt.UtcDateTime;
            }
        }

        if (dbById.TryGetValue(sessionId, out var row))
        {
            title = string.IsNullOrWhiteSpace(row.Title) ? title : row.Title;
            cwd = NormalizeCwd(row.Cwd);
            modelProvider = row.ModelProvider;
            if (row.CreatedAt > 0)
            {
                createdAt = DateTimeOffset.FromUnixTimeSeconds(row.CreatedAt).UtcDateTime;
            }
            if (row.UpdatedAt > 0)
            {
                updatedAt = DateTimeOffset.FromUnixTimeSeconds(row.UpdatedAt).UtcDateTime;
            }
        }

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(cwd) || string.IsNullOrWhiteSpace(modelProvider))
        {
            TryReadSessionMeta(sessionPath, ref title, ref cwd, ref modelProvider, ref createdAt);
        }

        title = string.IsNullOrWhiteSpace(title) ? sessionId : title;

        return new SessionRecord
        {
            Id = sessionId,
            Title = title,
            Cwd = cwd,
            SessionPath = sessionPath,
            UpdatedAt = new DateTimeOffset(updatedAt, TimeSpan.Zero),
            CreatedAt = new DateTimeOffset(createdAt, TimeSpan.Zero),
            Source = "session",
            ModelProvider = modelProvider
        };
    }

    private static void TryReadSessionMeta(string sessionPath, ref string title, ref string cwd, ref string modelProvider, ref DateTime createdAt)
    {
        try
        {
            using var reader = new StreamReader(sessionPath, Encoding.UTF8);
            var firstLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return;
            }

            using var document = JsonDocument.Parse(firstLine);
            var payload = document.RootElement.GetProperty("payload");
            if (string.IsNullOrWhiteSpace(cwd) && payload.TryGetProperty("cwd", out var cwdElement))
            {
                cwd = NormalizeCwd(cwdElement.GetString() ?? string.Empty);
            }
            if (string.IsNullOrWhiteSpace(modelProvider) && payload.TryGetProperty("model_provider", out var modelProviderElement))
            {
                modelProvider = NormalizeModelProvider(modelProviderElement.GetString());
            }
            if (payload.TryGetProperty("timestamp", out var timestampElement)
                && DateTimeOffset.TryParse(timestampElement.GetString(), out var createdAtOffset))
            {
                createdAt = createdAtOffset.UtcDateTime;
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var row = JsonDocument.Parse(line);
                var root = row.RootElement;
                if (!root.TryGetProperty("type", out var typeElement))
                {
                    continue;
                }

                if (!string.Equals(typeElement.GetString(), "response_item", StringComparison.Ordinal))
                {
                    continue;
                }

                var payloadElement = root.GetProperty("payload");
                if (!payloadElement.TryGetProperty("role", out var roleElement)
                    || !string.Equals(roleElement.GetString(), "user", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!payloadElement.TryGetProperty("content", out var contentElement)
                    || contentElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in contentElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("type", out var itemTypeElement)
                        || !string.Equals(itemTypeElement.GetString(), "input_text", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var text = item.GetProperty("text").GetString() ?? string.Empty;
                    if (text.StartsWith("<", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    title = text.Replace(Environment.NewLine, " ").Trim();
                    if (title.Length > 120)
                    {
                        title = title[..120];
                    }
                    return;
                }
            }
        }
        catch
        {
            // Best effort metadata extraction.
        }
    }

    private static string NormalizeCwd(string value)
    {
        return value.StartsWith("\\\\?\\", StringComparison.Ordinal)
            ? value[4..]
            : value;
    }

    private static void ApplyThreadRow(SessionRecord record, ThreadRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.Title))
        {
            record.Title = row.Title;
        }
        if (!string.IsNullOrWhiteSpace(row.Cwd))
        {
            record.Cwd = NormalizeCwd(row.Cwd);
        }
        if (row.CreatedAt > 0)
        {
            record.CreatedAt = DateTimeOffset.FromUnixTimeSeconds(row.CreatedAt);
        }
        if (row.UpdatedAt > 0)
        {
            record.UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(row.UpdatedAt);
        }
        if (!string.IsNullOrWhiteSpace(row.Source))
        {
            record.Source = row.Source;
        }
        if (!string.IsNullOrWhiteSpace(row.ModelProvider))
        {
            record.ModelProvider = row.ModelProvider;
        }
    }

    private static void UpsertSessionIndex(string targetHome, SessionRecord session, bool refreshUpdatedAt)
    {
        var indexPath = Path.Combine(targetHome, "session_index.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath) ?? targetHome);

        var updatedAt = refreshUpdatedAt ? DateTimeOffset.UtcNow : session.UpdatedAt;
        var entry = new JsonObject
        {
            ["id"] = session.Id,
            ["thread_name"] = session.Title,
            ["updated_at"] = updatedAt.ToString("O")
        }.ToJsonString();

        var rows = new List<string>();
        var replaced = false;
        if (File.Exists(indexPath))
        {
            foreach (var line in File.ReadLines(indexPath, Encoding.UTF8))
            {
                if (line.Contains(session.Id, StringComparison.OrdinalIgnoreCase))
                {
                    rows.Add(entry);
                    replaced = true;
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    rows.Add(line);
                }
            }
        }

        if (!replaced)
        {
            rows.Add(entry);
        }

        File.WriteAllLines(indexPath, rows, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void UpsertThreadRow(string targetHome, SessionRecord session, string targetSessionPath, bool refreshUpdatedAt)
    {
        var dbPath = Path.Combine(targetHome, "state_5.sqlite");
        Directory.CreateDirectory(targetHome);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        EnsureThreadsTable(connection);

        var updatedAt = refreshUpdatedAt ? DateTimeOffset.UtcNow : session.UpdatedAt;
        var createdAt = session.CreatedAt == default ? updatedAt : session.CreatedAt;

        var modelProvider = CoerceModelProvider(session.ModelProvider, ResolvePreferredModelProvider(targetHome));
        if (string.IsNullOrWhiteSpace(modelProvider))
        {
            modelProvider = "openai";
        }

        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO threads (
    id, rollout_path, created_at, updated_at, source, model_provider, cwd, title,
    sandbox_policy, approval_mode, tokens_used, has_user_event, archived,
    cli_version, first_user_message, memory_mode
) VALUES (
    $id, $rolloutPath, $createdAt, $updatedAt, $source, $modelProvider, $cwd, $title,
    $sandboxPolicy, $approvalMode, 0, 1, 0,
    $cliVersion, $firstUserMessage, 'enabled'
)
ON CONFLICT(id) DO UPDATE SET
    rollout_path = excluded.rollout_path,
    updated_at = excluded.updated_at,
    source = excluded.source,
    model_provider = excluded.model_provider,
    cwd = excluded.cwd,
    title = excluded.title,
    sandbox_policy = excluded.sandbox_policy,
    approval_mode = excluded.approval_mode,
    has_user_event = excluded.has_user_event,
    first_user_message = excluded.first_user_message,
    cli_version = excluded.cli_version;";
        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$rolloutPath", ToVerbatimPath(targetSessionPath));
        command.Parameters.AddWithValue("$createdAt", createdAt.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$updatedAt", updatedAt.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$source", string.IsNullOrWhiteSpace(session.Source) ? "cli" : session.Source);
        command.Parameters.AddWithValue("$modelProvider", modelProvider);
        command.Parameters.AddWithValue("$cwd", session.Cwd);
        command.Parameters.AddWithValue("$title", session.Title);
        command.Parameters.AddWithValue("$sandboxPolicy", "{\"type\":\"danger-full-access\"}");
        command.Parameters.AddWithValue("$approvalMode", "never");
        command.Parameters.AddWithValue("$cliVersion", "imported-by-csharp-client");
        command.Parameters.AddWithValue("$firstUserMessage", session.Title);
        command.ExecuteNonQuery();
    }

    private static void EnsureThreadsTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS threads (
    id TEXT PRIMARY KEY,
    rollout_path TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    source TEXT NOT NULL,
    model_provider TEXT NOT NULL,
    cwd TEXT NOT NULL,
    title TEXT NOT NULL,
    sandbox_policy TEXT NOT NULL,
    approval_mode TEXT NOT NULL,
    tokens_used INTEGER NOT NULL DEFAULT 0,
    has_user_event INTEGER NOT NULL DEFAULT 0,
    archived INTEGER NOT NULL DEFAULT 0,
    archived_at INTEGER,
    git_sha TEXT,
    git_branch TEXT,
    git_origin_url TEXT,
    cli_version TEXT NOT NULL DEFAULT '',
    first_user_message TEXT NOT NULL DEFAULT '',
    agent_nickname TEXT,
    agent_role TEXT,
    memory_mode TEXT NOT NULL DEFAULT 'enabled'
);
CREATE INDEX IF NOT EXISTS idx_threads_created_at ON threads(created_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS idx_threads_updated_at ON threads(updated_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS idx_threads_archived ON threads(archived);
CREATE INDEX IF NOT EXISTS idx_threads_source ON threads(source);
CREATE INDEX IF NOT EXISTS idx_threads_provider ON threads(model_provider);";
        command.ExecuteNonQuery();
    }

    private static void AppendHistoryLines(string sourceHome, string targetHome, string sessionId)
    {
        var sourcePath = Path.Combine(sourceHome, "history.jsonl");
        if (!File.Exists(sourcePath))
        {
            return;
        }

        var matchingLines = File.ReadLines(sourcePath, Encoding.UTF8)
            .Where(line => line.Contains(sessionId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (matchingLines.Count == 0)
        {
            return;
        }

        var targetPath = Path.Combine(targetHome, "history.jsonl");
        var existing = File.Exists(targetPath)
            ? new HashSet<string>(File.ReadLines(targetPath, Encoding.UTF8), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        using var stream = new StreamWriter(targetPath, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        foreach (var line in matchingLines)
        {
            if (existing.Add(line))
            {
                stream.WriteLine(line);
            }
        }
    }

    private static void AddWorkspaceRoot(string targetHome, string workspaceRoot)
    {
        var normalizedWorkspaceRoot = NormalizeWorkspaceRoot(workspaceRoot);
        if (string.IsNullOrWhiteSpace(normalizedWorkspaceRoot))
        {
            return;
        }

        var path = Path.Combine(targetHome, ".codex-global-state.json");
        JsonObject root;
        if (File.Exists(path))
        {
            root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))?.AsObject() ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        EnsureArrayContains(root, "electron-saved-workspace-roots", normalizedWorkspaceRoot);
        EnsureArrayContains(root, "active-workspace-roots", normalizedWorkspaceRoot);

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static void EnsureArrayContains(JsonObject root, string propertyName, string value)
    {
        if (root[propertyName] is not JsonArray array)
        {
            array = [];
            root[propertyName] = array;
        }

        if (array.Any(node => string.Equals(node?.GetValue<string>(), value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        array.Add(value);
    }

    private sealed record ThreadRow(
        string Id,
        string Title,
        string Cwd,
        string Source,
        string ModelProvider,
        long CreatedAt,
        long UpdatedAt);
}
