namespace Vnc.Service;

public enum ServiceSharingState
{
    Disabled,
    RefusedNoInteractiveAgent,
}

public sealed record ServiceSharingDiagnostic(ServiceSharingState State, string Message);

public static class ServiceSharingController
{
    public static ServiceSharingDiagnostic Evaluate(ServiceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();

        if (!configuration.RemoteSharingEnabled)
        {
            return new ServiceSharingDiagnostic(
                ServiceSharingState.Disabled,
                "Remote sharing is disabled. No listener was opened.");
        }

        return new ServiceSharingDiagnostic(
            ServiceSharingState.RefusedNoInteractiveAgent,
            "Remote sharing was explicitly requested but is unavailable: no authenticated interactive-session agent with restricted IPC is attached. No listener was opened. Session 0 does not capture the screen or inject input.");
    }
}
