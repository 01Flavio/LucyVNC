using System.Runtime.InteropServices;

namespace Vnc.Service;

public static class WindowsServiceStatusProbe
{
    public const string ServiceName = "LucyVNCService";

    public static string GetDiagnostic()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Windows service status is available only on Windows.";
        }

        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return $"Could not query Service Control Manager (Windows error {Marshal.GetLastWin32Error()}).";
        }

        try
        {
            var service = OpenService(manager, ServiceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                return error == ErrorServiceDoesNotExist
                    ? "Service is not installed. No service listener or firewall rule was created."
                    : $"Could not query service state (Windows error {error}).";
            }

            try
            {
                if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _))
                {
                    return $"Could not query service state (Windows error {Marshal.GetLastWin32Error()}).";
                }

                return status.CurrentState switch
                {
                    ServiceRunning => "Service is running. It still refuses remote sharing until a real interactive-session agent is attached.",
                    ServiceStartPending => "Service is starting. Remote sharing remains unavailable without an interactive-session agent.",
                    ServiceStopPending => "Service is stopping. No interactive-session screen access is supported.",
                    ServiceStopped => "Service is installed but stopped. No service listener is active.",
                    _ => $"Service is installed (SCM state {status.CurrentState}). No interactive-session screen access is supported.",
                };
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ErrorServiceDoesNotExist = 1060;
    private const uint ServiceStopped = 1;
    private const uint ServiceStartPending = 2;
    private const uint ServiceStopPending = 3;
    private const uint ServiceRunning = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        IntPtr service,
        int infoLevel,
        out ServiceStatusProcess status,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
