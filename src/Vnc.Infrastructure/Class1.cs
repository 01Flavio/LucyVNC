using Microsoft.Data.Sqlite;

namespace Vnc.Infrastructure;

public sealed record SavedComputer(
    Guid Id,
    string Name,
    string? Hostname,
    string? IpAddress,
    int Port,
    string? Group,
    bool IsFavorite,
    DateTimeOffset? LastConnectedUtc,
    string? LastResult,
    string ViewPreference,
    string? CredentialTarget = null)
{
    public string ConnectionHost =>
        !string.IsNullOrWhiteSpace(IpAddress) ? IpAddress : Hostname ?? string.Empty;
}

public sealed record ConnectionHistoryEntry(
    long Id,
    string Host,
    int Port,
    DateTimeOffset ConnectedUtc,
    long DurationMilliseconds,
    string Result);

public sealed class LocalComputerStore
{
    private readonly string _connectionString;

    public LocalComputerStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
    }

    public static LocalComputerStore CreateDefault()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LucyVNC");
        return new LocalComputerStore(Path.Combine(directory, "lucy.db"));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;";
            await setup.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var schemaVersion = Convert.ToInt32(
            await versionCommand.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        if (schemaVersion > 2)
        {
            throw new InvalidOperationException($"Database schema version {schemaVersion} is newer than this LucyVNC build supports.");
        }

        if (schemaVersion == 2)
        {
            return;
        }

        if (schemaVersion == 1)
        {
            await using var upgrade = connection.CreateCommand();
            upgrade.CommandText = """
                ALTER TABLE computers ADD COLUMN credential_target TEXT NULL;
                PRAGMA user_version = 2;
                """;
            await upgrade.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS computers (
                id TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                hostname TEXT NULL,
                ip_address TEXT NULL,
                port INTEGER NOT NULL CHECK(port BETWEEN 1 AND 65535),
                group_name TEXT NULL,
                is_favorite INTEGER NOT NULL DEFAULT 0,
                last_connected_utc TEXT NULL,
                last_result TEXT NULL,
                view_preference TEXT NOT NULL DEFAULT 'fit',
                credential_target TEXT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_computers_name ON computers(name COLLATE NOCASE);
            CREATE TABLE IF NOT EXISTS preferences (
                key TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS connection_history (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                host TEXT NOT NULL,
                port INTEGER NOT NULL CHECK(port BETWEEN 1 AND 65535),
                connected_utc TEXT NOT NULL,
                duration_ms INTEGER NOT NULL DEFAULT 0,
                result TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_history_connected_utc ON connection_history(connected_utc DESC);
            PRAGMA user_version = 2;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SavedComputer>> GetComputersAsync(
        string? search = null,
        bool favoritesOnly = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, hostname, ip_address, port, group_name, is_favorite,
                   last_connected_utc, last_result, view_preference, credential_target
            FROM computers
            WHERE ($favorites = 0 OR is_favorite = 1)
              AND ($search IS NULL OR name LIKE $pattern ESCAPE '\' COLLATE NOCASE
                   OR hostname LIKE $pattern ESCAPE '\' COLLATE NOCASE
                   OR ip_address LIKE $pattern ESCAPE '\' COLLATE NOCASE
                   OR group_name LIKE $pattern ESCAPE '\' COLLATE NOCASE)
            ORDER BY is_favorite DESC, name COLLATE NOCASE, id;
            """;
        command.Parameters.AddWithValue("$favorites", favoritesOnly ? 1 : 0);
        command.Parameters.AddWithValue("$search", string.IsNullOrWhiteSpace(search) ? DBNull.Value : search.Trim());
        command.Parameters.AddWithValue("$pattern", $"%{EscapeLike(search?.Trim() ?? string.Empty)}%");
        var records = new List<SavedComputer>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadComputer(reader));
        }

        return records;
    }

    public async Task<SavedComputer?> GetComputerAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, hostname, ip_address, port, group_name, is_favorite,
                   last_connected_utc, last_result, view_preference, credential_target
            FROM computers WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadComputer(reader) : null;
    }

    public async Task SaveComputerAsync(SavedComputer computer, CancellationToken cancellationToken = default)
    {
        ValidateComputer(computer);
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO computers (
                id, name, hostname, ip_address, port, group_name, is_favorite,
                last_connected_utc, last_result, view_preference, credential_target, created_utc, updated_utc)
            VALUES (
                $id, $name, $hostname, $ip, $port, $group, $favorite,
                $lastConnected, $lastResult, $view, $credentialTarget, $now, $now)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                hostname = excluded.hostname,
                ip_address = excluded.ip_address,
                port = excluded.port,
                group_name = excluded.group_name,
                is_favorite = excluded.is_favorite,
                last_connected_utc = excluded.last_connected_utc,
                last_result = excluded.last_result,
                view_preference = excluded.view_preference,
                credential_target = excluded.credential_target,
                updated_utc = excluded.updated_utc;
            """;
        AddComputerParameters(command, computer);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task ApplyComputerImportAsync(
        IReadOnlyCollection<SavedComputer> creates,
        IReadOnlyCollection<SavedComputer> updates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(creates);
        ArgumentNullException.ThrowIfNull(updates);
        foreach (var computer in creates.Concat(updates))
        {
            ValidateComputer(computer);
            if (computer.CredentialTarget is not null)
            {
                throw new ArgumentException("Imports cannot set credential references.", nameof(creates));
            }
        }

        if (creates.Select(item => item.Id).Intersect(updates.Select(item => item.Id)).Any() ||
            creates.Select(item => item.Id).Distinct().Count() != creates.Count ||
            updates.Select(item => item.Id).Distinct().Count() != updates.Count)
        {
            throw new ArgumentException("Imported computer identifiers must be unique.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");

        foreach (var computer in creates)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO computers (
                    id, name, hostname, ip_address, port, group_name, is_favorite,
                    last_connected_utc, last_result, view_preference, credential_target,
                    created_utc, updated_utc)
                VALUES ($id, $name, $hostname, $ip, $port, $group, $favorite,
                    NULL, NULL, $view, NULL, $now, $now);
                """;
            AddComputerParameters(command, computer);
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var computer in updates)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE computers
                SET name = $name, hostname = $hostname, ip_address = $ip, port = $port,
                    group_name = $group, view_preference = $view, updated_utc = $now
                WHERE id = $id;
                """;
            AddComputerParameters(command, computer with
            {
                IsFavorite = false,
                LastConnectedUtc = null,
                LastResult = null,
                CredentialTarget = null,
            });
            command.Parameters.AddWithValue("$now", now);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException($"Computer {computer.Id} changed after the import plan was created.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteComputerAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM computers WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetPreferenceAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO preferences(key, value) VALUES($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetPreferenceAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM preferences WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<long> RecordConnectionAsync(
        string host,
        int port,
        DateTimeOffset connectedUtc,
        long durationMilliseconds,
        string result,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535 || durationMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port and duration must be valid.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(result);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var history = connection.CreateCommand())
        {
            history.Transaction = transaction;
            history.CommandText = """
                INSERT INTO connection_history(host, port, connected_utc, duration_ms, result)
                VALUES($host, $port, $connected, $duration, $result);
                SELECT last_insert_rowid();
                """;
            history.Parameters.AddWithValue("$host", host.Trim());
            history.Parameters.AddWithValue("$port", port);
            history.Parameters.AddWithValue("$connected", connectedUtc.ToUniversalTime().ToString("O"));
            history.Parameters.AddWithValue("$duration", durationMilliseconds);
            history.Parameters.AddWithValue("$result", result);
            var insertedId = await history.ExecuteScalarAsync(cancellationToken);
            var historyId = Convert.ToInt64(insertedId, System.Globalization.CultureInfo.InvariantCulture);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE computers
                SET last_connected_utc = $connected, last_result = $result, updated_utc = $connected
                WHERE port = $port AND (
                    (hostname IS NOT NULL AND hostname = $host COLLATE NOCASE)
                    OR (ip_address IS NOT NULL AND ip_address = $host COLLATE NOCASE)
                );
                """;
            update.Parameters.AddWithValue("$connected", connectedUtc.ToUniversalTime().ToString("O"));
            update.Parameters.AddWithValue("$result", result);
            update.Parameters.AddWithValue("$port", port);
            update.Parameters.AddWithValue("$host", host.Trim());
            await update.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return historyId;
        }
    }

    public async Task CompleteConnectionAsync(
        long historyId,
        long durationMilliseconds,
        string result,
        CancellationToken cancellationToken = default)
    {
        if (historyId <= 0 || durationMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(historyId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(result);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE connection_history
            SET duration_ms = $duration, result = $result
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$duration", durationMilliseconds);
        command.Parameters.AddWithValue("$result", result);
        command.Parameters.AddWithValue("$id", historyId);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken);
        if (changed != 1)
        {
            throw new InvalidOperationException($"Connection history entry {historyId} was not found.");
        }
    }

    public async Task<IReadOnlyList<ConnectionHistoryEntry>> GetHistoryAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, host, port, connected_utc, duration_ms, result
            FROM connection_history ORDER BY id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var entries = new List<ConnectionHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new ConnectionHistoryEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt32(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                reader.GetInt64(4),
                reader.GetString(5)));
        }

        return entries;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static SavedComputer ReadComputer(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetInt32(6) != 0,
        reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10));

    private static void AddComputerParameters(SqliteCommand command, SavedComputer computer)
    {
        command.Parameters.AddWithValue("$id", computer.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", computer.Name.Trim());
        command.Parameters.AddWithValue("$hostname", (object?)NormalizeOptional(computer.Hostname) ?? DBNull.Value);
        command.Parameters.AddWithValue("$ip", (object?)NormalizeOptional(computer.IpAddress) ?? DBNull.Value);
        command.Parameters.AddWithValue("$port", computer.Port);
        command.Parameters.AddWithValue("$group", (object?)NormalizeOptional(computer.Group) ?? DBNull.Value);
        command.Parameters.AddWithValue("$favorite", computer.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$lastConnected", computer.LastConnectedUtc?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$lastResult", (object?)NormalizeOptional(computer.LastResult) ?? DBNull.Value);
        command.Parameters.AddWithValue("$view", computer.ViewPreference);
        command.Parameters.AddWithValue("$credentialTarget", (object?)NormalizeOptional(computer.CredentialTarget) ?? DBNull.Value);
    }

    private static void ValidateComputer(SavedComputer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        if (string.IsNullOrWhiteSpace(computer.Name))
        {
            throw new ArgumentException("A friendly name is required.", nameof(computer));
        }

        if (string.IsNullOrWhiteSpace(computer.Hostname) && string.IsNullOrWhiteSpace(computer.IpAddress))
        {
            throw new ArgumentException("Provide a hostname or IP address.", nameof(computer));
        }

        if (computer.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(computer), "Port must be between 1 and 65535.");
        }

        if (computer.CredentialTarget is not null &&
            !WindowsCredentialVault.IsViewerTarget(computer.CredentialTarget))
        {
            throw new ArgumentException("CredentialTarget must be a Windows Credential Manager reference, not a credential value.", nameof(computer));
        }
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
