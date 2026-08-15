using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.FinParty.Services;

/// <summary>
/// How a client is reaching the server.
/// </summary>
public enum LinkKind
{
    /// <summary>The path could not be classified.</summary>
    Unknown,

    /// <summary>The client is on the same LAN as the server.</summary>
    Lan,

    /// <summary>The client is on a Tailscale tailnet.</summary>
    Tailscale,

    /// <summary>The client is on some other private network, typically a WireGuard tunnel.</summary>
    Vpn,

    /// <summary>The client is coming in over the public internet.</summary>
    Internet
}

/// <summary>
/// A diagnostic observation with an action attached.
/// </summary>
/// <param name="Severity">One of "ok", "warn" or "problem".</param>
/// <param name="Title">A short summary.</param>
/// <param name="Detail">What was observed.</param>
/// <param name="Fix">What to do about it, in plain language.</param>
public readonly record struct Finding(string Severity, string Title, string Detail, string Fix);

/// <summary>
/// Explains why a watch party is misbehaving, in language a person can act on.
/// </summary>
/// <remarks>
/// SyncPlay failures over a VPN almost never show up as an error. Playback simply drifts,
/// stalls, or snaps back a few seconds at a time, and the logs say nothing useful. This
/// service turns what the server can observe — where each client is connecting from, how
/// steady the link is, and whether the server is transcoding for it — into findings.
/// </remarks>
public sealed class NetworkDoctor
{
    private readonly ISessionManager _sessionManager;
    private readonly LatencyTracker _latency;
    private readonly SyncPlayReflector _reflector;

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkDoctor"/> class.
    /// </summary>
    /// <param name="sessionManager">Jellyfin's session manager.</param>
    /// <param name="latency">The latency tracker.</param>
    /// <param name="reflector">The SyncPlay internals accessor.</param>
    public NetworkDoctor(
        ISessionManager sessionManager,
        LatencyTracker latency,
        SyncPlayReflector reflector)
    {
        _sessionManager = sessionManager;
        _latency = latency;
        _reflector = reflector;
    }

    /// <summary>
    /// Produces a diagnostic report for the caller.
    /// </summary>
    /// <param name="caller">The calling user.</param>
    /// <returns>The report.</returns>
    public object Diagnose(User caller)
    {
        var findings = new List<Finding>();
        var links = new List<object>();
        var isAdmin = SessionRules.HasPermission(caller, PermissionKind.IsAdministrator);

        // Three states, not two. The per-group tolerance fields can only be resolved once a real
        // group object exists, so "no party has started yet" is normal and must not be reported
        // as a failure — that reads as broken on a perfectly healthy server.
        if (_reflector.CanTune)
        {
            findings.Add(new Finding(
                "ok",
                "Latency tuning is active",
                "FinParty is widening SyncPlay's timing tolerances to match your network.",
                "Nothing to do."));
        }
        else if (_reflector.IsAvailable)
        {
            findings.Add(new Finding(
                "ok",
                "Latency tuning is ready",
                "FinParty is attached to Jellyfin's SyncPlay manager. It measures and applies the "
                + "tolerances the moment a SyncPlay group starts playing.",
                "Nothing to do."));
        }
        else
        {
            findings.Add(new Finding(
                "warn",
                "Latency tuning is not available",
                $"FinParty could not attach to Jellyfin's SyncPlay manager ({_reflector.HealthSummary}). "
                + "SyncPlay still works, but with Jellyfin's fixed 500 ms drift tolerance.",
                "This usually means the Jellyfin version changed. Check for a FinParty update."));
        }

        var transcoding = 0;

        foreach (var session in _sessionManager.Sessions)
        {
            // Same reasoning as PartyManager: capability flags are not a reliable signal,
            // and filtering on them reported zero links on a server full of live televisions.
            if (!SessionRules.IsPlausiblePlaybackDevice(session, DateTime.UtcNow))
            {
                continue;
            }

            if (!isAdmin && !session.UserId.Equals(caller.Id))
            {
                continue;
            }

            var kind = Classify(session.RemoteEndPoint);
            var stats = _latency.Get(session.Id);
            var isTranscoding = session.TranscodingInfo is not null;

            if (isTranscoding)
            {
                transcoding++;
            }

            links.Add(new
            {
                sessionId = session.Id,
                device = session.DeviceName,
                client = session.Client,
                user = session.UserName,
                link = kind.ToString(),
                latencyMs = stats.Samples > 0 ? stats.MedianMs : -1,
                jitterMs = stats.Samples > 0 ? stats.JitterMs : -1,
                worstMs = stats.Samples > 0 ? stats.WorstMs : -1,
                quality = stats.Samples > 0 ? stats.Quality : "unknown",
                transcoding = isTranscoding
            });

            if (stats.Samples >= 6 && stats.JitterMs > 120)
            {
                findings.Add(new Finding(
                    "problem",
                    $"{session.DeviceName} has an unstable link",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Round-trip time swings by about {stats.JitterMs} ms around a median of {stats.MedianMs} ms."),
                    kind == LinkKind.Tailscale
                        ? "That pattern means the tailnet is falling back to a DERP relay instead of a direct " +
                          "connection. Run 'tailscale status' and look for 'relay' next to this device; opening " +
                          "UDP 41641 or enabling UPnP/NAT-PMP on both ends usually restores a direct path."
                        : "Check for Wi-Fi congestion or an overloaded uplink on this device's network."));
            }
            else if (stats.Samples >= 6 && stats.MedianMs > 250)
            {
                findings.Add(new Finding(
                    "warn",
                    $"{session.DeviceName} is a long way away",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Median round-trip time is {stats.MedianMs} ms."),
                    "Steady latency is fine — FinParty widens the tolerances to match. Expect a longer " +
                    "pause when someone hits play."));
            }

            if (isTranscoding && kind != LinkKind.Lan)
            {
                findings.Add(new Finding(
                    "warn",
                    $"{session.DeviceName} is being transcoded over a tunnel",
                    "The server is re-encoding this stream and pushing it down a VPN link.",
                    "This is the most common cause of one person stalling the whole party. If the file " +
                    "plays directly on the LAN, the tunnel's smaller packet size is usually the trigger — " +
                    "see the MTU note below."));
            }
        }

        AddMtuFinding(findings);

        if (transcoding > 1)
        {
            findings.Add(new Finding(
                "warn",
                "More than one stream is being transcoded",
                $"{transcoding} sessions are transcoding at once.",
                "Watch parties work best when everyone direct-plays the same file. Matching everyone's " +
                "quality setting to the source usually removes the stalls entirely."));
        }

        return new
        {
            generatedUtc = DateTime.UtcNow,
            syncPlayInternals = _reflector.HealthSummary,
            tuningActive = _reflector.CanTune,
            links,
            findings = findings.Select(f => new
            {
                severity = f.Severity,
                title = f.Title,
                detail = f.Detail,
                fix = f.Fix
            })
        };
    }

    /// <summary>
    /// Classifies a remote endpoint into the kind of path it most likely represents.
    /// </summary>
    /// <param name="remoteEndPoint">The remote endpoint reported by Jellyfin.</param>
    /// <returns>The link kind.</returns>
    public static LinkKind Classify(string? remoteEndPoint)
    {
        if (string.IsNullOrWhiteSpace(remoteEndPoint))
        {
            return LinkKind.Unknown;
        }

        var host = remoteEndPoint.Trim();

        // Strip a port, taking care not to break IPv6 literals.
        if (host.StartsWith('['))
        {
            var close = host.IndexOf(']', StringComparison.Ordinal);
            if (close > 0)
            {
                host = host[1..close];
            }
        }
        else if (host.Count(c => c == ':') == 1)
        {
            host = host[..host.IndexOf(':', StringComparison.Ordinal)];
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            return LinkKind.Unknown;
        }

        if (IPAddress.IsLoopback(address))
        {
            return LinkKind.Lan;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Tailscale hands out ULAs from fd7a:115c:a1e0::/48.
            var v6 = address.GetAddressBytes();
            if (v6[0] == 0xfd && v6[1] == 0x7a && v6[2] == 0x11 && v6[3] == 0x5c)
            {
                return LinkKind.Tailscale;
            }

            // fc00::/7 unique local, fe80::/10 link local.
            if ((v6[0] & 0xfe) == 0xfc)
            {
                return LinkKind.Vpn;
            }

            if (v6[0] == 0xfe && (v6[1] & 0xc0) == 0x80)
            {
                return LinkKind.Lan;
            }

            return LinkKind.Internet;
        }

        var bytes = address.GetAddressBytes();

        // Tailscale allocates from the 100.64.0.0/10 CGNAT range.
        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
        {
            return LinkKind.Tailscale;
        }

        if (bytes[0] == 10)
        {
            return LinkKind.Vpn;
        }

        if (bytes[0] == 192 && bytes[1] == 168)
        {
            return LinkKind.Lan;
        }

        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
        {
            return LinkKind.Lan;
        }

        return LinkKind.Internet;
    }

    /// <summary>
    /// Reports the server's tunnel interfaces and the packet-size trap that comes with them.
    /// </summary>
    /// <param name="findings">The findings list to append to.</param>
    private static void AddMtuFinding(ICollection<Finding> findings)
    {
        try
        {
            var tunnels = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .Select(nic => new
                {
                    nic.Name,
                    Mtu = SafeMtu(nic)
                })
                .Where(nic => nic.Mtu > 0 && nic.Mtu < 1500)
                .ToList();

            if (tunnels.Count == 0)
            {
                return;
            }

            var described = string.Join(", ", tunnels.Select(t => $"{t.Name} (MTU {t.Mtu})"));

            findings.Add(new Finding(
                "warn",
                "The server has a reduced-MTU interface",
                $"Tunnel interfaces found: {described}. Tailscale defaults to 1280 and WireGuard to 1420, " +
                "against 1500 on a normal link.",
                "If playback stalls only over the tunnel, the usual cause is a black hole for large packets. " +
                "Confirm with 'ping -M do -s 1400 <server-tailscale-ip>' from a client; if that fails but " +
                "'-s 1200' succeeds, clamp MSS on the router (or set Tailscale's MTU) rather than changing Jellyfin."));
        }
        catch (NetworkInformationException)
        {
            // Reading interfaces is best-effort; a container without NET_ADMIN may refuse.
        }
        catch (PlatformNotSupportedException)
        {
            // Likewise on restricted platforms.
        }
    }

    private static int SafeMtu(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GetIPv4Properties()?.Mtu ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
