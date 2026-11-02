using Vnc.Service;

namespace Vnc.Service.Tests;

public class ServiceWorkerTests
{
    [Fact]
    public void ResolveVncPasswordAcceptsExactlyEightAsciiCharacters()
    {
        Environment.SetEnvironmentVariable(ServiceWorker.VncPasswordEnvironmentVariable, "password", EnvironmentVariableTarget.Process);
        try
        {
            Assert.Equal("password", ServiceWorker.ResolveVncPassword());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ServiceWorker.VncPasswordEnvironmentVariable, null, EnvironmentVariableTarget.Process);
        }
    }

    [Fact]
    public void ResolveVncPasswordRejectsMissingOrInvalidValues()
    {
        Environment.SetEnvironmentVariable(ServiceWorker.VncPasswordEnvironmentVariable, string.Empty, EnvironmentVariableTarget.Process);
        Assert.Null(ServiceWorker.ResolveVncPassword());

        Environment.SetEnvironmentVariable(ServiceWorker.VncPasswordEnvironmentVariable, "short", EnvironmentVariableTarget.Process);
        Assert.Null(ServiceWorker.ResolveVncPassword());

        Environment.SetEnvironmentVariable(ServiceWorker.VncPasswordEnvironmentVariable, "123456789", EnvironmentVariableTarget.Process);
        Assert.Null(ServiceWorker.ResolveVncPassword());
    }
}
