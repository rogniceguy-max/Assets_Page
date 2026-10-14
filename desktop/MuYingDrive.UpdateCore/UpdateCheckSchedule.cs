namespace MuYingDrive.UpdateCore;

/// <summary>
/// Decides how long to wait before the next update check.
/// </summary>
/// <remarks>
/// The wait is jittered so that clients which started at the same moment — everyone
/// arriving at work, or everyone installed from the same setup executable the same
/// afternoon — do not poll in lockstep. Without it, thirty machines fire within the
/// same second every interval, and the NAS absorbs a burst for no benefit. The check
/// itself is one small request; the point is not to concentrate it.
///
/// Lives here rather than in the client so it can be tested: the harness for the update
/// path references UpdateCore and nothing else.
/// </remarks>
public static class UpdateCheckSchedule
{
    /// <summary>
    /// How often the signed stable channel is polled, before jitter.
    /// </summary>
    /// <remarks>
    /// A check is one small HTTPS request — the manifest is about 0.5 KB — and a package
    /// is downloaded only when a newer version exists. At this interval thirty clients
    /// average one request every ten seconds between them, which is nothing; the reason
    /// it is not shorter is that detecting a release sooner also means thirty machines
    /// start fetching 142 MB sooner, and that burst is the real load.
    /// </remarks>
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>How far the wait may drift either side of the interval: ±20%.</summary>
    public const double JitterFraction = 0.20;

    /// <summary>
    /// The delay before the next check, for a <paramref name="sample"/> in [0,1).
    /// </summary>
    /// <remarks>
    /// Deterministic for a given sample, so the bounds can be pinned in a test without
    /// waiting for anything. A sample of 0.5 returns exactly the interval, which keeps
    /// the documented period meaningful rather than a rough average.
    /// </remarks>
    public static TimeSpan NextDelay(TimeSpan interval, double sample)
    {
        var clamped = double.IsNaN(sample) ? 0.5 : Math.Clamp(sample, 0.0, 1.0);
        var factor = 1.0 + JitterFraction * (2.0 * clamped - 1.0);
        var ticks = (long)Math.Round(interval.Ticks * factor);
        return TimeSpan.FromTicks(Math.Max(0L, ticks));
    }
}
