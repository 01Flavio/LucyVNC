using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Vnc.Service;

public sealed class ServiceConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _directoryPath;

    public ServiceConfigurationStore(string? directoryPath = null)
    {
        _directoryPath = directoryPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LucyVNC",
            "Service");
    }

    public string ConfigurationPath => Path.Combine(_directoryPath, "service.json");

    public ServiceConfiguration LoadOrCreate()
    {
        EnsureSecureDirectory();
        if (!File.Exists(ConfigurationPath))
        {
            Save(ServiceConfiguration.SafeDefaults);
        }

        if ((File.GetAttributes(ConfigurationPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("The service configuration file cannot be a reparse point.");
        }

        var configuration = JsonSerializer.Deserialize<ServiceConfiguration>(
            File.ReadAllText(ConfigurationPath),
            JsonOptions) ?? throw new InvalidDataException("The service configuration is empty.");
        configuration.Validate();
        return configuration;
    }

    public void Save(ServiceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        EnsureSecureDirectory();

        var temporaryPath = Path.Combine(_directoryPath, $"service.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, configuration, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            ApplyServiceAcl(temporaryPath, isDirectory: false);
            File.Move(temporaryPath, ConfigurationPath, overwrite: true);
            ApplyServiceAcl(ConfigurationPath, isDirectory: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void EnsureSecureDirectory()
    {
        Directory.CreateDirectory(_directoryPath);
        var directory = new DirectoryInfo(_directoryPath);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("The service configuration directory cannot be a reparse point.");
        }

        ApplyServiceAcl(_directoryPath, isDirectory: true);
    }

    private static void ApplyServiceAcl(string path, bool isDirectory)
    {
        var security = isDirectory
            ? (FileSystemSecurity)new DirectorySecurity()
            : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = isDirectory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));

        if (isDirectory)
        {
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        }
        else
        {
            new FileInfo(path).SetAccessControl((FileSecurity)security);
        }
    }
}
