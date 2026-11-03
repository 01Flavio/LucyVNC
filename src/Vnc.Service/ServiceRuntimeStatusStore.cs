using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Vnc.Service;

public sealed record ServiceRuntimeStatus(
    string State,
    bool AgentAuthenticated,
    bool ListenerActive,
    string? BindAddress,
    int? Port,
    DateTimeOffset UpdatedUtc,
    string Detail);

public sealed class ServiceRuntimeStatusStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _directoryPath;

    public ServiceRuntimeStatusStore(string statusPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statusPath);
        StatusPath = Path.GetFullPath(statusPath);
        _directoryPath = Path.GetDirectoryName(StatusPath)
            ?? throw new ArgumentException("The status path must include a directory.", nameof(statusPath));
    }

    public string StatusPath { get; }

    public void Write(ServiceRuntimeStatus status, string? readableUserSid)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(status.State);
        if (status.Port is < 1 or > 65535)
        {
            throw new InvalidDataException("Runtime status port is outside the TCP port range.");
        }

        Directory.CreateDirectory(_directoryPath);
        if ((File.GetAttributes(_directoryPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("The LucyVNC Server runtime status directory cannot be a reparse point.");
        }

        var temporaryPath = Path.Combine(_directoryPath, $"runtime.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, status, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            ApplyFileAcl(temporaryPath, readableUserSid);
            File.Move(temporaryPath, StatusPath, overwrite: true);
            ApplyFileAcl(StatusPath, readableUserSid);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public ServiceRuntimeStatus? Read()
    {
        if (!File.Exists(StatusPath))
        {
            return null;
        }

        if ((File.GetAttributes(StatusPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("The LucyVNC Server runtime status file cannot be a reparse point.");
        }

        return JsonSerializer.Deserialize<ServiceRuntimeStatus>(File.ReadAllText(StatusPath), JsonOptions)
            ?? throw new InvalidDataException("The LucyVNC Server runtime status file is empty.");
    }

    private static void ApplyFileAcl(string path, string? readableUserSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        if (!string.IsNullOrWhiteSpace(readableUserSid))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(readableUserSid),
                FileSystemRights.Read,
                AccessControlType.Allow));
        }

        new FileInfo(path).SetAccessControl(security);
    }
}
