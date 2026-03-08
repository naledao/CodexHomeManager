using System.Text;
using System.Text.Json.Nodes;
using CodexHomeManager.Models;

namespace CodexHomeManager.Services;

public sealed partial class CodexManager
{
    public string DefaultSharedStoreHome => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex-shared-store");

    public string GetEffectiveModelProvider(string codexHome)
    {
        if (string.IsNullOrWhiteSpace(codexHome) || !Directory.Exists(codexHome))
        {
            return string.Empty;
        }

        return ResolvePreferredModelProvider(codexHome);
    }

    public void PrepareSharedWorkspace(string? authFromHome, string sharedStoreHome, string runtimeHome, bool overwriteRuntimeConfig)
    {
        EnsureProjectionHomes(sharedStoreHome, runtimeHome);
        EnsureSharedStoreHome(sharedStoreHome);
        Directory.CreateDirectory(runtimeHome);
        EnsureRuntimeConfigurationFiles(authFromHome, runtimeHome, overwriteRuntimeConfig);
    }

    public SessionRecord ImportSessionToSharedStoreOnly(
        string sourceHome,
        string sharedStoreHome,
        string sessionId,
        bool refreshUpdatedAt,
        bool addWorkspaceHint)
    {
        EnsureSharedStoreHome(sharedStoreHome);

        var imported = ImportSession(sourceHome, sharedStoreHome, sessionId, refreshUpdatedAt, addWorkspaceHint);
        UpsertSharedCatalog(sharedStoreHome, imported, sourceHome);
        return imported;
    }

    public RuntimeSyncResult ImportSessionToSharedStore(
        string sourceHome,
        string sharedStoreHome,
        string? authFromHome,
        string runtimeHome,
        string sessionId,
        bool refreshUpdatedAt,
        bool addWorkspaceHint)
    {
        EnsureProjectionHomes(sharedStoreHome, runtimeHome);

        var imported = ImportSessionToSharedStoreOnly(
            sourceHome,
            sharedStoreHome,
            sessionId,
            refreshUpdatedAt,
            addWorkspaceHint);

        var syncResult = SyncRuntimeHome(sharedStoreHome, authFromHome, runtimeHome, overwriteRuntimeConfig: true);
        return new RuntimeSyncResult
        {
            SharedStoreHome = syncResult.SharedStoreHome,
            RuntimeHome = syncResult.RuntimeHome,
            EffectiveProvider = syncResult.EffectiveProvider,
            SessionCount = syncResult.SessionCount,
            LastImportedSessionId = imported.Id
        };
    }

    public RuntimeSyncResult SyncRuntimeHome(string sharedStoreHome, string? authFromHome, string runtimeHome, bool overwriteRuntimeConfig = false)
    {
        EnsureProjectionHomes(sharedStoreHome, runtimeHome);
        EnsureSharedStoreHome(sharedStoreHome);
        Directory.CreateDirectory(runtimeHome);
        EnsureRuntimeConfigurationFiles(authFromHome, runtimeHome, overwriteRuntimeConfig);

        if (IsCodexAppRunning())
        {
            throw new InvalidOperationException("Codex app is running. Close it before syncing the runtime home.");
        }

        ResetManagedState(runtimeHome);
        foreach (var relativePath in StatePaths)
        {
            CopyPathIfExists(Path.Combine(sharedStoreHome, relativePath), Path.Combine(runtimeHome, relativePath));
        }

        RepairTargetHome(runtimeHome, sharedStoreHome);
        var sessions = LoadSessions(runtimeHome);
        return new RuntimeSyncResult
        {
            SharedStoreHome = sharedStoreHome,
            RuntimeHome = runtimeHome,
            EffectiveProvider = ResolvePreferredModelProvider(runtimeHome),
            SessionCount = sessions.Count
        };
    }

    public RuntimeSyncResult SyncAndLaunchCodexApp(string sharedStoreHome, string? authFromHome, string runtimeHome, string? appExePath)
    {
        var syncResult = SyncRuntimeHome(sharedStoreHome, authFromHome, runtimeHome, overwriteRuntimeConfig: true);
        LaunchCodexApp(runtimeHome, appExePath);
        return syncResult;
    }

    private static void EnsureSharedStoreHome(string sharedStoreHome)
    {
        if (string.IsNullOrWhiteSpace(sharedStoreHome))
        {
            throw new ArgumentException("Shared store home cannot be empty.", nameof(sharedStoreHome));
        }

        Directory.CreateDirectory(sharedStoreHome);
    }

    private static void EnsureProjectionHomes(string sharedStoreHome, string runtimeHome)
    {
        if (string.IsNullOrWhiteSpace(runtimeHome))
        {
            throw new ArgumentException("Runtime home cannot be empty.", nameof(runtimeHome));
        }

        if (AreSamePath(sharedStoreHome, runtimeHome))
        {
            throw new InvalidOperationException("Shared store home and runtime home must be different directories.");
        }
    }

    private static bool AreSamePath(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var normalizedLeft = Path.GetFullPath(StripVerbatimPathPrefix(left)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRight = Path.GetFullPath(StripVerbatimPathPrefix(right)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

        private static void EnsureRuntimeConfigurationFiles(string? authFromHome, string runtimeHome, bool overwriteExisting)
    {
        if (string.IsNullOrWhiteSpace(authFromHome) || !Directory.Exists(authFromHome))
        {
            return;
        }

        SyncRuntimeConfigFile(authFromHome, runtimeHome, "auth.json", overwriteExisting);
        SyncRuntimeConfigFile(authFromHome, runtimeHome, "config.toml", overwriteExisting);
    }

    private static void SyncRuntimeConfigFile(string sourceHome, string runtimeHome, string fileName, bool overwriteExisting)
    {
        var source = Path.Combine(sourceHome, fileName);
        var target = Path.Combine(runtimeHome, fileName);
        if (AreSamePath(source, target))
        {
            return;
        }

        if (!File.Exists(source))
        {
            if (overwriteExisting && File.Exists(target))
            {
                File.Delete(target);
            }

            return;
        }

        if (!overwriteExisting && File.Exists(target))
        {
            return;
        }

        Directory.CreateDirectory(runtimeHome);
        File.Copy(source, target, overwrite: true);
    }

    private static void ResetManagedState(string runtimeHome)
    {
        foreach (var relativePath in StatePaths)
        {
            DeleteManagedPath(Path.Combine(runtimeHome, relativePath));
        }
    }

    private static void DeleteManagedPath(string path)
    {
        const int maxAttempts = 6;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                System.Threading.Thread.Sleep(150);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                System.Threading.Thread.Sleep(150);
            }
        }
    }

    private static void UpsertSharedCatalog(string sharedStoreHome, SessionRecord session, string sourceHome)
    {
        var catalogPath = Path.Combine(sharedStoreHome, "catalog.jsonl");
        Directory.CreateDirectory(sharedStoreHome);

        var entry = new JsonObject
        {
            ["session_id"] = session.Id,
            ["title"] = session.Title,
            ["cwd"] = session.Cwd,
            ["source_home"] = sourceHome,
            ["shared_session_path"] = session.SessionPath,
            ["original_provider"] = NormalizeModelProvider(session.ModelProvider),
            ["imported_at"] = DateTimeOffset.UtcNow.ToString("O")
        }.ToJsonString();

        var rows = new List<string>();
        var replaced = false;
        if (File.Exists(catalogPath))
        {
            foreach (var line in File.ReadLines(catalogPath, Encoding.UTF8))
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

        File.WriteAllLines(catalogPath, rows, new UTF8Encoding(false));
    }
}



