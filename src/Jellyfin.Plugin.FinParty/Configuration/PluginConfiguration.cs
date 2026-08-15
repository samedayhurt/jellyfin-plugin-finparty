using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.FinParty.Configuration;

/// <summary>
/// Tuning profile applied to a SyncPlay group.
/// </summary>
public enum TuningMode
{
    /// <summary>
    /// Do not touch Jellyfin's SyncPlay internals at all. Stock behaviour.
    /// </summary>
    Off = 0,

    /// <summary>
    /// Apply the fixed tolerances configured below to every group.
    /// </summary>
    Fixed = 1,

    /// <summary>
    /// Measure real round-trip time per session and size the tolerances from it.
    /// Falls back to <see cref="Fixed"/> values when no measurement exists yet.
    /// </summary>
    Adaptive = 2
}

/// <summary>
/// FinParty plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets how aggressively FinParty retunes SyncPlay groups.
    /// </summary>
    public TuningMode Tuning { get; set; } = TuningMode.Adaptive;

    /// <summary>
    /// Gets or sets the assumed round-trip time, in milliseconds, for a session that
    /// has not reported a ping yet. Jellyfin's stock value is 500.
    /// </summary>
    public long DefaultPingMs { get; set; } = 400;

    /// <summary>
    /// Gets or sets the maximum accepted clock/transit skew, in milliseconds, before the
    /// server discards a client's reported timestamp. Jellyfin's stock value is 2000.
    /// </summary>
    public long TimeSyncOffsetMs { get; set; } = 5000;

    /// <summary>
    /// Gets or sets the playback position tolerance, in milliseconds, before the server
    /// force-seeks a client back into line. Jellyfin's stock value is 500, which a
    /// relayed VPN link violates constantly.
    /// </summary>
    public long MaxPlaybackOffsetMs { get; set; } = 1500;

    /// <summary>
    /// Gets or sets the ceiling applied to adaptive tolerances, in milliseconds, so a single
    /// pathological session cannot make the whole group sloppy.
    /// </summary>
    public long AdaptiveCeilingMs { get; set; } = 4000;

    /// <summary>
    /// Gets or sets the multiplier applied to measured round-trip time when deriving
    /// the playback tolerance in <see cref="TuningMode.Adaptive"/> mode.
    /// </summary>
    public double AdaptiveRttMultiplier { get; set; } = 3.0;

    /// <summary>
    /// Gets or sets a value indicating whether a member who has been buffering for longer than
    /// <see cref="StallBreakerSeconds"/> is released so the rest of the party can continue.
    /// </summary>
    public bool EnableStallBreaker { get; set; } = true;

    /// <summary>
    /// Gets or sets how long, in seconds, one member may stall the party before the
    /// stall breaker releases the group.
    /// </summary>
    public int StallBreakerSeconds { get; set; } = 25;
}
