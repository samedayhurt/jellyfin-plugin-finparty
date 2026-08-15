using System;
using System.Security.Claims;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.FinParty.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinParty.Api;

/// <summary>
/// A small read-only surface for confirming FinParty is doing its job.
/// </summary>
/// <remarks>
/// FinParty is headless: it watches every live SyncPlay group and keeps it from stalling over a
/// VPN, with no user interaction. This controller exists only so an administrator can see that it
/// is attached and what it is observing — there is nothing here that controls playback.
/// </remarks>
[ApiController]
[Route("FinParty")]
public class FinPartyController : ControllerBase
{
    private const string UserIdClaim = "Jellyfin-UserId";

    private readonly NetworkDoctor _doctor;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="FinPartyController"/> class.
    /// </summary>
    /// <param name="doctor">The network doctor.</param>
    /// <param name="userManager">Jellyfin's user manager.</param>
    public FinPartyController(NetworkDoctor doctor, IUserManager userManager)
    {
        _doctor = doctor;
        _userManager = userManager;
    }

    /// <summary>
    /// Reports whether tuning is attached and what each link looks like.
    /// </summary>
    /// <returns>The diagnostic report.</returns>
    [HttpGet("api/health")]
    [Authorize]
    public ActionResult GetFinPartyHealth()
    {
        var user = GetCaller();
        return user is null ? Unauthorized() : Ok(_doctor.Diagnose(user));
    }

    private User? GetCaller()
    {
        var raw = (HttpContext.User.Identity as ClaimsIdentity)?.FindFirst(UserIdClaim)?.Value;
        return Guid.TryParse(raw, out var userId) ? _userManager.GetUserById(userId) : null;
    }
}
