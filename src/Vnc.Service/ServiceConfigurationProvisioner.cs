using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text;

namespace Vnc.Service;

public sealed record ServiceConfigurationRequest(ServiceConfiguration Configuration, string Password);

public static class ServiceConfigurationProvisioner
{
    private const int CryptProtectLocalMachine = 0x4;
    private const int CryptProtectUiForbidden = 0x1;
    private const string CredentialFileName = "vnc-credential.bin";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string WriteProtectedRequest(ServiceConfigurationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Configuration is null)
        {
            throw new InvalidDataException("The service configuration request has no settings.");
        }

        request.Configuration.Validate();
        ValidatePassword(request.Password);

        var directory = Path.Combine(Path.GetTempPath(), $"LucyVNC-Setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        ApplyRequestDirectoryAcl(directory);

        var path = Path.Combine(directory, "request.bin");
        byte[]? json = null;
        byte[]? protectedBytes = null;
        try
        {
            json = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
            protectedBytes = ProtectMachine(json);
            File.WriteAllBytes(path, protectedBytes);
            ApplyRequestFileAcl(path);
            return path;
        }
        catch
        {
            DeleteProtectedRequest(path);
            throw;
        }
        finally
        {
            if (json is not null)
            {
                CryptographicOperations.ZeroMemory(json);
            }

            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    public static bool ApplyProtectedRequest(string requestPath)
    {
        var fullPath = ValidateProtectedRequestPath(requestPath);
        var directory = Path.GetDirectoryName(fullPath)!;

        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("The protected setup request directory cannot be a reparse point.");
        }

        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("The service configuration request cannot be a reparse point.");
        }

        var protectedBytes = File.ReadAllBytes(fullPath);
        byte[]? json = null;
        try
        {
            json = UnprotectMachine(protectedBytes);
            var request = JsonSerializer.Deserialize<ServiceConfigurationRequest>(json, JsonOptions)
                ?? throw new InvalidDataException("The protected service configuration request is empty.");
            if (request.Configuration is null)
            {
                throw new InvalidDataException("The protected service configuration has no settings.");
            }

            request.Configuration.Validate();
            ValidatePassword(request.Password);

            var configurationStore = new ServiceConfigurationStore();
            configurationStore.Save(request.Configuration with { RemoteSharingEnabled = false });
            var credentialStore = new ServiceCredentialStore();
            credentialStore.Save(request.Password);
            configurationStore.Save(request.Configuration);
            return WindowsServiceStatusProbe.RestartIfRunning(request.Configuration.StartAutomatically);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (json is not null)
            {
                CryptographicOperations.ZeroMemory(json);
            }

            DeleteProtectedRequest(fullPath);
        }
    }

    public static void DeleteProtectedRequest(string requestPath)
    {
        var fullPath = ValidateProtectedRequestPath(requestPath);
        var directory = Path.GetDirectoryName(fullPath)!;

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory);
        }
    }

    internal static string GetCredentialPath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LucyVNC",
            "Service");
        return Path.Combine(directory, CredentialFileName);
    }

    internal static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length != 8 || password.Any(character => character > 0x7f))
        {
            throw new InvalidDataException("The service password must contain exactly eight ASCII characters.");
        }
    }

    private static string ValidateProtectedRequestPath(string requestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestPath);
        var fullPath = Path.GetFullPath(requestPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new UnauthorizedAccessException("The protected setup request has no parent directory.");
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(directory);
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var directoryName = Path.GetFileName(normalizedDirectory);
        if (!string.Equals(Path.GetDirectoryName(normalizedDirectory), tempRoot, StringComparison.OrdinalIgnoreCase) ||
            !directoryName.StartsWith("LucyVNC-Setup-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(directoryName["LucyVNC-Setup-".Length..], "N", out _) ||
            !string.Equals(Path.GetFileName(fullPath), "request.bin", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("The protected setup request is outside its permitted temporary location.");
        }

        return fullPath;
    }

    private static void ApplyRequestDirectoryAcl(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddRequestAccessRules(security, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void ApplyRequestFileAcl(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddRequestAccessRules(security, InheritanceFlags.None);
        new FileInfo(path).SetAccessControl(security);
    }

    private static void AddRequestAccessRules(FileSystemSecurity security, InheritanceFlags inheritance)
    {
        var currentSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not resolve the current Windows user.");
        security.AddAccessRule(new FileSystemAccessRule(
            currentSid,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
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
    }

    internal static byte[] ProtectMachine(byte[] bytes)
    {
        var input = new DataBlob(bytes);
        try
        {
            if (!CryptProtectData(ref input, "LucyVNC service configuration", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectLocalMachine | CryptProtectUiForbidden, out var output))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            return CopyAndFree(output);
        }
        finally
        {
            var cleared = new byte[input.Size];
            Marshal.Copy(cleared, 0, input.Data, cleared.Length);
            Marshal.FreeHGlobal(input.Data);
            CryptographicOperations.ZeroMemory(cleared);
        }
    }

    internal static byte[] UnprotectMachine(byte[] bytes)
    {
        var input = new DataBlob(bytes);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var output))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            return CopyAndFree(output);
        }
        finally
        {
            var cleared = new byte[input.Size];
            Marshal.Copy(cleared, 0, input.Data, cleared.Length);
            Marshal.FreeHGlobal(input.Data);
            CryptographicOperations.ZeroMemory(cleared);
        }
    }

    internal static byte[] CopyAndFree(DataBlob blob)
    {
        try
        {
            var bytes = new byte[blob.Size];
            Marshal.Copy(blob.Data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            if (blob.Data != IntPtr.Zero)
            {
                var cleared = new byte[blob.Size];
                Marshal.Copy(cleared, 0, blob.Data, cleared.Length);
                CryptographicOperations.ZeroMemory(cleared);
                _ = LocalFree(blob.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DataBlob
    {
        public int Size;
        public IntPtr Data;

        public DataBlob(byte[] bytes)
        {
            Size = bytes.Length;
            Data = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, Data, bytes.Length);
        }
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptProtectData(
        ref DataBlob input,
        string description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptUnprotectData(
        ref DataBlob input,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr memory);
}

public sealed class ServiceCredentialStore
{
    private readonly string? _credentialPath;

    public ServiceCredentialStore(string? credentialPath = null)
    {
        _credentialPath = string.IsNullOrWhiteSpace(credentialPath)
            ? null
            : Path.GetFullPath(credentialPath);
    }

    public void Save(string password)
    {
        ServiceConfigurationProvisioner.ValidatePassword(password);
        var path = GetCredentialPath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var plaintextPassword = Encoding.ASCII.GetBytes(password);
        byte[] protectedPassword;
        try
        {
            protectedPassword = Protect(plaintextPassword);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextPassword);
        }

        try
        {
            var temporaryPath = Path.Combine(directory, $"credential.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(protectedPassword);
                    stream.Flush(flushToDisk: true);
                }

                ApplyMachineAcl(temporaryPath);
                File.Move(temporaryPath, path, overwrite: true);
                ApplyMachineAcl(path);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedPassword);
        }
    }

    public string Load()
    {
        var path = GetCredentialPath();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("No protected LucyVNC Server credential is configured.", path);
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("The service credential cannot be a reparse point.");
        }

        var protectedPassword = File.ReadAllBytes(path);
        try
        {
            var passwordBytes = Unprotect(protectedPassword);
            try
            {
                var password = Encoding.ASCII.GetString(passwordBytes);
                ServiceConfigurationProvisioner.ValidatePassword(password);
                return password;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedPassword);
        }
    }

    public void Delete()
    {
        var path = GetCredentialPath();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetCredentialPath() =>
        _credentialPath ?? ServiceConfigurationProvisioner.GetCredentialPath();

    private static byte[] Protect(byte[] bytes)
    {
        return ServiceConfigurationProvisioner.ProtectMachine(bytes);
    }

    private static byte[] Unprotect(byte[] bytes)
    {
        return ServiceConfigurationProvisioner.UnprotectMachine(bytes);
    }

    private static void ApplyMachineAcl(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[]
                 {
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        new FileInfo(path).SetAccessControl(security);
    }
}
