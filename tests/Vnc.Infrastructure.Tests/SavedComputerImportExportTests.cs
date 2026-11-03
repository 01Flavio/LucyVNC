using System.Text.Json;
using Vnc.Infrastructure;

namespace Vnc.Infrastructure.Tests;

public sealed class SavedComputerImportExportTests
{
    [Fact]
    public async Task NativeJsonExportRoundTripsWithoutCredentialFields()
    {
        using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var saved = new SavedComputer(
            Guid.NewGuid(), "Primary, workstation", "host.local.", "192.168.10.9", 5901,
            "Office, West", true, DateTimeOffset.UtcNow, "Connected", "100%",
            WindowsCredentialVault.CreateViewerTarget("host.local", 5901));
        await database.Store.SaveComputerAsync(saved);
        var service = new SavedComputerImportExportService(database.Store);

        var json = await service.ExportAllJsonAsync();

        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(saved.CredentialTarget!, json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        using var importDatabase = new TestDatabase();
        await importDatabase.Store.InitializeAsync();
        var importService = new SavedComputerImportExportService(importDatabase.Store);
        var plan = await importService.PlanJsonImportAsync(json);
        Assert.Equal(1, plan.ValidCount);
        Assert.Equal(ComputerImportStatus.Ready, Assert.Single(plan.Entries).Status);
        await importService.ApplyImportAsync(plan);

        var imported = Assert.Single(await importDatabase.Store.GetComputersAsync());
        Assert.Equal(saved.Name, imported.Name);
        Assert.Equal(saved.Hostname, imported.Hostname);
        Assert.Equal(saved.IpAddress, imported.IpAddress);
        Assert.Equal(saved.Group, imported.Group);
        Assert.Equal(saved.Port, imported.Port);
        Assert.True(imported.IsFavorite);
        Assert.Equal(saved.ViewPreference, imported.ViewPreference);
        Assert.Null(imported.CredentialTarget);
        Assert.Null(imported.LastConnectedUtc);
        Assert.Null(imported.LastResult);
    }

    [Fact]
    public async Task CsvRoundTripsQuotedFieldsAndExportsAllSelectedAndFavorites()
    {
        using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var favorite = new SavedComputer(Guid.NewGuid(), "Work, desk", "work.local", null, 5900, "Group, one", true, null, null, "fit");
        var ordinary = new SavedComputer(Guid.NewGuid(), "Home", null, "192.168.1.20", 5901, null, false, null, null, "100%");
        await database.Store.SaveComputerAsync(favorite);
        await database.Store.SaveComputerAsync(ordinary);
        var service = new SavedComputerImportExportService(database.Store);

        var csv = await service.ExportAllCsvAsync();
        Assert.Contains("\"Work, desk\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"Group, one\"", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", csv, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, CountCsvRecords(await service.ExportFavoritesCsvAsync()));
        Assert.Equal(1, CountCsvRecords(await service.ExportSelectedCsvAsync([ordinary.Id])));
        Assert.Equal(2, CountCsvRecords(await service.ExportSelectedCsvAsync([favorite.Id, ordinary.Id])));
        Assert.Equal(2, CountJsonComputers(await service.ExportAllJsonAsync()));
        using var importDatabase = new TestDatabase();
        await importDatabase.Store.InitializeAsync();
        var importService = new SavedComputerImportExportService(importDatabase.Store);
        var plan = await importService.PlanCsvImportAsync(csv);
        Assert.Equal(2, plan.ValidCount);
        await importService.ApplyImportAsync(plan);

        Assert.Equal(2, CountJsonComputers(await importService.ExportAllJsonAsync()));
    }

    [Fact]
    public async Task ImportReportsMalformedEmptyAndInvalidFilesAndFields()
    {
        using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var service = new SavedComputerImportExportService(database.Store);

        Assert.Equal(1, (await service.PlanJsonImportAsync(" ")).InvalidCount);
        Assert.Equal(1, (await service.PlanJsonImportAsync("{")).InvalidCount);
        Assert.Equal(1, (await service.PlanJsonImportAsync("""{"schemaVersion":2,"computers":[]}""")).InvalidCount);
        Assert.Equal(1, (await service.PlanJsonImportAsync("""{"schemaVersion":1,"computers":[]}""")).InvalidCount);
        Assert.Equal(1, (await service.PlanCsvImportAsync(string.Empty)).InvalidCount);
        Assert.Equal(1, (await service.PlanCsvImportAsync("name,port\r\n")).InvalidCount);
        Assert.Equal(1, (await service.PlanCsvImportAsync("name,port\n\"unfinished,5900")).InvalidCount);

        const string invalidJson = """
            {
              "schemaVersion": 1,
              "computers": [
                {"name":"Missing host","port":5900},
                {"name":"Bad host","hostname":"https://host","port":5900},
                {"name":"Bad IP","ipAddress":"host.local","port":5900},
                {"name":"Bad port","hostname":"host.local","port":65536},
                {"name":"Bad view","hostname":"host.local","port":5900,"viewPreference":"stretch"},
                {"name":"Bad type","hostname":"host.local","port":"5900"}
              ]
            }
            """;
        var jsonPlan = await service.PlanJsonImportAsync(invalidJson);
        Assert.Equal(6, jsonPlan.InvalidCount);

        const string invalidCsv = """
            name,hostname,ipAddress,port,group,isFavorite,viewPreference
            no-host,,,5900,,false,fit
            bad-host,host/path,,5900,,false,fit
            bad-port,host.local,,0,,false,fit
            bad-favorite,host.local,,5900,,maybe,fit
            """;
        Assert.Equal(4, (await service.PlanCsvImportAsync(invalidCsv)).InvalidCount);
        Assert.Equal(1, (await service.PlanCsvImportAsync("name,password\nhost,pw")).InvalidCount);
        Assert.Equal(1, (await service.PlanJsonImportAsync(
            """{"schemaVersion":1,"computers":[{"name":"Host","hostname":"host.local","port":5900,"password":"secret"}]}""")).InvalidCount);
    }

    [Fact]
    public async Task EndpointNormalizationRecognizesHostnameAndIpAliasesAndInputDuplicates()
    {
        using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        await database.Store.SaveComputerAsync(new SavedComputer(
            Guid.NewGuid(), "Saved host", "VNC-HOST.", "192.168.1.9", 5900, null, false, null, null, "fit"));
        var service = new SavedComputerImportExportService(database.Store);
        const string json = """
            {
              "schemaVersion": 1,
              "computers": [
                {"name":"Alias","hostname":"vnc-host","port":5900},
                {"name":"Duplicate","hostname":"VNC-HOST.","port":5900},
                {"name":"IP alias","ipAddress":"192.168.1.9","port":5900}
              ]
            }
            """;

        var plan = await service.PlanJsonImportAsync(json);

        Assert.Equal(1, plan.ConflictCount);
        Assert.Equal(2, plan.DuplicateCount);
        var conflict = Assert.Single(plan.Entries, item => item.Status == ComputerImportStatus.Conflict);
        Assert.Single(conflict.ExistingMatches);
    }

    [Fact]
    public async Task TightVncProfileImportsEndpointAndNameButIgnoresCredentialsAndUnsupportedOptions()
    {
        using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var service = new SavedComputerImportExportService(database.Store);
        const string profile = """
            [Connection]
            Host=desk.example.local
            Port=5901
            ConnectionName=Office desktop
            Password=do-not-import-this
            ViewOnly=1
            Command=C:\unexpected\command.exe
            """;

        var plan = await service.PlanVncImportAsync(profile, "Office.vnc");

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(ComputerImportStatus.Ready, entry.Status);
        Assert.Equal("Office desktop", entry.Computer!.Name);
        Assert.Equal("desk.example.local", entry.Computer.Hostname);
        Assert.Equal(5901, entry.Computer.Port);
        Assert.Null(entry.Computer.CredentialTarget);
        Assert.Contains("Credential option detected and ignored.", entry.Notice, StringComparison.Ordinal);
        Assert.Contains("Command, ViewOnly", entry.Notice, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-import-this", entry.Notice, StringComparison.Ordinal);
        Assert.DoesNotContain("unexpected", entry.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[Connection]\nHost=desk.local:1\n", "desk.local", null, 5901)]
    [InlineData("[Connection]\nHost=192.168.10.8::5903\n", null, "192.168.10.8", 5903)]
    [InlineData("[Connection]\nHost=[fd00::12]\nPort=5904\n", null, "fd00::12", 5904)]
    [InlineData("[Connection]\nHost=2001:db8::1\nPort=5905\n", null, "2001:db8::1", 5905)]
    public async Task VncProfileSupportsCommonHostPortForms(string profile, string? hostname, string? ipAddress, int port)
    {
        using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var service = new SavedComputerImportExportService(database.Store);

        var plan = await service.PlanVncImportAsync(profile, "from-file.vnc");

        var computer = Assert.Single(plan.Entries).Computer!;
        Assert.Equal(hostname, computer.Hostname);
        Assert.Equal(ipAddress, computer.IpAddress);
        Assert.Equal(port, computer.Port);
    }

    [Fact]
    public async Task VncProfilesRejectInvalidHostsPortsAndDuplicateKeysWithoutExecutingLines()
    {
        using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var service = new SavedComputerImportExportService(database.Store);

        var badHost = await service.PlanVncImportAsync("[Connection]\nHost=https://host.local\n", "bad.vnc");
        var badPort = await service.PlanVncImportAsync("[Connection]\nHost=desk.local\nPort=70000\n", "bad-port.vnc");
        var duplicate = await service.PlanVncImportAsync("[Connection]\nHost=desk.local\nHost=other.local\n", "duplicate.vnc");
        var command = await service.PlanVncImportAsync("[Connection]\nHost=desk.local\nCommand=not executed\n", "command.vnc");

        Assert.Equal(1, badHost.InvalidCount);
        Assert.Equal(1, badPort.InvalidCount);
        Assert.Equal(1, duplicate.InvalidCount);
        Assert.Contains("Command", Assert.Single(command.Entries).Notice, StringComparison.Ordinal);
        Assert.Equal("desk.local", Assert.Single(command.Entries).Computer!.Hostname);
    }

    [Fact]
    public async Task VncBatchAndFolderImportsShareDuplicatePlanAndReportUnreadableFiles()
    {
        using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var folder = Path.Combine(Path.GetTempPath(), $"LucyVNC-vnc-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "one.vnc"), "[Connection]\nHost=desk.local\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "two.vnc"), "[Connection]\nHost=desk.local\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "bad.vnc"), "[Connection]\nPort=5900\n");
        try
        {
            var service = new SavedComputerImportExportService(database.Store);
            var plan = await service.PlanVncFolderImportAsync(folder);

            Assert.Equal(1, plan.ValidCount);
            Assert.Equal(1, plan.DuplicateCount);
            Assert.Equal(1, plan.InvalidCount);
            var largeProfile = await service.PlanVncImportAsync(
                new string('x', 1024 * 1024 + 1),
                "large.vnc");
            Assert.Equal(1, largeProfile.InvalidCount);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task MixedMultiFileImportReplansDuplicatesAcrossVncAndNativeFiles()
    {
        using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var folder = Path.Combine(Path.GetTempPath(), $"LucyVNC-mixed-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var vncPath = Path.Combine(folder, "desk.vnc");
        var jsonPath = Path.Combine(folder, "collection.lucyvnc.json");
        await File.WriteAllTextAsync(vncPath, "[Connection]\nHost=desk.local\nPort=5900\n");
        await File.WriteAllTextAsync(jsonPath, """
            {"schemaVersion":1,"computers":[{"name":"Desk alias","hostname":"desk.local","port":5900}]}
            """);
        try
        {
            var service = new SavedComputerImportExportService(database.Store);
            var plan = await service.PlanImportFilesAsync([vncPath, jsonPath]);

            Assert.Equal(1, plan.ValidCount);
            Assert.Equal(1, plan.DuplicateCount);
            var report = await service.ApplyImportAsync(plan);
            Assert.Equal(1, report.Created);
            Assert.Single(await database.Store.GetComputersAsync());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task VncExportContainsOnlySupportedNonSecretConnectionFieldsAndCanBeReimported()
    {
        using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var saved = new SavedComputer(
            Guid.NewGuid(), "Office desktop", "desk.local", null, 5900, "Work", true,
            null, null, "fit", WindowsCredentialVault.CreateViewerTarget("desk.local", 5900));
        await database.Store.SaveComputerAsync(saved);
        var service = new SavedComputerImportExportService(database.Store);

        var vnc = await service.ExportVncAsync(saved.Id);
        Assert.DoesNotContain("credential", vnc, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", vnc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Host=desk.local", vnc, StringComparison.Ordinal);
        Assert.Contains("Port=5900", vnc, StringComparison.Ordinal);
        Assert.Contains("ConnectionName=Office desktop", vnc, StringComparison.Ordinal);

        var plan = await service.PlanVncImportAsync(vnc, "exported.vnc");
        Assert.Equal(ComputerImportStatus.Conflict, Assert.Single(plan.Entries).Status);
        var imported = Assert.Single(plan.Entries).Computer!;
        Assert.Equal(saved.Name, imported.Name);
        Assert.Equal(saved.Port, imported.Port);
        Assert.Null(imported.Group);
        Assert.Null(imported.CredentialTarget);
    }

    [Fact]
    public async Task ImportReportSeparatesIgnoredConflictsFromRejectedRecords()
    {
        using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        await database.Store.SaveComputerAsync(new SavedComputer(
            Guid.NewGuid(), "Saved host", "host.local", null, 5900, null, false, null, null, "fit"));
        var service = new SavedComputerImportExportService(database.Store);
        const string json = """
            {
              "schemaVersion": 1,
              "computers": [
                {"name":"Existing","hostname":"host.local","port":5900},
                {"name":"Repeated","hostname":"host.local","port":5900},
                {"name":"Missing host","port":5900}
              ]
            }
            """;

        var plan = await service.PlanJsonImportAsync(json);
        var report = await service.ApplyImportAsync(plan, ImportConflictPolicy.Ignore);

        Assert.Equal(1, plan.ConflictCount);
        Assert.Equal(1, plan.DuplicateCount);
        Assert.Equal(1, plan.InvalidCount);
        Assert.Equal(new ComputerImportApplyReport(0, 0, 1), report);
    }

    [Fact]
    public async Task UpdatePolicyPreservesFavoriteCredentialsAndConnectionMetadata()
    {
        using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var connected = DateTimeOffset.UtcNow.AddMinutes(-2);
        var target = WindowsCredentialVault.CreateViewerTarget("host.local", 5900);
        var saved = new SavedComputer(
            Guid.NewGuid(), "Old name", "host.local", null, 5900, "Old group", true,
            connected, "Previously connected", "100%", target);
        await database.Store.SaveComputerAsync(saved);
        var service = new SavedComputerImportExportService(database.Store);
        var plan = await service.PlanJsonImportAsync("""
            {"schemaVersion":1,"computers":[
              {"name":"Updated name","hostname":"HOST.LOCAL.","port":5900,"group":"New group","isFavorite":false,"viewPreference":"fit"}
            ]}
            """);

        Assert.Equal(ComputerImportStatus.Conflict, Assert.Single(plan.Entries).Status);
        var report = await service.ApplyImportAsync(plan, ImportConflictPolicy.Update);

        Assert.Equal(new ComputerImportApplyReport(0, 1, 0), report);
        var updated = Assert.Single(await database.Store.GetComputersAsync());
        Assert.Equal(saved.Id, updated.Id);
        Assert.Equal("Updated name", updated.Name);
        Assert.Equal("New group", updated.Group);
        Assert.Equal("fit", updated.ViewPreference);
        Assert.True(updated.IsFavorite);
        Assert.Equal(target, updated.CredentialTarget);
        Assert.Equal(connected, updated.LastConnectedUtc);
        Assert.Equal("Previously connected", updated.LastResult);
    }

    [Fact]
    public async Task IgnoreAndCreateConflictPoliciesAreSelectable()
    {
        using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        await database.Store.SaveComputerAsync(new SavedComputer(
            Guid.NewGuid(), "Existing", "host.local", null, 5900, null, false, null, null, "fit"));
        var service = new SavedComputerImportExportService(database.Store);
        var plan = await service.PlanJsonImportAsync("""
            {"schemaVersion":1,"computers":[{"name":"Imported","hostname":"HOST.LOCAL.","port":5900}]}
            """);

        var ignored = await service.ApplyImportAsync(plan, ImportConflictPolicy.Ignore);
        Assert.Equal(new ComputerImportApplyReport(0, 0, 1), ignored);
        var created = await service.ApplyImportAsync(plan, ImportConflictPolicy.Create);
        Assert.Equal(new ComputerImportApplyReport(1, 0, 0), created);
        Assert.Equal(2, (await database.Store.GetComputersAsync()).Count);
    }

    [Fact]
    public async Task LargeCsvImportPlansAndAppliesThousandsOfRecords()
    {
        using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var service = new SavedComputerImportExportService(database.Store);
        var builder = new System.Text.StringBuilder("name,hostname,ipAddress,port,group,isFavorite,viewPreference\r\n");
        for (var index = 0; index < 5000; index++)
        {
            builder.Append("Computer ").Append(index).Append(",host-").Append(index)
                .Append(".local,,5900,,false,fit\r\n");
        }

        var plan = await service.PlanCsvImportAsync(builder.ToString());
        Assert.Equal(5000, plan.ValidCount);
        Assert.Equal(0, plan.InvalidCount);
        var report = await service.ApplyImportAsync(plan);
        Assert.Equal(5000, report.Created);
        Assert.Equal(5000, (await database.Store.GetComputersAsync()).Count);
    }

    private static int CountCsvRecords(string csv) =>
        csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1;

    private static int CountJsonComputers(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("computers").GetArrayLength();
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Environment.CurrentDirectory, ".lucyvnc-import-tests", Guid.NewGuid().ToString("N"));

        public TestDatabase()
        {
            Directory.CreateDirectory(_directory);
            Store = new LocalComputerStore(Path.Combine(_directory, "lucy.db"));
        }

        public LocalComputerStore Store { get; }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
                var parent = Path.GetDirectoryName(_directory)!;
                if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                {
                    Directory.Delete(parent);
                }
            }
        }
    }
}
