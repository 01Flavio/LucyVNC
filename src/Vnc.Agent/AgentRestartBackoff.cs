namespace Vnc.Agent;

public static class AgentRestartBackoff
{
    public static TimeSpan GetDelay(int consecutiveFailures)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(consecutiveFailures);
        var exponent = Math.Min(consecutiveFailures - 1, 5);
        var seconds = Math.Min(30, 1 << exponent);
        return TimeSpan.FromSeconds(seconds);
    }
}
