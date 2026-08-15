using System;
using Jellyfin.Plugin.FinParty.Services;
using Xunit;

namespace Jellyfin.Plugin.FinParty.Tests;

/// <summary>
/// Tests for which sessions get offered as party candidates.
/// </summary>
/// <remarks>
/// FinParty 1.0.2 filtered on <c>SupportsMediaControl</c>, which seemed obviously right and was
/// obviously wrong: Moonfin for Android TV 2.4.0 reports <c>SupportsMediaControl=false</c> with
/// empty <c>PlayableMediaTypes</c> and <c>SupportedCommands</c> while direct-playing a film,
/// because it never calls <c>Sessions/Capabilities/Full</c>. On a server where every television
/// runs Moonfin, the device picker was simply empty and the whole feature was unusable.
/// </remarks>
public class DeviceEligibilityTests
{
    private static readonly DateTime Now = new(2026, 8, 14, 18, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SomeUser = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void AClientThatAdvertisesNothingIsStillOffered()
    {
        // Exactly what Moonfin for Android TV 2.4.0 looks like on the wire.
        Assert.True(PartyManager.IsPlausiblePlaybackDevice(
            SomeUser,
            "Moonfin for Android TV",
            "Amazon AFTJMST12",
            Now.AddSeconds(-5),
            Now));
    }

    [Fact]
    public void TheRemoteIsNeverOffered()
    {
        // A remote that joined its own party would leave the group waiting for a session
        // that never reports itself ready.
        Assert.False(PartyManager.IsPlausiblePlaybackDevice(
            SomeUser, "FinParty Remote", "someone's phone", Now, Now));
    }

    [Fact]
    public void ServiceSessionsWithNoUserAreNotOffered()
    {
        Assert.False(PartyManager.IsPlausiblePlaybackDevice(
            Guid.Empty, "MCP", "Jellyfin Server", Now, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SessionsWithoutADeviceNameAreNotOffered(string? deviceName)
    {
        Assert.False(PartyManager.IsPlausiblePlaybackDevice(
            SomeUser, "Some Client", deviceName, Now, Now));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(29, true)]
    [InlineData(31, false)]
    [InlineData(600, false)]
    public void OnlyRecentlySeenDevicesAreOffered(int minutesAgo, bool expected)
    {
        Assert.Equal(
            expected,
            PartyManager.IsPlausiblePlaybackDevice(
                SomeUser, "Moonfin", "Living Room TV", Now.AddMinutes(-minutesAgo), Now));
    }
}
