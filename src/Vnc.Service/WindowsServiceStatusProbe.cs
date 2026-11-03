using System.Diagnostics;
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
                    ServiceRunning => "Worker is running. SCM state alone does not confirm an RFB listener; verify the protected configuration, interactive Agent identity, and service logs.",
                    ServiceStartPending => "Worker is starting. Sharing is not confirmed until the interactive-session agent is authenticated.",
                    ServiceStopPending => "Service is stopping. No interactive-session screen access is supported.",
                    ServiceStopped => "Service is installed but stopped. No service listener is active.",
                    _ => $"Service is installed (SCM state {status.CurrentState}). Sharing requires an authenticated interactive-session agent.",
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

    public static bool RestartIfRunning()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("LucyServer service control is available only on Windows.");
        }

        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not open Service Control Manager.");
        }

        try
        {
            var service = OpenService(manager, ServiceName, ServiceQueryStatus | ServiceStart | ServiceStop);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorServiceDoesNotExist)
                {
                    return false;
                }

                throw new System.ComponentModel.Win32Exception(error, "Could not open LucyServer for restart.");
            }

            try
            {
                if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not query LucyServer state.");
                }

                if (status.CurrentState != ServiceRunning)
                {
                    return false;
                }

                if (!ControlService(service, ServiceControlStop, out _))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorServiceNotActive)
                    {
                        throw new System.ComponentModel.Win32Exception(error, "Could not stop LucyServer to apply its saved configuration.");
                    }
                }

                WaitForServiceState(service, ServiceStopped, TimeSpan.FromSeconds(30));
                if (!StartService(service, 0, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorServiceAlreadyRunning)
                    {
                        throw new System.ComponentModel.Win32Exception(error, "Could not restart LucyServer with the saved configuration.");
                    }
                }

                WaitForServiceState(service, ServiceRunning, TimeSpan.FromSeconds(30));
                return true;
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

    private static void WaitForServiceState(IntPtr service, uint expectedState, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not query LucyServer while waiting for a state change.");
            }

            if (status.CurrentState == expectedState)
            {
                return;
            }

            if (expectedState == ServiceRunning && status.CurrentState == ServiceStopped)
            {
                throw new InvalidOperationException($"LucyServer stopped during restart (Windows exit code {status.Win32ExitCode}, service exit code {status.ServiceSpecificExitCode}).");
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException($"LucyServer did not reach SCM state {expectedState} within {timeout}.");
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceStopped = 1;
    private const uint ServiceStartPending = 2;
    private const uint ServiceStopPending = 3;
    private const uint ServiceRunning = 4;
    private const uint ServiceControlStop = 1;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorServiceAlreadyRunning = 1056;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(IntPtr service, uint argumentCount, IntPtr arguments);

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
