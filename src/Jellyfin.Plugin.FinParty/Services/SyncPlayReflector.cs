using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MediaBrowser.Controller.SyncPlay;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinParty.Services;

/// <summary>
/// The single place in FinParty that reaches into Jellyfin's SyncPlay internals.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin exposes SyncPlay groups only through <see cref="ISyncPlayManager"/>, which hands
/// back DTOs. The three tolerances that decide whether a party survives a high-latency link —
/// <c>DefaultPing</c>, <c>TimeSyncOffset</c> and <c>MaxPlaybackOffset</c> — are get-only
/// auto-properties initialised inline on <c>Emby.Server.Implementations.SyncPlay.Group</c>,
/// so there is no supported way to change them.
/// </para>
/// <para>
/// Everything fragile is therefore quarantined here. If Jellyfin renames a field, this class
/// reports <see cref="IsAvailable"/> as <c>false</c> and every caller silently degrades to
/// stock behaviour rather than throwing. Nothing else in the plugin uses reflection.
/// </para>
/// </remarks>
public sealed class SyncPlayReflector
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private readonly ILogger<SyncPlayReflector> _logger;
    private readonly ISyncPlayManager _syncPlayManager;

    private readonly FieldInfo? _groupsField;
    private FieldInfo? _defaultPingField;
    private FieldInfo? _timeSyncOffsetField;
    private FieldInfo? _maxPlaybackOffsetField;
    private FieldInfo? _participantsField;
    private bool _groupFieldsResolved;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncPlayReflector"/> class.
    /// </summary>
    /// <param name="syncPlayManager">Jellyfin's SyncPlay manager.</param>
    /// <param name="logger">The logger.</param>
    public SyncPlayReflector(ISyncPlayManager syncPlayManager, ILogger<SyncPlayReflector> logger)
    {
        _syncPlayManager = syncPlayManager;
        _logger = logger;

        _groupsField = syncPlayManager.GetType().GetField("_groups", Instance);

        if (_groupsField is null)
        {
            _logger.LogWarning(
                "FinParty could not locate SyncPlayManager._groups on {Type}. Latency tuning and the " +
                "stall breaker are disabled; parties will still work with Jellyfin's stock tolerances.",
                syncPlayManager.GetType().FullName);
        }
    }

    /// <summary>
    /// Gets a value indicating whether the internals FinParty needs were found.
    /// </summary>
    public bool IsAvailable => _groupsField is not null;

    /// <summary>
    /// Gets a value indicating whether the tunable fields were found on the group type.
    /// </summary>
    public bool CanTune => _defaultPingField is not null
                           && _timeSyncOffsetField is not null
                           && _maxPlaybackOffsetField is not null;

    /// <summary>
    /// Gets a short human-readable description of what reflection managed to bind to.
    /// </summary>
    public string HealthSummary
    {
        get
        {
            if (!IsAvailable)
            {
                return "unavailable: SyncPlayManager._groups not found";
            }

            if (!_groupFieldsResolved)
            {
                return "pending: no group has been created yet";
            }

            return CanTune
                ? "ok: latency tuning active"
                : "degraded: group tolerance fields not found";
        }
    }

    /// <summary>
    /// Enumerates the live SyncPlay groups as their public state-context interface.
    /// </summary>
    /// <returns>The live groups, or an empty list when reflection is unavailable.</returns>
    public IReadOnlyList<IGroupStateContext> GetGroups()
    {
        if (_groupsField is null)
        {
            return Array.Empty<IGroupStateContext>();
        }

        try
        {
            if (_groupsField.GetValue(_syncPlayManager) is not System.Collections.IEnumerable raw)
            {
                return Array.Empty<IGroupStateContext>();
            }

            var groups = new List<IGroupStateContext>();
            foreach (var entry in raw)
            {
                // ConcurrentDictionary<Guid, Group> enumerates as KeyValuePair<Guid, Group>.
                var valueProperty = entry?.GetType().GetProperty("Value");
                if (valueProperty?.GetValue(entry) is IGroupStateContext context)
                {
                    EnsureGroupFields(context);
                    groups.Add(context);
                }
            }

            return groups;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FinParty failed to enumerate SyncPlay groups.");
            return Array.Empty<IGroupStateContext>();
        }
    }

    /// <summary>
    /// Finds a live group by its identifier.
    /// </summary>
    /// <param name="groupId">The group identifier.</param>
    /// <returns>The group, or <c>null</c> when not found.</returns>
    public IGroupStateContext? GetGroup(Guid groupId)
        => GetGroups().FirstOrDefault(g => g.GroupId.Equals(groupId));

    /// <summary>
    /// Reads a group's display name (the public <c>GroupName</c> property, not on the interface).
    /// </summary>
    /// <param name="group">The group.</param>
    /// <returns>The name, or <c>null</c> when unavailable.</returns>
    public string? GetGroupName(IGroupStateContext group)
    {
        try
        {
            return group.GetType().GetProperty("GroupName")?.GetValue(group) as string;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FinParty could not read the name of group {GroupId}.", group.GroupId);
            return null;
        }
    }

    /// <summary>
    /// Reads the participants of a group, keyed by session id.
    /// </summary>
    /// <param name="group">The group.</param>
    /// <returns>The participants, or an empty dictionary when unavailable.</returns>
    public IReadOnlyDictionary<string, GroupMember> GetParticipants(IGroupStateContext group)
    {
        EnsureGroupFields(group);

        if (_participantsField is null)
        {
            return new Dictionary<string, GroupMember>(StringComparer.Ordinal);
        }

        try
        {
            if (_participantsField.GetValue(group) is not IDictionary<string, GroupMember> participants)
            {
                return new Dictionary<string, GroupMember>(StringComparer.Ordinal);
            }

            // Copy under the caller's thread; Group mutates this dictionary from its own lock.
            return new Dictionary<string, GroupMember>(participants, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FinParty could not read participants for group {GroupId}.", group.GroupId);
            return new Dictionary<string, GroupMember>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Applies latency tolerances to a live group.
    /// </summary>
    /// <param name="group">The group to retune.</param>
    /// <param name="defaultPingMs">The assumed ping for sessions that have not reported one.</param>
    /// <param name="timeSyncOffsetMs">The accepted clock/transit skew.</param>
    /// <param name="maxPlaybackOffsetMs">The accepted playback position drift.</param>
    /// <returns><c>true</c> when the values were written.</returns>
    public bool ApplyTuning(IGroupStateContext group, long defaultPingMs, long timeSyncOffsetMs, long maxPlaybackOffsetMs)
    {
        EnsureGroupFields(group);

        if (!CanTune)
        {
            return false;
        }

        try
        {
            _defaultPingField!.SetValue(group, defaultPingMs);
            _timeSyncOffsetField!.SetValue(group, timeSyncOffsetMs);
            _maxPlaybackOffsetField!.SetValue(group, maxPlaybackOffsetMs);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FinParty failed to retune group {GroupId}.", group.GroupId);
            return false;
        }
    }

    /// <summary>
    /// Resolves the private fields on the concrete group type the first time a group is seen.
    /// </summary>
    /// <param name="group">A live group instance.</param>
    private void EnsureGroupFields(IGroupStateContext group)
    {
        if (_groupFieldsResolved)
        {
            return;
        }

        _groupFieldsResolved = true;
        var type = group.GetType();

        _defaultPingField = FindBackingField(type, nameof(IGroupStateContext.DefaultPing));
        _timeSyncOffsetField = FindBackingField(type, nameof(IGroupStateContext.TimeSyncOffset));
        _maxPlaybackOffsetField = FindBackingField(type, nameof(IGroupStateContext.MaxPlaybackOffset));
        _participantsField = type.GetField("_participants", Instance);

        if (CanTune)
        {
            _logger.LogInformation(
                "FinParty bound to {Type}; SyncPlay latency tuning is active.",
                type.FullName);
        }
        else
        {
            _logger.LogWarning(
                "FinParty bound to {Type} but could not find the SyncPlay tolerance fields. " +
                "Parties will run with Jellyfin's stock tolerances (500 ms playback offset), which is " +
                "unreliable over a relayed VPN link. This usually means the Jellyfin version changed.",
                type.FullName);
        }
    }

    /// <summary>
    /// Finds the compiler-generated backing field for a get-only auto-property,
    /// falling back to a conventionally named private field.
    /// </summary>
    /// <param name="type">The declaring type.</param>
    /// <param name="propertyName">The property name.</param>
    /// <returns>The field, or <c>null</c>.</returns>
    private static FieldInfo? FindBackingField(Type type, string propertyName)
    {
        var backing = type.GetField($"<{propertyName}>k__BackingField", Instance);
        if (backing is not null && backing.FieldType == typeof(long))
        {
            return backing;
        }

        var conventional = type.GetField($"_{char.ToLowerInvariant(propertyName[0])}{propertyName[1..]}", Instance);
        return conventional?.FieldType == typeof(long) ? conventional : null;
    }
}
