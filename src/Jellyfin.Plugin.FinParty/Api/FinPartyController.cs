using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.FinParty.Models;
using Jellyfin.Plugin.FinParty.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinParty.Api;

/// <summary>
/// The FinParty API and the party remote itself.
/// </summary>
[ApiController]
[Route("FinParty")]
public class FinPartyController : ControllerBase
{
    private const string UserIdClaim = "Jellyfin-UserId";
    private const string IsApiKeyClaim = "Jellyfin-IsApiKey";

    private const string ApiKeyMessage =
        "FinParty needs a user token, not an API key. An API key authenticates the server itself " +
        "and carries no user identity, so there is nobody to own the party or check device " +
        "permissions against. Sign in with a normal account instead " +
        "(POST /Users/AuthenticateByName) and use the returned AccessToken.";

    private readonly PartyManager _parties;
    private readonly NetworkDoctor _doctor;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<FinPartyController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FinPartyController"/> class.
    /// </summary>
    /// <param name="parties">The party manager.</param>
    /// <param name="doctor">The network doctor.</param>
    /// <param name="userManager">Jellyfin's user manager.</param>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="logger">The logger.</param>
    public FinPartyController(
        PartyManager parties,
        NetworkDoctor doctor,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILogger<FinPartyController> logger)
    {
        _parties = parties;
        _doctor = doctor;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Serves the party remote.
    /// </summary>
    /// <returns>The remote's HTML.</returns>
    [HttpGet("")]
    [HttpGet("index.html")]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Text.Html)]
    public ActionResult GetFinPartyRemote()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = $"{typeof(Plugin).Namespace}.Web.party.html";

        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            _logger.LogError("FinParty could not load its remote from {Resource}.", resource);
            return StatusCode(StatusCodes.Status500InternalServerError, "FinParty remote is missing from the plugin.");
        }

        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), MediaTypeNames.Text.Html);
    }

    /// <summary>
    /// Lists the devices the caller may add to a party.
    /// </summary>
    /// <returns>The controllable devices.</returns>
    [HttpGet("api/devices")]
    [Authorize]
    public ActionResult<IReadOnlyList<FinPartyDeviceDto>> GetFinPartyDevices()
        => Execute(user => _parties.GetDevices(user));

    /// <summary>
    /// Lists the parties currently running.
    /// </summary>
    /// <returns>The live parties.</returns>
    [HttpGet("api/parties")]
    [Authorize]
    public ActionResult<IReadOnlyList<FinPartyStateDto>> GetFinPartyParties()
        => Execute(_ => _parties.GetParties());

    /// <summary>
    /// Starts a new party.
    /// </summary>
    /// <param name="request">The create request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The new party and the outcome of each invitation.</returns>
    [HttpPost("api/parties")]
    [Authorize]
    public async Task<ActionResult> CreateFinParty(
        [FromBody] FinPartyCreateRequest request,
        CancellationToken cancellationToken)
    {
        var user = GetCaller();
        if (user is null)
        {
            return NoCaller();
        }

        try
        {
            await _parties.EnsureSyncPlayAccessAsync(user).ConfigureAwait(false);
            await _parties.EnsureSyncPlayAccessForSessionsAsync(request.SessionIds).ConfigureAwait(false);
            var (state, invites) = _parties.CreateParty(user, request, cancellationToken);
            return Ok(new { party = state, invites });
        }
        catch (PartyForbiddenException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gets the state of a party.
    /// </summary>
    /// <param name="groupId">The party group identifier.</param>
    /// <returns>The party state.</returns>
    [HttpGet("api/parties/{groupId}")]
    [Authorize]
    public ActionResult GetFinPartyState([FromRoute] Guid groupId)
        => Execute(user => _parties.GetState(user, groupId));

    /// <summary>
    /// Resolves a short join code to a party.
    /// </summary>
    /// <param name="code">The join code.</param>
    /// <returns>The party state.</returns>
    [HttpGet("api/code/{code}")]
    [Authorize]
    public ActionResult ResolveFinPartyCode([FromRoute] string code)
    {
        var groupId = _parties.ResolveCode(code);

        if (groupId is null)
        {
            return NotFound(new { error = "No party with that code. Codes expire once everyone leaves." });
        }

        return Execute(user => _parties.GetState(user, groupId.Value));
    }

    /// <summary>
    /// Adds devices to a party.
    /// </summary>
    /// <param name="groupId">The party group identifier.</param>
    /// <param name="request">The invite request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome of each invitation.</returns>
    [HttpPost("api/parties/{groupId}/invite")]
    [Authorize]
    public async Task<ActionResult> InviteToFinParty(
        [FromRoute] Guid groupId,
        [FromBody] FinPartyInviteRequest request,
        CancellationToken cancellationToken)
    {
        if (GetCaller() is null)
        {
            return NoCaller();
        }

        await _parties.EnsureSyncPlayAccessForSessionsAsync(request.SessionIds).ConfigureAwait(false);
        return Execute(user => _parties.Invite(user, groupId, request.SessionIds, cancellationToken));
    }

    /// <summary>
    /// Removes a device from a party.
    /// </summary>
    /// <param name="groupId">The party group identifier.</param>
    /// <param name="sessionId">The session to remove.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated party state.</returns>
    [HttpDelete("api/parties/{groupId}/members/{sessionId}")]
    [Authorize]
    public ActionResult RemoveFromFinParty(
        [FromRoute] Guid groupId,
        [FromRoute] string sessionId,
        CancellationToken cancellationToken)
        => Execute(user =>
        {
            _parties.Remove(user, groupId, sessionId, cancellationToken);
            return _parties.GetState(user, groupId);
        });

    /// <summary>
    /// Starts playback across the party.
    /// </summary>
    /// <param name="groupId">The party group identifier.</param>
    /// <param name="request">The play request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated party state.</returns>
    [HttpPost("api/parties/{groupId}/play")]
    [Authorize]
    public ActionResult PlayInFinParty(
        [FromRoute] Guid groupId,
        [FromBody] FinPartyPlayRequest request,
        CancellationToken cancellationToken)
        => Execute(user =>
        {
            _parties.Play(user, groupId, request, cancellationToken);
            return _parties.GetState(user, groupId);
        });

    /// <summary>
    /// Pauses the party.
    /// </summary>
    /// <param name="groupId">The party group identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated party state.</returns>
    [HttpPost("api/parties/{groupId}/pause")]
    [Authorize]
    public ActionResult PauseFinParty([FromRoute] Guid groupId, CancellationToken cancellationToken)
        => Execute(user =>
        {
            _parties.Command(user, groupId, new PauseGroupRequest(), cancellationToken);
            return _parties.GetState(user, groupId);
        });

    /// <summary>
    /// Resumes the party.
    /// </summary>
    /// <param name="groupId">The party group identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated party state.</returns>
    [HttpPost("api/parties/{groupId}/resume")]
    [Authorize]
    public ActionResult ResumeFinParty([FromRoute] Guid groupId, CancellationToken cancellationToken)
        => Execute(user =>
        {
            _parties.Command(user, groupId, new UnpauseGroupRequest(), cancellationToken);
            return _parties.GetState(user, groupId);
        });

    /// <summary>
    /// Seeks the party to a position.
    /// </summary>
    /// <param name="groupId">The party group identifier.</param>
    /// <param name="request">The seek request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated party state.</returns>
    [HttpPost("api/parties/{groupId}/seek")]
    [Authorize]
    public ActionResult SeekFinParty(
        [FromRoute] Guid groupId,
        [FromBody] FinPartySeekRequest request,
        CancellationToken cancellationToken)
        => Execute(user =>
        {
            var ticks = (long)Math.Max(0, request.PositionSeconds) * TimeSpan.TicksPerSecond;
            _parties.Command(user, groupId, new SeekGroupRequest(ticks), cancellationToken);
            return _parties.GetState(user, groupId);
        });

    /// <summary>
    /// Stops the party for everyone.
    /// </summary>
    /// <param name="groupId">The party group identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpPost("api/parties/{groupId}/end")]
    [Authorize]
    public ActionResult EndFinParty([FromRoute] Guid groupId, CancellationToken cancellationToken)
        => Execute(user =>
        {
            _parties.Command(user, groupId, new StopGroupRequest(), cancellationToken);

            foreach (var member in _parties.GetState(user, groupId).Members.ToList())
            {
                try
                {
                    _parties.Remove(user, groupId, member.SessionId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "FinParty could not remove {SessionId} while ending the party.", member.SessionId);
                }
            }

            return new { ended = true };
        });

    /// <summary>
    /// Reports on the health of the links between the server and its clients.
    /// </summary>
    /// <returns>The diagnostic report.</returns>
    [HttpGet("api/health")]
    [Authorize]
    public ActionResult GetFinPartyHealth()
        => Execute(user => _doctor.Diagnose(user));

    /// <summary>
    /// Lists items the caller can start a party with.
    /// </summary>
    /// <param name="q">An optional search term.</param>
    /// <param name="limit">The maximum number of results.</param>
    /// <returns>The matching items.</returns>
    [HttpGet("api/library")]
    [Authorize]
    public ActionResult GetFinPartyLibrary([FromQuery] string? q, [FromQuery] int limit = 40)
        => Execute(user => Browse(user, q, Math.Clamp(limit, 1, 100)));

    private IReadOnlyList<object> Browse(User user, string? searchTerm, int limit)
    {
        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            Recursive = true,
            Limit = limit,
            IsVirtualItem = false,
            OrderBy = new[]
            {
                (ItemSortBy.DateCreated, SortOrder.Descending)
            }
        };

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            query.SearchTerm = searchTerm.Trim();
            query.OrderBy = Array.Empty<(ItemSortBy, SortOrder)>();
        }

        return _libraryManager.GetItemList(query)
            .Select(item => (object)new
            {
                id = item.Id,
                name = item.Name,
                type = item.GetBaseItemKind().ToString(),
                seriesName = (item as Episode)?.SeriesName,
                year = item.ProductionYear,
                runtimeSeconds = TimeSpan.FromTicks(item.RunTimeTicks ?? 0).TotalSeconds,
                subtitle = BuildSubtitle(item)
            })
            .ToList();
    }

    private static string BuildSubtitle(BaseItem item)
    {
        if (item is Episode episode)
        {
            var season = episode.ParentIndexNumber;
            var number = episode.IndexNumber;

            if (season.HasValue && number.HasValue)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{episode.SeriesName} · S{season:00}E{number:00}");
            }

            return episode.SeriesName ?? string.Empty;
        }

        return item.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private User? GetCaller()
    {
        var raw = (HttpContext.User.Identity as ClaimsIdentity)?
            .FindFirst(UserIdClaim)?.Value;

        return Guid.TryParse(raw, out var userId) ? _userManager.GetUserById(userId) : null;
    }

    /// <summary>
    /// Determines whether the request was authenticated with an API key rather than a user token.
    /// </summary>
    /// <returns><c>true</c> when the caller is an API key.</returns>
    private bool IsApiKeyRequest()
        => string.Equals(
            (HttpContext.User.Identity as ClaimsIdentity)?.FindFirst(IsApiKeyClaim)?.Value,
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Produces the correct failure for a request with no usable user identity.
    /// </summary>
    /// <returns>The error result.</returns>
    private ActionResult NoCaller()
        => IsApiKeyRequest()
            ? BadRequest(new { error = ApiKeyMessage })
            : Unauthorized();

    private ActionResult Execute<T>(Func<User, T> action)
    {
        var user = GetCaller();
        if (user is null)
        {
            return NoCaller();
        }

        try
        {
            return Ok(action(user));
        }
        catch (PartyForbiddenException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
