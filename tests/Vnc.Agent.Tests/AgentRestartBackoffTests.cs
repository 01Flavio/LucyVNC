using Vnc.Agent;

namespace Vnc.Agent.Tests;

public sealed class AgentRestartBackoffTests
{
    [Fact]
    public void RestartDelayIncreasesExponentiallyAndIsCapped()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), AgentRestartBackoff.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), AgentRestartBackoff.GetDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(4), AgentRestartBackoff.GetDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(30), AgentRestartBackoff.GetDelay(6));
        Assert.Equal(TimeSpan.FromSeconds(30), AgentRestartBackoff.GetDelay(100));
    }

    [Fact]
    public void RestartDelayRejectsInvalidFailureCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentRestartBackoff.GetDelay(0));
    }
}
