using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.FinParty.Models;

/// <summary>
/// A device that the caller is allowed to pull into a party.
/// </summary>
public class DeviceDto
{
    /// <summary>Gets or sets the Jellyfin session identifier.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the friendly device name, for example "Living Room Apple TV".</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>Gets or sets the client application name, for example "Moonfin".</summary>
    public string Client { get; set; } = string.Empty;

    /// <summary>Gets or sets the name of the signed-in user.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets the identifier of the signed-in user.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets a value indicating whether this session belongs to the caller.</summary>
    public bool IsMine { get; set; }

    /// <summary>Gets or sets what the device is playing, if anything.</summary>
    public string? NowPlaying { get; set; }

    /// <summary>Gets or sets a value indicating whether the device is already in a party.</summary>
    public bool InParty { get; set; }

    /// <summary>Gets or sets the party code the device is currently in, if any.</summary>
    public string? PartyCode { get; set; }

    /// <summary>Gets or sets the measured round-trip time in milliseconds, or -1 when unknown.</summary>
    public long LatencyMs { get; set; } = -1;

    /// <summary>Gets or sets a plain-language link quality label.</summary>
    public string LinkQuality { get; set; } = "unknown";

    /// <summary>Gets or sets a value indicating whether the client advertises SyncPlay support.</summary>
    public bool SupportsSyncPlay { get; set; }

    /// <summary>Gets or sets seconds since the device was last seen.</summary>
    public double IdleSeconds { get; set; }
}

/// <summary>
/// A member of a live party.
/// </summary>
public class PartyMemberDto
{
    /// <summary>Gets or sets the session identifier.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name of the member.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets the device name.</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the member is buffering.</summary>
    public bool IsBuffering { get; set; }

    /// <summary>Gets or sets a value indicating whether the party has stopped waiting for this member.</summary>
    public bool Released { get; set; }

    /// <summary>Gets or sets the measured round-trip time in milliseconds, or -1 when unknown.</summary>
    public long LatencyMs { get; set; } = -1;

    /// <summary>Gets or sets a plain-language link quality label.</summary>
    public string LinkQuality { get; set; } = "unknown";
}

/// <summary>
/// The current state of a party.
/// </summary>
public class PartyStateDto
{
    /// <summary>Gets or sets the SyncPlay group identifier.</summary>
    public Guid GroupId { get; set; }

    /// <summary>Gets or sets the short join code.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Gets or sets the party name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the SyncPlay group state, for example Playing or Paused.</summary>
    public string State { get; set; } = "Idle";

    /// <summary>Gets or sets what the party is watching.</summary>
    public string? NowPlaying { get; set; }

    /// <summary>Gets or sets the identifier of the item being watched.</summary>
    public Guid? NowPlayingItemId { get; set; }

    /// <summary>Gets or sets the current playback position in seconds.</summary>
    public double PositionSeconds { get; set; }

    /// <summary>Gets or sets the runtime of the current item in seconds.</summary>
    public double RuntimeSeconds { get; set; }

    /// <summary>Gets or sets the members of the party.</summary>
    public IReadOnlyList<PartyMemberDto> Members { get; set; } = Array.Empty<PartyMemberDto>();

    /// <summary>Gets or sets a value indicating whether anyone is currently buffering.</summary>
    public bool AnyoneBuffering { get; set; }

    /// <summary>Gets or sets the tolerances FinParty applied to this party.</summary>
    public TuningDto? Tuning { get; set; }
}

/// <summary>
/// The latency tolerances applied to a party.
/// </summary>
public class TuningDto
{
    /// <summary>Gets or sets the tuning mode in force.</summary>
    public string Mode { get; set; } = "Off";

    /// <summary>Gets or sets the applied playback drift tolerance in milliseconds.</summary>
    public long MaxPlaybackOffsetMs { get; set; }

    /// <summary>Gets or sets the applied clock-skew tolerance in milliseconds.</summary>
    public long TimeSyncOffsetMs { get; set; }

    /// <summary>Gets or sets the worst observed round-trip time in milliseconds.</summary>
    public long ObservedRttMs { get; set; }

    /// <summary>Gets or sets the worst observed jitter in milliseconds.</summary>
    public long ObservedJitterMs { get; set; }

    /// <summary>Gets or sets a plain-language explanation for the admin page.</summary>
    public string Explanation { get; set; } = string.Empty;
}

/// <summary>
/// The outcome of inviting devices into a party.
/// </summary>
public class InviteResultDto
{
    /// <summary>Gets or sets the sessions that joined.</summary>
    public IList<string> Joined { get; set; } = new List<string>();

    /// <summary>Gets or sets the sessions that could not join, with a reason.</summary>
    public IDictionary<string, string> Failed { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// A request to start a new party.
/// </summary>
public class CreatePartyRequest
{
    /// <summary>Gets or sets the party name shown to the family.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the sessions to pull into the party.</summary>
    public IReadOnlyList<string> SessionIds { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets an optional item to start playing immediately.</summary>
    public Guid? ItemId { get; set; }
}

/// <summary>
/// A request to add devices to an existing party.
/// </summary>
public class InviteRequest
{
    /// <summary>Gets or sets the sessions to pull into the party.</summary>
    public IReadOnlyList<string> SessionIds { get; set; } = Array.Empty<string>();
}

/// <summary>
/// A request to start playback in a party.
/// </summary>
public class PlayRequest
{
    /// <summary>Gets or sets the item to play.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the position to start from, in seconds.</summary>
    public double StartSeconds { get; set; }
}

/// <summary>
/// A request to seek the party.
/// </summary>
public class SeekRequest
{
    /// <summary>Gets or sets the target position in seconds.</summary>
    public double PositionSeconds { get; set; }
}
