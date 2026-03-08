using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexHomeManager.Models;
using Microsoft.Data.Sqlite;

namespace CodexHomeManager.Services;

public sealed class ProfileStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _databasePath;
    private readonly string _materializedProfilesRoot;
    private readonly object _initializationLock = new();
    private bool _isInitialized;

    public ProfileStore()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexHomeManager",
                "managed-accounts.db"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexHomeManager",
                "materialized-profiles"))
    {
    }

    public ProfileStore(string databasePath, string materializedProfilesRoot)
    {
        _databasePath = databasePath;
        _materializedProfilesRoot = materializedProfilesRoot;
        EnsureInitialized();
    }

    public string DefaultProfilesRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex-profiles");

    public string DatabasePath => _databasePath;

    public string MaterializedProfilesRoot => _materializedProfilesRoot;

    public IReadOnlyList<ProviderProfile> ListProfiles(string profilesRoot)
    {
        EnsureInitialized();
        MigrateLegacyProfilesIfDatabaseEmpty(profilesRoot);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT a.id, a.name, a.provider, c.revision, c.updated_at
FROM accounts a
JOIN account_contents c ON c.account_id = a.id
WHERE a.is_enabled = 1
ORDER BY a.name COLLATE NOCASE;";

        using var reader = command.ExecuteReader();
        var profiles = new List<ProviderProfile>();
        while (reader.Read())
        {
            profiles.Add(new ProviderProfile
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                DirectoryPath = string.Empty,
                ModelProvider = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Revision = reader.GetInt32(3),
                UpdatedAt = ParseTimestamp(reader.IsDBNull(4) ? null : reader.GetString(4))
            });
        }

        return profiles;
    }

    public ProviderProfile SaveProfile(string profilesRoot, string profileName, string sourceHome, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(sourceHome) || !Directory.Exists(sourceHome))
        {
            throw new DirectoryNotFoundException($"Profile source home does not exist: {sourceHome}");
        }

        EnsureInitialized();
        MigrateLegacyProfilesIfDatabaseEmpty(profilesRoot);

        var normalizedName = NormalizeProfileName(profileName);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new InvalidOperationException("Profile name cannot be empty.");
        }

        if (!overwrite && ProfileExists(normalizedName))
        {
            return GetProfileMetadata(normalizedName);
        }

        var authJson = ReadTextIfExists(Path.Combine(sourceHome, "auth.json"));
        var configToml = ReadTextIfExists(Path.Combine(sourceHome, "config.toml"));
        if (string.IsNullOrWhiteSpace(authJson) && string.IsNullOrWhiteSpace(configToml))
        {
            throw new InvalidOperationException("The selected source home does not contain auth.json or config.toml.");
        }

        return SaveProfileContentInternal(normalizedName, authJson, configToml);
    }

    public ProviderProfile ImportProfile(string profilesRoot, string sourceDirectory, string? profileName, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"Import source directory does not exist: {sourceDirectory}");
        }

        var suggestedName = NormalizeProfileName(profileName);
        if (string.IsNullOrWhiteSpace(suggestedName))
        {
            suggestedName = Path.GetFileName(
                Path.GetFullPath(sourceDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        return SaveProfile(profilesRoot, suggestedName, sourceDirectory, overwrite);
    }

    public string ExportProfile(string profilesRoot, string profileName, string targetRoot, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(targetRoot))
        {
            throw new InvalidOperationException("Export target directory cannot be empty.");
        }

        EnsureInitialized();
        MigrateLegacyProfilesIfDatabaseEmpty(profilesRoot);

        var content = GetProfileContent(profileName);
        Directory.CreateDirectory(targetRoot);

        var exportDirectory = Path.Combine(targetRoot, content.Name);
        if (Directory.Exists(exportDirectory) && !overwrite)
        {
            throw new InvalidOperationException($"Export target already exists: {exportDirectory}");
        }

        Directory.CreateDirectory(exportDirectory);
        WriteMaterializedFile(Path.Combine(exportDirectory, "auth.json"), content.AuthJson);
        WriteMaterializedFile(Path.Combine(exportDirectory, "config.toml"), content.ConfigToml);
        WriteProfileMetadata(exportDirectory, content.Name, content.ModelProvider, content.Revision, content.UpdatedAt);
        return exportDirectory;
    }

    public ProviderProfile GetProfile(string profilesRoot, string profileName)
    {
        EnsureInitialized();
        MigrateLegacyProfilesIfDatabaseEmpty(profilesRoot);

        var content = GetProfileContent(profileName);
        var materializedDirectory = MaterializeProfile(content);
        return new ProviderProfile
        {
            Id = content.AccountId,
            Name = content.Name,
            DirectoryPath = materializedDirectory,
            ModelProvider = content.ModelProvider,
            Revision = content.Revision,
            UpdatedAt = content.UpdatedAt
        };
    }

    public ManagedProfileContent GetProfileContent(string profileName)
    {
        EnsureInitialized();

        var normalizedName = NormalizeProfileName(profileName);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new InvalidOperationException("Profile name cannot be empty.");
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT a.id, a.name, a.provider, c.auth_json, c.config_toml, c.revision, c.updated_at
FROM accounts a
JOIN account_contents c ON c.account_id = a.id
WHERE a.name = $name COLLATE NOCASE
LIMIT 1;";
        command.Parameters.AddWithValue("$name", normalizedName);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new DirectoryNotFoundException($"Profile does not exist: {normalizedName}");
        }

        return new ManagedProfileContent
        {
            AccountId = reader.GetInt64(0),
            Name = reader.GetString(1),
            ModelProvider = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            AuthJson = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            ConfigToml = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            Revision = reader.GetInt32(5),
            UpdatedAt = ParseTimestamp(reader.IsDBNull(6) ? null : reader.GetString(6))
        };
    }

    public ManagedProfileContent GetOrCreateProfileContent(string profileName)
    {
        EnsureInitialized();

        var normalizedName = NormalizeProfileName(profileName);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new InvalidOperationException("Profile name cannot be empty.");
        }

        if (ProfileExists(normalizedName))
        {
            return GetProfileContent(normalizedName);
        }

        var timestamp = CreateTimestamp();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var insertAccount = connection.CreateCommand())
        {
            insertAccount.Transaction = transaction;
            insertAccount.CommandText = @"
INSERT INTO accounts (name, provider, remark, is_enabled, created_at, updated_at)
VALUES ($name, '', '', 1, $timestamp, $timestamp);";
            insertAccount.Parameters.AddWithValue("$name", normalizedName);
            insertAccount.Parameters.AddWithValue("$timestamp", timestamp);
            insertAccount.ExecuteNonQuery();
        }

        long accountId;
        using (var selectId = connection.CreateCommand())
        {
            selectId.Transaction = transaction;
            selectId.CommandText = "SELECT id FROM accounts WHERE name = $name COLLATE NOCASE LIMIT 1;";
            selectId.Parameters.AddWithValue("$name", normalizedName);
            accountId = Convert.ToInt64(selectId.ExecuteScalar());
        }

        using (var insertContents = connection.CreateCommand())
        {
            insertContents.Transaction = transaction;
            insertContents.CommandText = @"
INSERT INTO account_contents (
    account_id,
    auth_json,
    config_toml,
    auth_sha256,
    config_sha256,
    revision,
    created_at,
    updated_at)
VALUES (
    $accountId,
    '',
    '',
    '',
    '',
    1,
    $timestamp,
    $timestamp);";
            insertContents.Parameters.AddWithValue("$accountId", accountId);
            insertContents.Parameters.AddWithValue("$timestamp", timestamp);
            insertContents.ExecuteNonQuery();
        }

        transaction.Commit();
        return GetProfileContent(normalizedName);
    }

    public ManagedProfileContent SaveProfileContent(string profileName, string authJson, string configToml)
    {
        var profile = SaveProfileContentInternal(profileName, authJson, configToml);
        return GetProfileContent(profile.Name);
    }

    public ProviderProfile UpdateProfileFromHome(string profileName, string sourceHome)
    {
        return SaveProfile(DefaultProfilesRoot, profileName, sourceHome, overwrite: true);
    }

    public ProviderProfile CreateEmptyProfile(string profileName)
    {
        EnsureInitialized();

        var normalizedName = NormalizeProfileName(profileName);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new InvalidOperationException("Profile name cannot be empty.");
        }

        if (ProfileExists(normalizedName))
        {
            throw new InvalidOperationException($"Profile already exists: {normalizedName}");
        }

        return SaveProfileContentInternal(normalizedName, string.Empty, string.Empty);
    }

    public ProviderProfile RenameProfile(string currentProfileName, string newProfileName)
    {
        EnsureInitialized();

        var normalizedCurrentName = NormalizeProfileName(currentProfileName);
        if (string.IsNullOrWhiteSpace(normalizedCurrentName))
        {
            throw new InvalidOperationException("Current profile name cannot be empty.");
        }

        var normalizedNewName = NormalizeProfileName(newProfileName);
        if (string.IsNullOrWhiteSpace(normalizedNewName))
        {
            throw new InvalidOperationException("New profile name cannot be empty.");
        }

        if (string.Equals(normalizedCurrentName, normalizedNewName, StringComparison.Ordinal))
        {
            return GetProfileMetadata(normalizedCurrentName);
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        var accountId = TryGetAccountId(connection, transaction, normalizedCurrentName)
            ?? throw new InvalidOperationException($"Profile does not exist: {normalizedCurrentName}");
        var conflictingAccountId = TryGetAccountId(connection, transaction, normalizedNewName);
        if (conflictingAccountId is long otherAccountId && otherAccountId != accountId)
        {
            throw new InvalidOperationException($"Profile already exists: {normalizedNewName}");
        }

        using (var updateAccount = connection.CreateCommand())
        {
            updateAccount.Transaction = transaction;
            updateAccount.CommandText = @"
UPDATE accounts
SET name = $newName,
    updated_at = $timestamp
WHERE id = $accountId;";
            updateAccount.Parameters.AddWithValue("$newName", normalizedNewName);
            updateAccount.Parameters.AddWithValue("$timestamp", CreateTimestamp());
            updateAccount.Parameters.AddWithValue("$accountId", accountId);
            updateAccount.ExecuteNonQuery();
        }

        transaction.Commit();

        var content = GetProfileContent(normalizedNewName);
        var materializedDirectory = MaterializeProfile(content);
        DeleteMaterializedDirectoriesForAccount(accountId, materializedDirectory);
        return new ProviderProfile
        {
            Id = content.AccountId,
            Name = content.Name,
            DirectoryPath = materializedDirectory,
            ModelProvider = content.ModelProvider,
            Revision = content.Revision,
            UpdatedAt = content.UpdatedAt
        };
    }

    public void DeleteProfile(string profileName)
    {
        EnsureInitialized();

        var normalizedName = NormalizeProfileName(profileName);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new InvalidOperationException("Profile name cannot be empty.");
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        var accountId = TryGetAccountId(connection, transaction, normalizedName)
            ?? throw new InvalidOperationException($"Profile does not exist: {normalizedName}");

        using (var deleteAccount = connection.CreateCommand())
        {
            deleteAccount.Transaction = transaction;
            deleteAccount.CommandText = "DELETE FROM accounts WHERE id = $accountId;";
            deleteAccount.Parameters.AddWithValue("$accountId", accountId);
            deleteAccount.ExecuteNonQuery();
        }

        transaction.Commit();
        DeleteMaterializedDirectoriesForAccount(accountId);
    }

    public Dictionary<string, string> LoadSharedStoreDefaultLaunchProfiles()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT d.shared_store_key, a.name
FROM shared_store_defaults d
JOIN accounts a ON a.id = d.account_id
ORDER BY d.shared_store_path COLLATE NOCASE;";

        using var reader = command.ExecuteReader();
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var sharedStoreKey = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var profileName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            if (string.IsNullOrWhiteSpace(sharedStoreKey) || string.IsNullOrWhiteSpace(profileName))
            {
                continue;
            }

            mappings[sharedStoreKey] = profileName;
        }

        return mappings;
    }

    public Dictionary<string, string> MigrateSharedStoreDefaultLaunchProfiles(IReadOnlyDictionary<string, string>? mappings)
    {
        EnsureInitialized();
        if (mappings is null || mappings.Count == 0)
        {
            return LoadSharedStoreDefaultLaunchProfiles();
        }

        using var connection = OpenConnection();
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(1) FROM shared_store_defaults;";
        var existingCount = Convert.ToInt32(countCommand.ExecuteScalar());
        if (existingCount > 0)
        {
            return LoadSharedStoreDefaultLaunchProfiles();
        }

        SaveSharedStoreDefaultLaunchProfiles(mappings);
        return LoadSharedStoreDefaultLaunchProfiles();
    }

    public void SaveSharedStoreDefaultLaunchProfiles(IReadOnlyDictionary<string, string> mappings)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM shared_store_defaults;";
            deleteCommand.ExecuteNonQuery();
        }

        foreach (var pair in mappings)
        {
            var sharedStoreKey = NormalizeStoreKey(pair.Key);
            var profileName = NormalizeProfileName(pair.Value);
            if (string.IsNullOrWhiteSpace(sharedStoreKey) || string.IsNullOrWhiteSpace(profileName))
            {
                continue;
            }

            var accountId = TryGetAccountId(connection, transaction, profileName);
            if (accountId is null)
            {
                continue;
            }

            using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = @"
INSERT INTO shared_store_defaults (shared_store_key, shared_store_path, account_id, created_at, updated_at)
VALUES ($sharedStoreKey, $sharedStorePath, $accountId, $timestamp, $timestamp);";
            insertCommand.Parameters.AddWithValue("$sharedStoreKey", sharedStoreKey);
            insertCommand.Parameters.AddWithValue("$sharedStorePath", pair.Key.Trim());
            insertCommand.Parameters.AddWithValue("$accountId", accountId.Value);
            insertCommand.Parameters.AddWithValue("$timestamp", CreateTimestamp());
            insertCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private ProviderProfile SaveProfileContentInternal(string profileName, string authJson, string configToml)
    {
        EnsureInitialized();

        var normalizedName = NormalizeProfileName(profileName);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new InvalidOperationException("Profile name cannot be empty.");
        }

        authJson ??= string.Empty;
        configToml ??= string.Empty;
        var provider = InferProvider(configToml, authJson);
        var timestamp = CreateTimestamp();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        var accountId = TryGetAccountId(connection, transaction, normalizedName);
        int revision;
        if (accountId is null)
        {
            using (var insertAccount = connection.CreateCommand())
            {
                insertAccount.Transaction = transaction;
                insertAccount.CommandText = @"
INSERT INTO accounts (name, provider, remark, is_enabled, created_at, updated_at)
VALUES ($name, $provider, '', 1, $timestamp, $timestamp);";
                insertAccount.Parameters.AddWithValue("$name", normalizedName);
                insertAccount.Parameters.AddWithValue("$provider", provider);
                insertAccount.Parameters.AddWithValue("$timestamp", timestamp);
                insertAccount.ExecuteNonQuery();
            }

            accountId = TryGetAccountId(connection, transaction, normalizedName)
                ?? throw new InvalidOperationException($"Unable to create profile: {normalizedName}");
            revision = 1;

            using var insertContents = connection.CreateCommand();
            insertContents.Transaction = transaction;
            insertContents.CommandText = @"
INSERT INTO account_contents (
    account_id,
    auth_json,
    config_toml,
    auth_sha256,
    config_sha256,
    revision,
    created_at,
    updated_at)
VALUES (
    $accountId,
    $authJson,
    $configToml,
    $authSha256,
    $configSha256,
    $revision,
    $timestamp,
    $timestamp);";
            insertContents.Parameters.AddWithValue("$accountId", accountId.Value);
            insertContents.Parameters.AddWithValue("$authJson", authJson);
            insertContents.Parameters.AddWithValue("$configToml", configToml);
            insertContents.Parameters.AddWithValue("$authSha256", ComputeSha256(authJson));
            insertContents.Parameters.AddWithValue("$configSha256", ComputeSha256(configToml));
            insertContents.Parameters.AddWithValue("$revision", revision);
            insertContents.Parameters.AddWithValue("$timestamp", timestamp);
            insertContents.ExecuteNonQuery();
        }
        else
        {
            using (var updateAccount = connection.CreateCommand())
            {
                updateAccount.Transaction = transaction;
                updateAccount.CommandText = @"
UPDATE accounts
SET provider = $provider,
    updated_at = $timestamp
WHERE id = $accountId;";
                updateAccount.Parameters.AddWithValue("$provider", provider);
                updateAccount.Parameters.AddWithValue("$timestamp", timestamp);
                updateAccount.Parameters.AddWithValue("$accountId", accountId.Value);
                updateAccount.ExecuteNonQuery();
            }

            using var selectRevision = connection.CreateCommand();
            selectRevision.Transaction = transaction;
            selectRevision.CommandText = "SELECT revision FROM account_contents WHERE account_id = $accountId LIMIT 1;";
            selectRevision.Parameters.AddWithValue("$accountId", accountId.Value);
            var existingRevision = selectRevision.ExecuteScalar();
            revision = existingRevision is null || existingRevision == DBNull.Value
                ? 1
                : Convert.ToInt32(existingRevision) + 1;

            using var upsertContents = connection.CreateCommand();
            upsertContents.Transaction = transaction;
            upsertContents.CommandText = @"
INSERT INTO account_contents (
    account_id,
    auth_json,
    config_toml,
    auth_sha256,
    config_sha256,
    revision,
    created_at,
    updated_at)
VALUES (
    $accountId,
    $authJson,
    $configToml,
    $authSha256,
    $configSha256,
    $revision,
    $timestamp,
    $timestamp)
ON CONFLICT(account_id) DO UPDATE SET
    auth_json = excluded.auth_json,
    config_toml = excluded.config_toml,
    auth_sha256 = excluded.auth_sha256,
    config_sha256 = excluded.config_sha256,
    revision = excluded.revision,
    updated_at = excluded.updated_at;";
            upsertContents.Parameters.AddWithValue("$accountId", accountId.Value);
            upsertContents.Parameters.AddWithValue("$authJson", authJson);
            upsertContents.Parameters.AddWithValue("$configToml", configToml);
            upsertContents.Parameters.AddWithValue("$authSha256", ComputeSha256(authJson));
            upsertContents.Parameters.AddWithValue("$configSha256", ComputeSha256(configToml));
            upsertContents.Parameters.AddWithValue("$revision", revision);
            upsertContents.Parameters.AddWithValue("$timestamp", timestamp);
            upsertContents.ExecuteNonQuery();
        }

        transaction.Commit();
        var content = GetProfileContent(normalizedName);
        MaterializeProfile(content);
        return new ProviderProfile
        {
            Id = content.AccountId,
            Name = content.Name,
            DirectoryPath = GetMaterializedDirectory(content.AccountId, content.Name),
            ModelProvider = content.ModelProvider,
            Revision = content.Revision,
            UpdatedAt = content.UpdatedAt
        };
    }

    private void MigrateLegacyProfilesIfDatabaseEmpty(string profilesRoot)
    {
        if (CountProfiles() > 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(profilesRoot) || !Directory.Exists(profilesRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(profilesRoot))
        {
            var name = Path.GetFileName(directory);
            var authPath = Path.Combine(directory, "auth.json");
            var configPath = Path.Combine(directory, "config.toml");
            if (!File.Exists(authPath) && !File.Exists(configPath))
            {
                continue;
            }

            SaveProfileContentInternal(name, ReadTextIfExists(authPath), ReadTextIfExists(configPath));
        }
    }

    private int CountProfiles()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM accounts;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private ProviderProfile GetProfileMetadata(string profileName)
    {
        var content = GetProfileContent(profileName);
        return new ProviderProfile
        {
            Id = content.AccountId,
            Name = content.Name,
            DirectoryPath = GetMaterializedDirectory(content.AccountId, content.Name),
            ModelProvider = content.ModelProvider,
            Revision = content.Revision,
            UpdatedAt = content.UpdatedAt
        };
    }

    private bool ProfileExists(string profileName)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM accounts WHERE name = $name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$name", profileName);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private string MaterializeProfile(ManagedProfileContent content)
    {
        var directory = GetMaterializedDirectory(content.AccountId, content.Name);
        Directory.CreateDirectory(directory);
        WriteMaterializedFile(Path.Combine(directory, "auth.json"), content.AuthJson);
        WriteMaterializedFile(Path.Combine(directory, "config.toml"), content.ConfigToml);
        WriteProfileMetadata(directory, content.Name, content.ModelProvider, content.Revision, content.UpdatedAt);
        return directory;
    }

    private string GetMaterializedDirectory(long accountId, string profileName)
    {
        var safeName = NormalizeProfileName(profileName);
        return Path.Combine(_materializedProfilesRoot, $"{accountId:D6}-{safeName}");
    }

    private void DeleteMaterializedDirectoriesForAccount(long accountId, string? exceptDirectory = null)
    {
        if (!Directory.Exists(_materializedProfilesRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(_materializedProfilesRoot, $"{accountId:D6}-*", SearchOption.TopDirectoryOnly))
        {
            if (!string.IsNullOrWhiteSpace(exceptDirectory) && SameDirectory(directory, exceptDirectory))
            {
                continue;
            }

            TryDeleteDirectory(directory);
        }
    }

    private static bool SameDirectory(string left, string right)
    {
        try
        {
            var normalizedLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup for stale materialized directories.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup for stale materialized directories.
        }
    }

    private void EnsureInitialized()
    {
        if (_isInitialized)
        {
            return;
        }

        lock (_initializationLock)
        {
            if (_isInitialized)
            {
                return;
            }

            var databaseDirectory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrWhiteSpace(databaseDirectory))
            {
                Directory.CreateDirectory(databaseDirectory);
            }

            Directory.CreateDirectory(_materializedProfilesRoot);

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA journal_mode = WAL;
CREATE TABLE IF NOT EXISTS accounts (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    name              TEXT NOT NULL COLLATE NOCASE UNIQUE,
    provider          TEXT NOT NULL DEFAULT '',
    remark            TEXT NOT NULL DEFAULT '',
    is_enabled        INTEGER NOT NULL DEFAULT 1 CHECK (is_enabled IN (0, 1)),
    created_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);
CREATE TABLE IF NOT EXISTS account_contents (
    account_id        INTEGER PRIMARY KEY,
    auth_json         TEXT NOT NULL DEFAULT '',
    config_toml       TEXT NOT NULL DEFAULT '',
    auth_sha256       TEXT NOT NULL DEFAULT '',
    config_sha256     TEXT NOT NULL DEFAULT '',
    revision          INTEGER NOT NULL DEFAULT 1 CHECK (revision >= 1),
    created_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    FOREIGN KEY (account_id) REFERENCES accounts(id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS shared_store_defaults (
    shared_store_key  TEXT PRIMARY KEY COLLATE NOCASE,
    shared_store_path TEXT NOT NULL,
    account_id        INTEGER NOT NULL,
    created_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    FOREIGN KEY (account_id) REFERENCES accounts(id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS materialized_targets (
    target_home_key   TEXT PRIMARY KEY COLLATE NOCASE,
    target_home_path  TEXT NOT NULL,
    account_id        INTEGER NOT NULL,
    revision          INTEGER NOT NULL DEFAULT 0,
    auth_sha256       TEXT NOT NULL DEFAULT '',
    config_sha256     TEXT NOT NULL DEFAULT '',
    last_written_at   TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    FOREIGN KEY (account_id) REFERENCES accounts(id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS app_settings (
    key               TEXT PRIMARY KEY,
    value             TEXT NOT NULL DEFAULT '',
    updated_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);
CREATE INDEX IF NOT EXISTS idx_accounts_provider ON accounts(provider);
CREATE INDEX IF NOT EXISTS idx_accounts_enabled ON accounts(is_enabled);
CREATE INDEX IF NOT EXISTS idx_shared_store_defaults_account_id ON shared_store_defaults(account_id);
CREATE INDEX IF NOT EXISTS idx_materialized_targets_account_id ON materialized_targets(account_id);";
            command.ExecuteNonQuery();

            _isInitialized = true;
        }
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    private static long? TryGetAccountId(SqliteConnection connection, SqliteTransaction transaction, string profileName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM accounts WHERE name = $name COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("$name", profileName);
        var value = command.ExecuteScalar();
        return value is null || value == DBNull.Value ? null : Convert.ToInt64(value);
    }

    private static string ReadTextIfExists(string path)
    {
        return File.Exists(path)
            ? File.ReadAllText(path, Encoding.UTF8)
            : string.Empty;
    }

    private static void WriteMaterializedFile(string path, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Utf8NoBom);
    }

    private static void WriteProfileMetadata(string profileDirectory, string name, string provider, int revision, DateTimeOffset updatedAt)
    {
        var metadataPath = Path.Combine(profileDirectory, "profile.json");
        var payload = new JsonObject
        {
            ["name"] = name,
            ["model_provider"] = provider,
            ["revision"] = revision,
            ["updated_at"] = updatedAt.ToString("O")
        };

        File.WriteAllText(
            metadataPath,
            payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            Utf8NoBom);
    }

    private static string NormalizeProfileName(string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return string.Empty;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(profileName.Trim().Length);
        foreach (var character in profileName.Trim())
        {
            builder.Append(invalidChars.Contains(character) ? '_' : character);
        }

        return builder.ToString().Trim();
    }

    private static string NormalizeStoreKey(string? sharedStoreHome)
    {
        if (string.IsNullOrWhiteSpace(sharedStoreHome))
        {
            return string.Empty;
        }

        var trimmed = sharedStoreHome.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return trimmed;
        }
    }

    private static string ComputeSha256(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    private static string InferProvider(string configToml, string authJson)
    {
        var configuredProvider = TryReadTopLevelTomlStringValue(configToml, "model_provider");
        if (!string.IsNullOrWhiteSpace(configuredProvider))
        {
            return NormalizeProvider(configuredProvider);
        }

        if (!string.IsNullOrWhiteSpace(authJson))
        {
            try
            {
                using var document = JsonDocument.Parse(authJson);
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
        }

        return string.Empty;
    }

    private static string? TryReadTopLevelTomlStringValue(string content, string key)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var inSection = false;
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } rawLine)
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

    private static string NormalizeProvider(string? provider)
    {
        return string.IsNullOrWhiteSpace(provider)
            ? string.Empty
            : provider.Trim().ToLowerInvariant();
    }

    private static string CreateTimestamp()
    {
        return DateTimeOffset.UtcNow.ToString("O");
    }

    private static DateTimeOffset ParseTimestamp(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
    }
}

