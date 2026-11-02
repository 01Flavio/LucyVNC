namespace Vnc.Service;

public enum ServiceSharingState
{
    Disabled,
    Enabled,
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
            ServiceSharingState.Enabled,
            "Remote sharing is enabled. The worker opens the authenticated RFB listener on the configured address.");
    }
}
