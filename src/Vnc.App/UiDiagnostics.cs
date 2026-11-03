using System.Diagnostics;

namespace Vnc.App;

internal static class UiDiagnostics
{
    public static void Write(string eventName, string? details = null)
    {
#if DEBUG
        var suffix = string.IsNullOrWhiteSpace(details) ? string.Empty : $" {details}";
        Trace.WriteLine($"[{DateTimeOffset.Now:O}] [LucyVNC UI] {eventName}{suffix}");
#endif
    }
}
