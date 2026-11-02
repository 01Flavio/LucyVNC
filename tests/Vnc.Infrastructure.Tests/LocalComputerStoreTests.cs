using Vnc.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Vnc.Infrastructure.Tests;

public sealed class LocalComputerStoreTests
{
    [Fact]
    public async Task ComputersCanBeCreatedEditedFavoritedSearchedAndDeleted()
    {
        using var database = new TemporaryDatabase();
        var store = database.Store;
        await store.InitializeAsync();
        var id = Guid.NewGuid();

        await store.SaveComputerAsync(new SavedComputer(
            id, "Office PC", "office-pc", null, 5900, "Work", false, null, null, "fit"));
        await store.SaveComputerAsync(new SavedComputer(
            id, "Office Workstation", "office-pc", "192.168.10.22", 5901, "Work", true, null, null, "100%"));

        var edited = Assert.Single(await store.GetComputersAsync("Office W"));
        Assert.Equal("Office Workstation", edited.Name);
        Assert.Equal("192.168.10.22", edited.ConnectionHost);
        Assert.Equal(5901, edited.Port);
        Assert.True(edited.IsFavorite);
        Assert.Equal("100%", edited.ViewPreference);
        Assert.Single(await store.GetComputersAsync(favoritesOnly: true));

        await store.DeleteComputerAsync(id);
        Assert.Empty(await store.GetComputersAsync());
    }

    [Fact]
    public async Task ComputerMayUseOnlyHostnameOrOnlyIpAddress()
    {
        using var database = new TemporaryDatabase();
        await database.Store.InitializeAsync();
        var hostname = new SavedComputer(Guid.NewGuid(), "Host only", "machine.local", null, 5900, null, false, null, null, "fit");
        var ipOnly = new SavedComputer(Guid.NewGuid(), "IP only", null, "192.168.1.15", 5900, null, false, null, null, "fit");

        await database.Store.SaveComputerAsync(hostname);
        await database.Store.SaveComputerAsync(ipOnly);

        var records = await database.Store.GetComputersAsync();
        Assert.Equal(2, records.Count);
        Assert.Equal("machine.local", records.Single(item => item.Id == hostname.Id).ConnectionHost);
        Assert.Equal("192.168.1.15", records.Single(item => item.Id == ipOnly.Id).ConnectionHost);
    }

    [Fact]
    public async Task SuccessfulConnectionUpdatesKnownComputerAndHistoryWithoutCredentials()
    {
        using var database = new TemporaryDatabase();
        await database.Store.InitializeAsync();
        var id = Guid.NewGuid();
        await database.Store.SaveComputerAsync(new SavedComputer(
            id, "Lab", "lab-pc", null, 5900, null, true, null, null, "fit"));
        var connectedAt = DateTimeOffset.UtcNow;

        await database.Store.RecordConnectionAsync("lab-pc", 5900, connectedAt, 1250, "Authenticated");

        var saved = Assert.Single(await database.Store.GetComputersAsync());
        Assert.Equal(connectedAt, saved.LastConnectedUtc);
        Assert.Equal("Authenticated", saved.LastResult);
        var history = Assert.Single(await database.Store.GetHistoryAsync());
        Assert.Equal("lab-pc", history.Host);
        Assert.Equal(1250, history.DurationMilliseconds);
        Assert.Equal("Authenticated", history.Result);
    }

    [Fact]
    public async Task PreferencesAndSchemaMigrationAreRepeatable()
    {
        using var database = new TemporaryDatabase();
        await database.Store.InitializeAsync();
        await database.Store.SetPreferenceAsync("sort", "last-connected");
        await database.Store.InitializeAsync();

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        Assert.Equal("last-connected", await database.Store.GetPreferenceAsync("sort"));
        Assert.Equal(2L, await versionCommand.ExecuteScalarAsync());
        Assert.Empty(await database.Store.GetHistoryAsync());
        Assert.Empty(await database.Store.GetComputersAsync());
    }

    [Fact]
    public async Task VersionOneDatabaseIsMigratedWithCredentialReferenceOnly()
    {
        using var database = new TemporaryDatabase();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE computers (
                    id TEXT NOT NULL PRIMARY KEY, name TEXT NOT NULL, hostname TEXT NULL,
                    ip_address TEXT NULL, port INTEGER NOT NULL, group_name TEXT NULL,
                    is_favorite INTEGER NOT NULL DEFAULT 0, last_connected_utc TEXT NULL,
                    last_result TEXT NULL, view_preference TEXT NOT NULL DEFAULT 'fit',
                    created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL
                );
                PRAGMA user_version = 1;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await database.Store.InitializeAsync();
        await using var migrated = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Pooling = false,
        }.ToString());
        await migrated.OpenAsync();
        await using var versionCommand = migrated.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        Assert.Equal(2L, await versionCommand.ExecuteScalarAsync());
        await using var columnCommand = migrated.CreateCommand();
        columnCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('computers') WHERE name = 'credential_target';";
        Assert.Equal(1L, await columnCommand.ExecuteScalarAsync());
    }

    [Fact]
    public void CredentialTargetIsEndpointSpecificAndNormalized()
    {
        Assert.Equal(
            WindowsCredentialVault.CreateViewerTarget("VNC-HOST.", 5900),
            WindowsCredentialVault.CreateViewerTarget("vnc-host", 5900));
        Assert.NotEqual(
            WindowsCredentialVault.CreateViewerTarget("vnc-host", 5900),
            WindowsCredentialVault.CreateViewerTarget("vnc-host", 5901));
        Assert.NotEqual(
            WindowsCredentialVault.CreateViewerTarget("192.168.1.1", 5900),
            WindowsCredentialVault.CreateViewerTarget("192.168.1.2", 5900));
        Assert.False(WindowsCredentialVault.IsViewerTarget("remember-this-password"));
    }

    [Fact]
    public void WindowsCredentialVaultRoundTripsAndDeletesAnIsolatedTestCredential()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var vault = new WindowsCredentialVault();
        var target = WindowsCredentialVault.CreateViewerTarget($"credential-test-{Guid.NewGuid():N}.invalid", 5900);
        const string secret = "lucy-test";
        try
        {
            vault.Write(target, secret);
            Assert.Equal(secret, vault.Read(target));
        }
        finally
        {
            vault.Delete(target);
        }

        Assert.Null(vault.Read(target));
    }

    [Fact]
    public async Task ComputerStoresCredentialManagerReferenceButRejectsSecretValue()
    {
        using var database = new TemporaryDatabase();
        await database.Store.InitializeAsync();
        var computer = new SavedComputer(
            Guid.NewGuid(), "Lab", "lab-pc", null, 5900, null, false, null, null, "fit",
            WindowsCredentialVault.CreateViewerTarget("lab-pc", 5900));

        await database.Store.SaveComputerAsync(computer);
        Assert.Equal(computer.CredentialTarget, Assert.Single(await database.Store.GetComputersAsync()).CredentialTarget);
        await Assert.ThrowsAsync<ArgumentException>(() => database.Store.SaveComputerAsync(computer with { CredentialTarget = "do-not-store-this-password" }));
    }

    [Fact]
    public async Task StoreRejectsDatabaseSchemaNewerThanSupported()
    {
        using var database = new TemporaryDatabase();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 9;";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Store.InitializeAsync());
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "LucyVNC.Tests", Guid.NewGuid().ToString("N"));

        public TemporaryDatabase()
        {
            DatabasePath = Path.Combine(_directory, "lucy.db");
            Store = new LocalComputerStore(DatabasePath);
        }

        public string DatabasePath { get; }
        public LocalComputerStore Store { get; }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
