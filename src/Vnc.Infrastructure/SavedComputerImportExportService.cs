using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Vnc.Infrastructure;

public enum ComputerImportStatus
{
    Ready,
    Invalid,
    Duplicate,
    Conflict,
}

public enum ComputerImportAction
{
    Create,
    Update,
    Ignore,
}

public enum ImportConflictPolicy
{
    Ignore,
    Update,
    Create,
}

public sealed record ComputerImportEntry(
    int RecordNumber,
    ComputerImportStatus Status,
    string? Message,
    SavedComputer? Computer,
    IReadOnlyList<SavedComputer> ExistingMatches,
    ComputerImportAction SuggestedAction,
    string? Notice = null);

public sealed class ComputerImportPlan
{
    internal ComputerImportPlan(IReadOnlyList<ComputerImportEntry> entries) => Entries = entries;

    public IReadOnlyList<ComputerImportEntry> Entries { get; }
    public int ValidCount => Entries.Count(entry => entry.Status is ComputerImportStatus.Ready or ComputerImportStatus.Conflict);
    public int InvalidCount => Entries.Count(entry => entry.Status == ComputerImportStatus.Invalid);
    public int DuplicateCount => Entries.Count(entry => entry.Status == ComputerImportStatus.Duplicate);
    public int ConflictCount => Entries.Count(entry => entry.Status == ComputerImportStatus.Conflict);
}

public sealed record ComputerImportApplyReport(int Created, int Updated, int Ignored);

public sealed class SavedComputerImportExportService
{
    private const int NativeSchemaVersion = 1;
    private const int MaximumImportFileBytes = 10 * 1024 * 1024;
    private const int MaximumVncFileBytes = 1024 * 1024;
    private const int MaximumVncFileCount = 1000;
    private static readonly string[] CsvHeaders =
    [
        "name", "hostname", "ipAddress", "port", "group", "isFavorite", "viewPreference",
    ];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly LocalComputerStore _store;

    public SavedComputerImportExportService(LocalComputerStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<ComputerImportPlan> PlanJsonImportAsync(
        string json,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return FileError("The JSON import file is empty.");
        }
        if (Encoding.UTF8.GetByteCount(json) > MaximumImportFileBytes)
        {
            return FileError("The JSON import file exceeds the 10 MB size limit.");
        }

        List<(int Number, SavedComputer? Computer, string? Error)> rows;
        try
        {
            using var document = JsonDocument.Parse(json.TrimStart('\uFEFF'));
            rows = ParseJsonDocument(document.RootElement);
        }
        catch (JsonException exception)
        {
            return FileError($"Invalid JSON: {exception.Message}");
        }
        catch (InvalidDataException exception)
        {
            return FileError(exception.Message);
        }

        return await BuildPlanAsync(rows, cancellationToken);
    }

    public async Task<ComputerImportPlan> PlanCsvImportAsync(
        string csv,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return FileError("The CSV import file is empty.");
        }
        if (Encoding.UTF8.GetByteCount(csv) > MaximumImportFileBytes)
        {
            return FileError("The CSV import file exceeds the 10 MB size limit.");
        }

        List<IReadOnlyList<string>> rows;
        try
        {
            rows = ParseCsv(csv);
        }
        catch (InvalidDataException exception)
        {
            return FileError(exception.Message);
        }

        if (rows.Count == 0)
        {
            return FileError("The CSV import file has no header.");
        }

        var header = rows[0].Select(value => value.Trim().TrimStart('\uFEFF')).ToArray();
        var headerLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < header.Length; index++)
        {
            if (!CsvHeaders.Contains(header[index], StringComparer.OrdinalIgnoreCase) ||
                !headerLookup.TryAdd(header[index], index))
            {
                return FileError($"Invalid or duplicate CSV header '{header[index]}'.");
            }
        }

        foreach (var required in new[] { "name", "port" })
        {
            if (!headerLookup.ContainsKey(required))
            {
                return FileError($"Required CSV header '{required}' is missing.");
            }
        }

        var parsed = new List<(int Number, SavedComputer? Computer, string? Error)>();
        for (var index = 1; index < rows.Count; index++)
        {
            var fields = rows[index];
            if (fields.Count == 1 && string.IsNullOrWhiteSpace(fields[0]))
            {
                continue;
            }

            var recordNumber = index;
            if (fields.Count != header.Length)
            {
                parsed.Add((recordNumber, null, "The CSV record has a different number of fields than the header."));
                continue;
            }

            string Value(string key) =>
                headerLookup.TryGetValue(key, out var column) ? fields[column].Trim() : string.Empty;

            if (!int.TryParse(Value("port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            {
                parsed.Add((recordNumber, null, "Port must be an integer between 1 and 65535."));
                continue;
            }

            if (!TryParseBoolean(Value("isFavorite"), out var favorite))
            {
                parsed.Add((recordNumber, null, "isFavorite must be 'true' or 'false'."));
                continue;
            }

            var candidate = CreateCandidate(
                Value("name"),
                Value("hostname"),
                Value("ipAddress"),
                port,
                Value("group"),
                favorite,
                Value("viewPreference"));
            parsed.Add((recordNumber, candidate.Computer, candidate.Error));
        }

        if (parsed.Count == 0)
        {
            return FileError("The CSV file contains no computer records.");
        }

        return await BuildPlanAsync(parsed, cancellationToken);
    }

    public async Task<ComputerImportPlan> PlanVncImportAsync(
        string content,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (content.Length == 0)
        {
            return FileError("The VNC profile is empty.");
        }

        if (Encoding.UTF8.GetByteCount(content) > MaximumVncFileBytes)
        {
            return FileError("The VNC profile exceeds the 1 MB size limit.");
        }

        var parsed = ParseVncProfile(content, Path.GetFileNameWithoutExtension(fileName));
        if (parsed.Computer is null)
        {
            return FileError(parsed.Error ?? "The VNC profile is invalid.");
        }

        var plan = await BuildPlanAsync([(1, parsed.Computer, null)], cancellationToken);
        return WithNotices(plan, new Dictionary<int, string?> { [1] = parsed.Notice });
    }

    public async Task<ComputerImportPlan> PlanVncFilesImportAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var paths = filePaths.ToArray();
        if (paths.Length == 0)
        {
            return FileError("Select at least one .vnc profile.");
        }

        if (paths.Length > MaximumVncFileCount)
        {
            return FileError($"A maximum of {MaximumVncFileCount} VNC profiles can be imported at once.");
        }

        var parsedRows = new List<(int Number, SavedComputer? Computer, string? Error)>();
        var notices = new Dictionary<int, string?>();
        long totalBytes = 0;
        var recordNumber = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!string.Equals(Path.GetExtension(path), ".vnc", StringComparison.OrdinalIgnoreCase))
                {
                    parsedRows.Add((recordNumber, null, "Only .vnc profile files are supported in a multi-file VNC import."));
                    continue;
                }

                var file = new FileInfo(path);
                if (!file.Exists)
                {
                    parsedRows.Add((recordNumber, null, "The VNC profile does not exist."));
                    continue;
                }

                if (file.Length > MaximumVncFileBytes)
                {
                    parsedRows.Add((recordNumber, null, "The VNC profile exceeds the 1 MB size limit."));
                    continue;
                }

                totalBytes += file.Length;
                if (totalBytes > MaximumImportFileBytes)
                {
                    parsedRows.Add((recordNumber, null, "The selected VNC profiles exceed the 10 MB total import limit."));
                    break;
                }

                var content = await File.ReadAllTextAsync(path, cancellationToken);
                var parsed = ParseVncProfile(content, Path.GetFileNameWithoutExtension(path));
                parsedRows.Add((recordNumber, parsed.Computer, parsed.Error));
                notices[recordNumber] = parsed.Notice;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                parsedRows.Add((recordNumber, null, $"Could not read the VNC profile ({exception.GetType().Name})."));
            }
        }

        var plan = await BuildPlanAsync(parsedRows, cancellationToken);
        return WithNotices(plan, notices);
    }

    public async Task<ComputerImportPlan> PlanImportFilesAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var paths = filePaths.ToArray();
        if (paths.Length == 0)
        {
            return FileError("Select at least one connection file.");
        }
        if (paths.Length > MaximumVncFileCount)
        {
            return FileError($"A maximum of {MaximumVncFileCount} connection files can be imported at once.");
        }

        var rows = new List<(int Number, SavedComputer? Computer, string? Error)>();
        var notices = new Dictionary<int, string?>();
        long totalBytes = 0;
        var recordNumber = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists)
                {
                    recordNumber++;
                    rows.Add((recordNumber, null, "The selected connection file does not exist."));
                    continue;
                }

                var extension = Path.GetExtension(path);
                var isVnc = string.Equals(extension, ".vnc", StringComparison.OrdinalIgnoreCase);
                var isCsv = string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase);
                var isJson = string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".lucyvnc", StringComparison.OrdinalIgnoreCase);
                if (!isVnc && !isCsv && !isJson)
                {
                    recordNumber++;
                    rows.Add((recordNumber, null, "Supported file types are .vnc, .json, and .csv."));
                    continue;
                }

                var sizeLimit = string.Equals(extension, ".vnc", StringComparison.OrdinalIgnoreCase)
                    ? MaximumVncFileBytes
                    : MaximumImportFileBytes;
                if (file.Length > sizeLimit)
                {
                    recordNumber++;
                    rows.Add((recordNumber, null, $"The selected file exceeds the {sizeLimit / (1024 * 1024)} MB size limit."));
                    continue;
                }
                totalBytes += file.Length;
                if (totalBytes > MaximumImportFileBytes)
                {
                    recordNumber++;
                    rows.Add((recordNumber, null, "The selected files exceed the 10 MB total import limit."));
                    break;
                }

                var content = await File.ReadAllTextAsync(path, cancellationToken);
                if (isVnc)
                {
                    var parsed = ParseVncProfile(content, Path.GetFileNameWithoutExtension(path));
                    recordNumber++;
                    rows.Add((recordNumber, parsed.Computer, parsed.Error));
                    notices[recordNumber] = parsed.Notice;
                }
                else
                {
                    var filePlan = isCsv
                        ? await PlanCsvImportAsync(content, cancellationToken)
                        : await PlanJsonImportAsync(content, cancellationToken);
                    foreach (var entry in filePlan.Entries)
                    {
                        recordNumber++;
                        rows.Add((
                            recordNumber,
                            entry.Computer,
                            entry.Computer is null && entry.Status == ComputerImportStatus.Invalid ? entry.Message : null));
                        notices[recordNumber] = entry.Notice;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                recordNumber++;
                rows.Add((recordNumber, null, $"Could not read the selected connection file ({exception.GetType().Name})."));
            }
        }

        var plan = await BuildPlanAsync(rows, cancellationToken);
        return WithNotices(plan, notices);
    }

    public Task<ComputerImportPlan> PlanVncFolderImportAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        if (!Directory.Exists(folderPath))
        {
            return Task.FromResult(FileError("The selected folder does not exist."));
        }

        try
        {
            var files = Directory.EnumerateFiles(folderPath, "*.vnc", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumVncFileCount + 1)
                .ToArray();
            if (files.Length == 0)
            {
                return Task.FromResult(FileError("The selected folder contains no .vnc profiles."));
            }

            return PlanVncFilesImportAsync(files, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(FileError($"Could not enumerate the selected folder ({exception.GetType().Name})."));
        }
    }

    public async Task<string> ExportVncAsync(Guid computerId, CancellationToken cancellationToken = default)
    {
        var computer = await _store.GetComputerAsync(computerId, cancellationToken)
            ?? throw new InvalidOperationException("The selected computer no longer exists.");
        return ExportVnc(computer);
    }

    public async Task<ComputerImportApplyReport> ApplyImportAsync(
        ComputerImportPlan plan,
        ImportConflictPolicy conflictPolicy = ImportConflictPolicy.Ignore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!Enum.IsDefined(conflictPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(conflictPolicy));
        }

        var creates = new List<SavedComputer>();
        var updates = new List<SavedComputer>();
        var ignored = 0;
        var updatedTargets = new HashSet<Guid>();
        foreach (var entry in plan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Status == ComputerImportStatus.Invalid ||
                entry.Status == ComputerImportStatus.Duplicate ||
                entry.Computer is null)
            {
                continue;
            }

            if (entry.Status == ComputerImportStatus.Ready)
            {
                creates.Add(entry.Computer with { Id = Guid.NewGuid(), CredentialTarget = null });
                continue;
            }

            switch (conflictPolicy)
            {
                case ImportConflictPolicy.Ignore:
                    ignored++;
                    break;
                case ImportConflictPolicy.Create:
                    creates.Add(entry.Computer with { Id = Guid.NewGuid(), CredentialTarget = null });
                    break;
                case ImportConflictPolicy.Update when entry.ExistingMatches.Count == 1:
                    var existing = entry.ExistingMatches[0];
                    if (updatedTargets.Add(existing.Id))
                    {
                        updates.Add(entry.Computer with { Id = existing.Id, CredentialTarget = null });
                    }
                    else
                    {
                        ignored++;
                    }

                    break;
                case ImportConflictPolicy.Update:
                    ignored++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(conflictPolicy));
            }
        }

        await _store.ApplyComputerImportAsync(creates, updates, cancellationToken);
        return new ComputerImportApplyReport(creates.Count, updates.Count, ignored);
    }

    public async Task<string> ExportAllJsonAsync(CancellationToken cancellationToken = default) =>
        ExportJson(await _store.GetComputersAsync(cancellationToken: cancellationToken));

    public async Task<string> ExportSelectedJsonAsync(
        IEnumerable<Guid> selectedIds,
        CancellationToken cancellationToken = default) =>
        ExportJson(await GetSelectedAsync(selectedIds, cancellationToken));

    public async Task<string> ExportFavoritesJsonAsync(CancellationToken cancellationToken = default) =>
        ExportJson(await _store.GetComputersAsync(favoritesOnly: true, cancellationToken: cancellationToken));

    public async Task<string> ExportAllCsvAsync(CancellationToken cancellationToken = default) =>
        ExportCsv(await _store.GetComputersAsync(cancellationToken: cancellationToken));

    public async Task<string> ExportSelectedCsvAsync(
        IEnumerable<Guid> selectedIds,
        CancellationToken cancellationToken = default) =>
        ExportCsv(await GetSelectedAsync(selectedIds, cancellationToken));

    public async Task<string> ExportFavoritesCsvAsync(CancellationToken cancellationToken = default) =>
        ExportCsv(await _store.GetComputersAsync(favoritesOnly: true, cancellationToken: cancellationToken));

    public static string ExportJson(IEnumerable<SavedComputer> computers)
    {
        ArgumentNullException.ThrowIfNull(computers);
        var payload = new NativeDocument(
            NativeSchemaVersion,
            computers.Select(ToTransferRecord).ToArray());
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public static string ExportVnc(SavedComputer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        var host = computer.ConnectionHost;
        var displayHost = IPAddress.TryParse(host, out var address) &&
            address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? $"[{host}]"
                : host;
        if (ContainsLineBreakOrControl(computer.Name) || ContainsLineBreakOrControl(displayHost))
        {
            throw new InvalidDataException("This connection contains values that cannot be represented safely in a VNC profile.");
        }

        return string.Join("\r\n",
            "[Connection]",
            $"Host={displayHost}",
            $"Port={computer.Port.ToString(CultureInfo.InvariantCulture)}",
            $"ConnectionName={computer.Name}",
            string.Empty);
    }

    private static (SavedComputer? Computer, string? Error, string? Notice) ParseVncProfile(string content, string fallbackName)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unsupported = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var credentialFound = false;
        using var reader = new StringReader(content.TrimStart('\uFEFF'));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#') ||
                trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                unsupported.Add("malformed line");
                continue;
            }

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim().Trim('"');
            if (key.Length == 0 || key.Length > 128)
            {
                unsupported.Add("invalid key");
                continue;
            }

            if (key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("passwd", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("credential", StringComparison.OrdinalIgnoreCase))
            {
                credentialFound = true;
                continue;
            }

            if (!values.TryAdd(key, value))
            {
                return (null, $"The VNC profile contains duplicate '{key}' options.", null);
            }
        }

        var host = ReadVncValue(values, "Host");
        var explicitPort = ReadVncValue(values, "Port");
        if (string.IsNullOrWhiteSpace(host))
        {
            return (null, "The VNC profile must contain a Host option.", null);
        }

        if (!TryParseVncEndpoint(host, explicitPort, out var hostname, out var ipAddress, out var port))
        {
            return (null, "The VNC profile Host or Port is invalid. Supported hosts are DNS names or IP addresses.", null);
        }

        var name = ReadVncValue(values, "ConnectionName") ??
            ReadVncValue(values, "Name") ??
            fallbackName;
        var group = ReadVncValue(values, "Group");
        var candidate = CreateCandidate(name, hostname, ipAddress, port, group, favorite: false, viewPreference: "fit");
        if (candidate.Computer is null)
        {
            return (null, candidate.Error ?? "The VNC profile is invalid.", null);
        }

        var supportedKeys = new HashSet<string>(
            ["Host", "Port", "ConnectionName", "Name", "Group"],
            StringComparer.OrdinalIgnoreCase);
        foreach (var key in values.Keys.Where(key => !supportedKeys.Contains(key)))
        {
            unsupported.Add(key);
        }

        var notices = new List<string>();
        if (credentialFound)
        {
            notices.Add("Credential option detected and ignored.");
        }
        if (unsupported.Count > 0)
        {
            notices.Add($"Unsupported options ignored: {string.Join(", ", unsupported)}.");
        }

        return (candidate.Computer, null, notices.Count == 0 ? null : string.Join(" ", notices));
    }

    private static bool TryParseVncEndpoint(
        string value,
        string? explicitPort,
        out string? hostname,
        out string? ipAddress,
        out int port)
    {
        hostname = null;
        ipAddress = null;
        port = 5900;
        value = value.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        if (value[0] == '[')
        {
            var closingBracket = value.IndexOf(']');
            if (closingBracket <= 1)
            {
                return false;
            }

            var hostPart = value[1..closingBracket];
            var suffix = value[(closingBracket + 1)..];
            if (suffix.Length > 0)
            {
                if (!suffix.StartsWith("::", StringComparison.Ordinal) ||
                    !int.TryParse(suffix[2..], NumberStyles.None, CultureInfo.InvariantCulture, out port))
                {
                    return false;
                }
            }
            value = hostPart;
        }
        else if (!IPAddress.TryParse(value, out _))
        {
            var doubleSeparator = value.LastIndexOf("::", StringComparison.Ordinal);
            if (doubleSeparator > 0)
            {
                var suffix = value[(doubleSeparator + 2)..];
                if (int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var tcpPort))
                {
                    value = value[..doubleSeparator];
                    if (explicitPort is null)
                    {
                        port = tcpPort;
                    }
                }
            }
            else if (value.Count(character => character == ':') == 1)
            {
                var separator = value.LastIndexOf(':');
                var suffix = value[(separator + 1)..];
                if (int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var display))
                {
                    if (explicitPort is not null)
                    {
                        value = value[..separator];
                    }
                    else if (display is >= 0 and <= 99)
                    {
                        port = 5900 + display;
                        value = value[..separator];
                    }
                    else if (display is >= 100 and <= 65535)
                    {
                        port = display;
                        value = value[..separator];
                    }
                }
            }
        }

        if (explicitPort is not null &&
            !int.TryParse(explicitPort, NumberStyles.None, CultureInfo.InvariantCulture, out port))
        {
            return false;
        }
        if (port is < 1 or > 65535)
        {
            return false;
        }

        value = value.Trim();
        if (IPAddress.TryParse(value, out var parsedAddress))
        {
            if (parsedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
                parsedAddress.IsIPv4MappedToIPv6)
            {
                parsedAddress = parsedAddress.MapToIPv4();
            }
            if (parsedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                ipAddress = parsedAddress.ToString();
            }
            else
            {
                ipAddress = parsedAddress.ToString();
            }
            return true;
        }

        if (!IsValidHost(value))
        {
            return false;
        }
        hostname = value;
        return true;
    }

    private static string? ReadVncValue(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static bool ContainsLineBreakOrControl(string value) =>
        value.Any(character => char.IsControl(character));

    private static ComputerImportPlan WithNotices(
        ComputerImportPlan plan,
        IReadOnlyDictionary<int, string?> notices) =>
        new(plan.Entries.Select(entry =>
            notices.TryGetValue(entry.RecordNumber, out var notice)
                ? entry with { Notice = notice }
                : entry).ToArray());

    public static string ExportCsv(IEnumerable<SavedComputer> computers)
    {
        ArgumentNullException.ThrowIfNull(computers);
        var builder = new StringBuilder();
        AppendCsvRecord(builder, CsvHeaders);
        foreach (var computer in computers)
        {
            AppendCsvRecord(builder,
            [
                computer.Name,
                computer.Hostname ?? string.Empty,
                computer.IpAddress ?? string.Empty,
                computer.Port.ToString(CultureInfo.InvariantCulture),
                computer.Group ?? string.Empty,
                computer.IsFavorite ? "true" : "false",
                computer.ViewPreference,
            ]);
        }

        return builder.ToString();
    }

    private async Task<IReadOnlyList<SavedComputer>> GetSelectedAsync(
        IEnumerable<Guid> selectedIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectedIds);
        var ids = selectedIds.ToHashSet();
        var computers = await _store.GetComputersAsync(cancellationToken: cancellationToken);
        return computers.Where(computer => ids.Contains(computer.Id)).ToArray();
    }

    private async Task<ComputerImportPlan> BuildPlanAsync(
        IReadOnlyList<(int Number, SavedComputer? Computer, string? Error)> rows,
        CancellationToken cancellationToken)
    {
        var existing = await _store.GetComputersAsync(cancellationToken: cancellationToken);
        var entries = new List<ComputerImportEntry>(rows.Count);
        var seenEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimedExisting = new HashSet<Guid>();

        foreach (var (number, computer, error) in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (error is not null || computer is null)
            {
                entries.Add(new ComputerImportEntry(number, ComputerImportStatus.Invalid, error ?? "Invalid record.", null, [], ComputerImportAction.Ignore));
                continue;
            }

            var aliases = EndpointAliases(computer);
            if (aliases.Any(seenEndpoints.Contains))
            {
                entries.Add(new ComputerImportEntry(number, ComputerImportStatus.Duplicate, "This endpoint duplicates an earlier import record.", computer, [], ComputerImportAction.Ignore));
                continue;
            }

            foreach (var alias in aliases)
            {
                seenEndpoints.Add(alias);
            }

            var matches = existing
                .Where(saved => EndpointAliases(saved).Intersect(aliases, StringComparer.OrdinalIgnoreCase).Any())
                .ToArray();
            if (matches.Length == 0)
            {
                entries.Add(new ComputerImportEntry(number, ComputerImportStatus.Ready, null, computer, [], ComputerImportAction.Create));
                continue;
            }

            if (matches.All(match => claimedExisting.Contains(match.Id)))
            {
                entries.Add(new ComputerImportEntry(number, ComputerImportStatus.Duplicate, "This endpoint aliases a computer already matched by an earlier import record.", computer, matches, ComputerImportAction.Ignore));
                continue;
            }

            foreach (var match in matches)
            {
                claimedExisting.Add(match.Id);
            }

            entries.Add(new ComputerImportEntry(number, ComputerImportStatus.Conflict, "This endpoint already exists in the local computer list.", computer, matches, ComputerImportAction.Ignore));
        }

        return new ComputerImportPlan(entries);
    }

    private static List<(int Number, SavedComputer? Computer, string? Error)> ParseJsonDocument(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The native JSON root must be an object.");
        }

        var rootProperties = ReadProperties(root, ["schemaVersion", "computers"]);
        if (!rootProperties.TryGetValue("schemaVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var schemaVersion) ||
            schemaVersion != NativeSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported or missing native JSON schemaVersion; expected {NativeSchemaVersion}.");
        }

        if (!rootProperties.TryGetValue("computers", out var computers) ||
            computers.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The native JSON 'computers' field must be an array.");
        }

        var rows = new List<(int, SavedComputer?, string?)>();
        var number = 0;
        foreach (var item in computers.EnumerateArray())
        {
            number++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                rows.Add((number, null, "Each computer must be a JSON object."));
                continue;
            }

            try
            {
                var properties = ReadProperties(item, ["name", "hostname", "ipAddress", "port", "group", "isFavorite", "viewPreference"]);
                var name = RequiredString(properties, "name");
                var port = RequiredInt(properties, "port");
                var hostname = OptionalString(properties, "hostname");
                var ipAddress = OptionalString(properties, "ipAddress");
                var group = OptionalString(properties, "group");
                var favorite = OptionalBoolean(properties, "isFavorite");
                var viewPreference = OptionalString(properties, "viewPreference");
                var candidate = CreateCandidate(name, hostname, ipAddress, port, group, favorite, viewPreference);
                rows.Add((number, candidate.Computer, candidate.Error));
            }
            catch (InvalidDataException exception)
            {
                rows.Add((number, null, exception.Message));
            }
        }

        if (number == 0)
        {
            rows.Add((1, null, "The native JSON file contains no computer records."));
        }

        return rows;
    }

    private static Dictionary<string, JsonElement> ReadProperties(JsonElement element, IReadOnlyCollection<string> allowed)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Unknown or unsupported field '{property.Name}'. Secret fields are never imported or exported.");
            }

            if (!properties.TryAdd(property.Name, property.Value))
            {
                throw new InvalidDataException($"Duplicate JSON field '{property.Name}'.");
            }
        }

        return properties;
    }

    private static string RequiredString(IReadOnlyDictionary<string, JsonElement> properties, string name)
    {
        if (!properties.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Required field '{name}' must be a string.");
        }

        return value.GetString() ?? string.Empty;
    }

    private static string? OptionalString(IReadOnlyDictionary<string, JsonElement> properties, string name)
    {
        if (!properties.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Field '{name}' must be a string or null.");
        }

        return value.GetString();
    }

    private static int RequiredInt(IReadOnlyDictionary<string, JsonElement> properties, string name)
    {
        if (!properties.TryGetValue(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var number))
        {
            throw new InvalidDataException($"Required field '{name}' must be an integer.");
        }

        return number;
    }

    private static bool OptionalBoolean(IReadOnlyDictionary<string, JsonElement> properties, string name)
    {
        if (!properties.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"Field '{name}' must be a boolean."),
        };
    }

    private static (int Number, SavedComputer? Computer, string? Error) CreateCandidate(
        string? name,
        string? hostname,
        string? ipAddress,
        int port,
        string? group,
        bool favorite,
        string? viewPreference)
    {
        name = name?.Trim();
        hostname = NormalizeOptional(hostname);
        ipAddress = NormalizeOptional(ipAddress);
        group = NormalizeOptional(group);
        viewPreference = string.IsNullOrWhiteSpace(viewPreference) ? "fit" : viewPreference.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256)
        {
            return (0, null, "Name is required and must be at most 256 characters.");
        }

        if (hostname is null && ipAddress is null)
        {
            return (0, null, "Provide at least one hostname or IP address.");
        }

        if ((hostname is not null && !IsValidHost(hostname)) ||
            (ipAddress is not null && !IPAddress.TryParse(ipAddress, out _)))
        {
            return (0, null, "Hostname or IP address is invalid.");
        }

        if (port is < 1 or > 65535)
        {
            return (0, null, "Port must be between 1 and 65535.");
        }

        if (viewPreference is not ("fit" or "100%"))
        {
            return (0, null, "viewPreference must be 'fit' or '100%'.");
        }

        return (0, new SavedComputer(
            Guid.NewGuid(), name, hostname, ipAddress, port, group, favorite, null, null, viewPreference), null);
    }

    private static bool IsValidHost(string value)
    {
        if (value.Length > 253 || value.Any(char.IsWhiteSpace) ||
            value.Contains('/') || value.Contains('\\') || value.Contains(':') && !IPAddress.TryParse(value, out _))
        {
            return false;
        }

        if (IPAddress.TryParse(value, out _))
        {
            return true;
        }

        var host = value.EndsWith(".", StringComparison.Ordinal) ? value[..^1] : value;
        if (host.Length == 0 || (host.All(character => char.IsAsciiDigit(character) || character == '.') && host.Contains('.')))
        {
            return false;
        }

        return host.Split('.').All(label =>
            label.Length is > 0 and <= 63 &&
            char.IsAsciiLetterOrDigit(label[0]) &&
            char.IsAsciiLetterOrDigit(label[^1]) &&
            label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
    }

    private static IEnumerable<string> EndpointAliases(SavedComputer computer)
    {
        var hosts = new[] { computer.Hostname, computer.IpAddress };
        return hosts.Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(host => $"{NormalizeHost(host!)}:{computer.Port}");
    }

    private static string NormalizeHost(string host)
    {
        host = host.Trim();
        if (IPAddress.TryParse(host, out var address))
        {
            return address.ToString().ToLowerInvariant();
        }

        return host.TrimEnd('.').ToLowerInvariant();
    }

    private static ComputerTransferRecord ToTransferRecord(SavedComputer computer) => new(
        computer.Name,
        computer.Hostname,
        computer.IpAddress,
        computer.Port,
        computer.Group,
        computer.IsFavorite,
        computer.ViewPreference);

    private static ComputerImportPlan FileError(string message) =>
        new([new ComputerImportEntry(1, ComputerImportStatus.Invalid, message, null, [], ComputerImportAction.Ignore)]);

    private static bool TryParseBoolean(string value, out bool result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = false;
            return true;
        }

        return bool.TryParse(value, out result);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<IReadOnlyList<string>> ParseCsv(string csv)
    {
        var records = new List<IReadOnlyList<string>>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var afterQuote = false;
        for (var index = 0; index < csv.Length; index++)
        {
            var character = csv[index];
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < csv.Length && csv[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                        afterQuote = true;
                    }
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            if (afterQuote && character is not (',' or '\r' or '\n'))
            {
                throw new InvalidDataException("Unexpected character after a quoted CSV field.");
            }

            if (character == '"' && field.Length == 0 && !afterQuote)
            {
                inQuotes = true;
            }
            else if (character == '"')
            {
                throw new InvalidDataException("A quoted CSV field must begin at the start of a field.");
            }
            else if (character == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
                afterQuote = false;
            }
            else if (character is '\r' or '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                records.Add(fields);
                fields = [];
                afterQuote = false;
                if (character == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n')
                {
                    index++;
                }
            }
            else
            {
                field.Append(character);
            }
        }

        if (inQuotes)
        {
            throw new InvalidDataException("The CSV file ends inside a quoted field.");
        }

        if (field.Length > 0 || fields.Count > 0 || afterQuote)
        {
            fields.Add(field.ToString());
            records.Add(fields);
        }

        return records;
    }

    private static void AppendCsvRecord(StringBuilder builder, IEnumerable<string> values)
    {
        builder.AppendJoin(',', values.Select(EscapeCsv));
        builder.Append("\r\n");
    }

    private static string EscapeCsv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;

    private sealed record NativeDocument(int SchemaVersion, IReadOnlyList<ComputerTransferRecord> Computers);

    private sealed record ComputerTransferRecord(
        string Name,
        string? Hostname,
        string? IpAddress,
        int Port,
        string? Group,
        bool IsFavorite,
        string ViewPreference);
}
