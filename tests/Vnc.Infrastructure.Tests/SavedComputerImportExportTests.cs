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
