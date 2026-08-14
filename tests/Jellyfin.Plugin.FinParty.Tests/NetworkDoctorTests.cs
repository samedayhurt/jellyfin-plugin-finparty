using Jellyfin.Plugin.FinParty.Services;
using Xunit;

namespace Jellyfin.Plugin.FinParty.Tests;

/// <summary>
/// Tests for endpoint classification.
/// </summary>
/// <remarks>
/// Getting Tailscale's range wrong is the difference between telling someone
/// "your tailnet is relaying, open UDP 41641" and useless generic advice, so the
/// boundaries of 100.64.0.0/10 are pinned down explicitly here.
/// </remarks>
public class NetworkDoctorTests
{
    [Theory]
    [InlineData("100.64.0.1", LinkKind.Tailscale)]
    [InlineData("100.100.100.100:52344", LinkKind.Tailscale)]
    [InlineData("100.127.255.254", LinkKind.Tailscale)]
    [InlineData("100.63.255.255", LinkKind.Internet)]  // just below the CGNAT range
    [InlineData("100.128.0.1", LinkKind.Internet)]     // just above the CGNAT range
    [InlineData("192.168.1.50", LinkKind.Lan)]
    [InlineData("172.17.0.5", LinkKind.Lan)]
    [InlineData("172.32.0.5", LinkKind.Internet)]      // outside 172.16/12
    [InlineData("10.8.0.3", LinkKind.Vpn)]
    [InlineData("127.0.0.1", LinkKind.Lan)]
    [InlineData("8.8.8.8", LinkKind.Internet)]
    public void Classify_MapsIpv4(string endpoint, LinkKind expected)
        => Assert.Equal(expected, NetworkDoctor.Classify(endpoint));

    [Theory]
    [InlineData("[fd7a:115c:a1e0::1234:5678]:41641", LinkKind.Tailscale)]
    [InlineData("[fd00::1]:8096", LinkKind.Vpn)]
    [InlineData("[fe80::1]:8096", LinkKind.Lan)]
    [InlineData("[2001:db8::1]:8096", LinkKind.Internet)]
    public void Classify_MapsIpv6(string endpoint, LinkKind expected)
        => Assert.Equal(expected, NetworkDoctor.Classify(endpoint));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    public void Classify_HandlesJunk(string? endpoint)
        => Assert.Equal(LinkKind.Unknown, NetworkDoctor.Classify(endpoint));
}
