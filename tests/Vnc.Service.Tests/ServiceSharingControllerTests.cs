using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vnc.Service;

namespace Vnc.Service.Tests;

public sealed class ServiceSharingControllerTests
{
    [Fact]
    public void DefaultsDisableSharingAndBindToLoopback()
    {
        var configuration = ServiceConfiguration.SafeDefaults;

        Assert.False(configuration.RemoteSharingEnabled);
        Assert.Equal(IPAddress.Loopback.ToString(), configuration.BindAddress);
        Assert.Equal(5900, configuration.Port);
        Assert.True(configuration.AuthenticationRequired);
        Assert.Equal(1, configuration.MaxSessions);
        Assert.Equal(ServiceSharingState.Disabled, ServiceSharingController.Evaluate(configuration).State);
    }

    [Fact]
    public void ExplicitOptInStartsTheAuthenticatedListenerPolicy()
    {
        var result = ServiceSharingController.Evaluate(
            ServiceConfiguration.SafeDefaults with { RemoteSharingEnabled = true });

        Assert.Equal(ServiceSharingState.Enabled, result.State);
        Assert.Contains("authenticated RFB listener", result.Message);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.10.5", true)]
    [InlineData("8.8.8.8", false)]
    public void BindAddressPolicyIsLoopbackFirstAndRejectsPublicAddresses(string address, bool expected)
    {
        Assert.Equal(expected, ServiceConfiguration.IsAllowedBindAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public void ConfigurationRequiresAuthenticationAndSingleSession()
    {
        Assert.Throws<InvalidDataException>(() =>
            (ServiceConfiguration.SafeDefaults with { AuthenticationRequired = false }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (ServiceConfiguration.SafeDefaults with { MaxSessions = 2 }).Validate());
    }

    [Fact]
    public void ConfigurationRejectsWildcardAndInvalidPort()
    {
        Assert.Throws<InvalidDataException>(() =>
            (ServiceConfiguration.SafeDefaults with { BindAddress = "0.0.0.0" }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (ServiceConfiguration.SafeDefaults with { Port = 65536 }).Validate());
    }

    [Fact]
    public void WildcardBindRequiresExplicitConfirmationAndKeepsAuthenticationRequired()
    {
        var wildcard = ServiceConfiguration.SafeDefaults with
        {
            RemoteSharingEnabled = true,
            BindAddress = IPAddress.Any.ToString(),
        };
        Assert.False(ServiceConfiguration.IsAllowedBindAddress(IPAddress.Any));
        Assert.Throws<InvalidDataException>(wildcard.Validate);

        var confirmed = wildcard with { AllInterfacesConfirmed = true };
        Assert.True(ServiceConfiguration.IsAllowedBindAddress(IPAddress.Any, allowAllInterfaces: true));
        confirmed.Validate();
        Assert.True(confirmed.AuthenticationRequired);
    }

    [Fact]
    public void NetworkBindingReportsPortConflictsAndEnumeratesOnlyUsableIpv4Adapters()
    {
        using var owner = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        owner.Start();
        var occupiedPort = ((System.Net.IPEndPoint)owner.LocalEndpoint).Port;

        Assert.False(ServiceNetworkBinding.IsPortAvailable(IPAddress.Loopback, occupiedPort));
        owner.Stop();
        Assert.True(ServiceNetworkBinding.IsPortAvailable(IPAddress.Loopback, occupiedPort));
        Assert.All(ServiceNetworkBinding.GetAvailableIpv4Interfaces(), adapter =>
        {
            Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, adapter.Address.AddressFamily);
            Assert.True(ServiceConfiguration.IsAllowedBindAddress(adapter.Address));
            Assert.False(IPAddress.IsLoopback(adapter.Address));
            Assert.False(string.IsNullOrWhiteSpace(adapter.Name));
            Assert.False(string.IsNullOrWhiteSpace(adapter.Type));
        });
    }

    [Fact]
    public void ServiceHostRegistersWorkerWithoutStartingOrInstallingService()
    {
        using var host = ServiceHostBuilder.Create(Array.Empty<string>()).Build();

        Assert.Contains(host.Services.GetServices<IHostedService>(), service => service is BackgroundService);
    }
}
