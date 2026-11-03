using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Vnc.Service;

namespace Vnc.Service.Tests;

public sealed class ServiceConfigurationProvisionerTests
{
    [Fact]
    public void ProtectedConfigurationRequestRoundTripsWithoutPlaintextPasswordOnDisk()
    {
        var request = new ServiceConfigurationRequest(
            ServiceConfiguration.SafeDefaults with
            {
                RemoteSharingEnabled = true,
                AgentSid = WindowsIdentity.GetCurrent().User!.Value,
                AgentWorkerSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            },
            "testpass");
        var path = ServiceConfigurationProvisioner.WriteProtectedRequest(request);

        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            Assert.DoesNotContain("testpass", Encoding.UTF8.GetString(protectedBytes), StringComparison.Ordinal);

            var json = ServiceConfigurationProvisioner.UnprotectMachine(protectedBytes);
            try
            {
                var restored = JsonSerializer.Deserialize<ServiceConfigurationRequest>(json);
                Assert.NotNull(restored);
                Assert.Equal(request.Password, restored.Password);
                Assert.Equal(request.Configuration.AgentSid, restored.Configuration.AgentSid);
                Assert.True(restored.Configuration.RemoteSharingEnabled);
            }
            finally
            {
                Array.Clear(json);
            }
        }
        finally
        {
            ServiceConfigurationProvisioner.DeleteProtectedRequest(path);
        }
    }

    [Fact]
    public void InvalidServicePasswordIsRejectedBeforeRequestCreation()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            ServiceConfigurationProvisioner.WriteProtectedRequest(
                new ServiceConfigurationRequest(ServiceConfiguration.SafeDefaults, "short")));

        Assert.Contains("eight ASCII characters", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingServicePasswordIsRejectedBeforeRequestCreation()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            ServiceConfigurationProvisioner.ValidatePassword(null!));

        Assert.Contains("eight ASCII characters", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProtectedRequestCleanupRejectsNestedTemporaryDirectories()
    {
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var outerDirectory = Path.Combine(tempRoot, $"LucyVNC-Test-{Guid.NewGuid():N}");
        var setupDirectory = Path.Combine(outerDirectory, $"LucyVNC-Setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(setupDirectory);
        var requestPath = Path.Combine(setupDirectory, "request.bin");
        File.WriteAllBytes(requestPath, [1]);

        try
        {
            Assert.Throws<UnauthorizedAccessException>(() =>
                ServiceConfigurationProvisioner.DeleteProtectedRequest(requestPath));
        }
        finally
        {
            if (Directory.Exists(outerDirectory))
            {
                Directory.Delete(outerDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void ServiceConfigurationPersistsAgentIdentityAndCanBeReadByTheManagerStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"LucyVNC-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "service.json");
        var store = new ServiceConfigurationStore(path);
        var configuration = ServiceConfiguration.SafeDefaults with
        {
            RemoteSharingEnabled = true,
            AgentSid = WindowsIdentity.GetCurrent().User!.Value,
            AgentWorkerSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
        };

        try
        {
            store.Save(configuration);

            Assert.Equal(configuration, store.LoadExisting());
            Assert.Equal(configuration, store.LoadOrCreate());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
