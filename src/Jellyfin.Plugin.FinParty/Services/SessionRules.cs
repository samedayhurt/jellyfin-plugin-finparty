using System;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.FinParty.Services;

/// <summary>
/// Small pure helpers for reasoning about users and sessions.
/// </summary>
public static class SessionRules
{
    private static readonly TimeSpan IdleWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Reads a permission straight off the user entity.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="kind">The permission to read.</param>
    /// <returns><c>true</c> when the permission is granted.</returns>
    public static bool HasPermission(User user, PermissionKind kind)
    {
        foreach (var permission in user.Permissions)
        {
            if (permission.Kind == kind)
            {
                return permission.Value;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether a session is a real playback device recent enough to reason about.
    /// </summary>
    /// <remarks>
    /// This deliberately does not consult <c>SupportsMediaControl</c> or advertised capabilities:
    /// clients report them inconsistently (Moonfin advertises nothing while direct-playing), so a
    /// capability filter hides real televisions. A real user, a real device name, and a recent
    /// check-in is the honest test.
    /// </remarks>
    /// <param name="session">The session.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <returns><c>true</c> when the session is a plausible playback device.</returns>
    public static bool IsPlausiblePlaybackDevice(SessionInfo session, DateTime nowUtc)
        => IsPlausiblePlaybackDevice(session.UserId, session.DeviceName, session.LastActivityDate, nowUtc);

    /// <summary>
    /// The device test in terms that can be exercised without constructing a session.
    /// </summary>
    /// <param name="userId">The session's user.</param>
    /// <param name="deviceName">The device name.</param>
    /// <param name="lastActivityUtc">When the session last checked in.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <returns><c>true</c> when the session is a plausible playback device.</returns>
    public static bool IsPlausiblePlaybackDevice(
        Guid userId,
        string? deviceName,
        DateTime lastActivityUtc,
        DateTime nowUtc)
    {
        if (userId.Equals(default) || string.IsNullOrWhiteSpace(deviceName))
        {
            return false;
        }

        return nowUtc - lastActivityUtc < IdleWindow;
    }
}
