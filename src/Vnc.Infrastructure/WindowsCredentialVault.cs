using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Vnc.Infrastructure;

public interface ICredentialVault
{
    string? Read(string target);
    void Write(string target, string secret);
    void Delete(string target);
}

public sealed class WindowsCredentialVault : ICredentialVault
{
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;
    private const int NotFound = 1168;

    public static string CreateViewerTarget(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var normalizedHost = host.Trim().TrimEnd('.');
        if (IPAddress.TryParse(normalizedHost, out var address))
        {
            normalizedHost = address.ToString();
        }
        else
        {
            normalizedHost = normalizedHost.ToLowerInvariant();
        }

        return $"LucyVNC/Viewer/v1/{port}/{normalizedHost}";
    }

    public static bool IsViewerTarget(string? target)
    {
        const string prefix = "LucyVNC/Viewer/v1/";
        if (target is null || !target.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var endpoint = target[prefix.Length..];
        var separator = endpoint.IndexOf('/');
        if (separator <= 0 ||
            !int.TryParse(endpoint[..separator], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            return false;
        }

        var host = endpoint[(separator + 1)..];
        return host.Length > 0 &&
               !host.Contains('/') &&
               !host.Contains('\\') &&
               string.Equals(target, CreateViewerTarget(host, port), StringComparison.Ordinal);
    }

    public string? Read(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!CredRead(target, GenericCredential, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NotFound)
            {
                return null;
            }

            throw new Win32Exception(error, "Windows Credential Manager could not read the LucyVNC credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }

            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize % sizeof(char) != 0)
            {
                throw new InvalidDataException("Windows Credential Manager returned an invalid LucyVNC credential.");
            }

            return Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)(credential.CredentialBlobSize / sizeof(char))));
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void Write(string target, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(secret);
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = target,
                CredentialBlobSize = checked((uint)bytes.Length),
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager could not save the LucyVNC credential.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            for (var index = 0; index < bytes.Length; index++)
            {
                Marshal.WriteByte(blob, index, 0);
            }

            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!CredDelete(target, GenericCredential, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != NotFound)
            {
                throw new Win32Exception(error, "Windows Credential Manager could not delete the LucyVNC credential.");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
