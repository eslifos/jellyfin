using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Models.UserDtos;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Viewing profiles: a master account can have up to <see cref="MaxProfiles"/> profiles (including itself).
/// Each extra profile is a hidden Jellyfin user linked to the master, so it keeps its own
/// watch progress, playlists, favorites and settings. The links are stored in data/profiles.json.
/// </summary>
[Route("Profiles")]
[Authorize]
public class ProfilesController : BaseJellyfinApiController
{
    /// <summary>
    /// Maximum number of profiles per account, including the master.
    /// </summary>
    public const int MaxProfiles = 5;

    private static readonly SemaphoreSlim _lock = new(1, 1);

    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly IServerApplicationPaths _appPaths;
    private readonly ILogger<ProfilesController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfilesController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="appPaths">Instance of the <see cref="IServerApplicationPaths"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{ProfilesController}"/> interface.</param>
    public ProfilesController(
        IUserManager userManager,
        ISessionManager sessionManager,
        IServerApplicationPaths appPaths,
        ILogger<ProfilesController> logger)
    {
        _userManager = userManager;
        _sessionManager = sessionManager;
        _appPaths = appPaths;
        _logger = logger;
    }

    private string StorePath => Path.Combine(_appPaths.DataPath, "profiles.json");

    private bool IsAdmin => User.IsInRole(UserRoles.Administrator);

    /// <summary>
    /// Gets the profiles of the current account (or of <paramref name="userId"/> for administrators).
    /// </summary>
    /// <param name="userId">Optional user id (administrators only).</param>
    /// <response code="200">Profiles returned.</response>
    /// <returns>A <see cref="ProfileListDto"/>.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ProfileListDto>> GetProfiles([FromQuery] Guid? userId)
    {
        var currentId = User.GetUserId();
        var targetId = userId ?? currentId;
        if (!targetId.Equals(currentId) && !IsAdmin)
        {
            return Forbid();
        }

        var links = await ReadLinksAsync().ConfigureAwait(false);
        var masterId = ResolveMaster(links, targetId);
        return BuildList(links, masterId, currentId);
    }

    /// <summary>
    /// Gets all profile links (administrators only), used to group profiles in the dashboard.
    /// </summary>
    /// <response code="200">Links returned.</response>
    /// <returns>The list of <see cref="ProfileLinkDto"/>.</returns>
    [HttpGet("Links")]
    [Authorize(Policy = MediaBrowser.Common.Api.Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<ProfileLinkDto>>> GetProfileLinks()
    {
        var links = await ReadLinksAsync().ConfigureAwait(false);
        IEnumerable<ProfileLinkDto> result = links
            .Where(l => _userManager.GetUserById(l.Key) is not null && _userManager.GetUserById(l.Value) is not null)
            .Select(l => new ProfileLinkDto { ProfileId = l.Key, MasterId = l.Value })
            .ToList();
        return Ok(result);
    }

    /// <summary>
    /// Creates a new profile for the current account (or for <paramref name="userId"/> for administrators).
    /// </summary>
    /// <param name="request">The profile to create.</param>
    /// <param name="userId">Optional master user id (administrators only).</param>
    /// <response code="200">Profile created.</response>
    /// <response code="400">Invalid name or profile limit reached.</response>
    /// <response code="403">Only the master account or an administrator can create profiles.</response>
    /// <returns>The created <see cref="ProfileDto"/>.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ProfileDto>> CreateProfile([FromBody, Required] CreateProfileDto request, [FromQuery] Guid? userId)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return BadRequest("The profile name is required.");
        }

        var currentId = User.GetUserId();

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var links = await ReadLinksAsyncUnlocked().ConfigureAwait(false);
            var masterId = ResolveMaster(links, userId ?? currentId);

            // Only the master itself or an administrator can manage profiles
            if (!masterId.Equals(currentId) && !IsAdmin)
            {
                return Forbid();
            }

            var master = _userManager.GetUserById(masterId);
            if (master is null)
            {
                return NotFound();
            }

            if (links.Count(l => l.Value.Equals(masterId)) + 1 >= MaxProfiles)
            {
                return BadRequest($"An account can have up to {MaxProfiles} profiles.");
            }

            // Profile users are normal users, so the name must be unique on the server
            var username = name;
            if (_userManager.GetUserByName(username) is not null)
            {
                username = $"{name} ({master.Username})";
            }

            Jellyfin.Database.Implementations.Entities.User profile;
            try
            {
                profile = await _userManager.CreateUserAsync(username).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }

            // Random password: the profile can only be entered by switching from the account
            await _userManager.ChangePassword(profile.Id, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))).ConfigureAwait(false);

            var masterDto = _userManager.GetUserDto(master);
            if (masterDto.Configuration is not null)
            {
                await _userManager.UpdateConfigurationAsync(profile.Id, masterDto.Configuration).ConfigureAwait(false);
            }

            if (masterDto.Policy is not null)
            {
                var policy = masterDto.Policy;
                policy.IsAdministrator = false;
                policy.IsHidden = true;
                policy.IsDisabled = false;
                await _userManager.UpdatePolicyAsync(profile.Id, policy).ConfigureAwait(false);
            }

            links[profile.Id] = masterId;
            await WriteLinksAsyncUnlocked(links).ConfigureAwait(false);

            _logger.LogInformation("Created profile {Profile} for account {Master}", profile.Username, master.Username);

            return ToDto(profile.Id, false, currentId) ?? throw new ResourceNotFoundException();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Deletes a profile (the master account cannot be deleted here).
    /// </summary>
    /// <param name="profileId">The profile user id.</param>
    /// <response code="204">Profile deleted.</response>
    /// <response code="403">Only the master account or an administrator can delete profiles.</response>
    /// <response code="404">Profile not found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("{profileId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteProfile([FromRoute, Required] Guid profileId)
    {
        var currentId = User.GetUserId();

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var links = await ReadLinksAsyncUnlocked().ConfigureAwait(false);
            if (!links.TryGetValue(profileId, out var masterId))
            {
                return NotFound();
            }

            if (!masterId.Equals(currentId) && !IsAdmin)
            {
                return Forbid();
            }

            if (_userManager.GetUserById(profileId) is not null)
            {
                await _userManager.DeleteUserAsync(profileId).ConfigureAwait(false);
            }

            links.Remove(profileId);
            await WriteLinksAsyncUnlocked(links).ConfigureAwait(false);
            return NoContent();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Switches the current session to another profile of the same account.
    /// </summary>
    /// <param name="profileId">The profile user id to switch to.</param>
    /// <param name="request">The account password, needed when switching away from a secondary profile.</param>
    /// <response code="200">New session created for the profile.</response>
    /// <response code="403">The profile does not belong to the current account, or the password is wrong.</response>
    /// <response code="428">The account password is required.</response>
    /// <returns>An <see cref="AuthenticationResult"/> for the profile.</returns>
    [HttpPost("{profileId}/Switch")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<AuthenticationResult>> SwitchProfile(
        [FromRoute, Required] Guid profileId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SwitchProfileDto? request)
    {
        var currentId = User.GetUserId();
        var links = await ReadLinksAsync().ConfigureAwait(false);

        var masterId = ResolveMaster(links, currentId);
        var targetMaster = ResolveMaster(links, profileId);
        if (!masterId.Equals(targetMaster))
        {
            return Forbid();
        }

        var target = _userManager.GetUserById(profileId);
        if (target is null)
        {
            return NotFound();
        }

        // Only the account itself switches freely. A secondary profile (which may have parental limits)
        // must not reach the account, which may be an administrator, or a sibling without its password.
        if (!currentId.Equals(masterId))
        {
            var master = _userManager.GetUserById(masterId);
            if (master is null)
            {
                return Forbid();
            }

            if (!string.IsNullOrEmpty(master.Password))
            {
                if (string.IsNullOrEmpty(request?.Password))
                {
                    return StatusCode(StatusCodes.Status428PreconditionRequired);
                }

                try
                {
                    var verified = await _userManager.AuthenticateUser(master.Username, request.Password, HttpContext.GetNormalizedRemoteIP().ToString(), false).ConfigureAwait(false);
                    if (verified is null)
                    {
                        return Forbid();
                    }
                }
                catch (Exception ex) when (ex is AuthenticationException or SecurityException)
                {
                    _logger.LogWarning("Wrong account password when switching profile from {User}", User.GetUserId());
                    return Forbid();
                }
            }
        }

        return await _sessionManager.AuthenticateDirect(new AuthenticationRequest
        {
            UserId = profileId,
            DeviceId = User.GetDeviceId() ?? string.Empty,
            DeviceName = User.GetDevice() ?? string.Empty,
            App = User.GetClient() ?? string.Empty,
            AppVersion = User.GetVersion() ?? string.Empty,
            RemoteEndPoint = HttpContext.GetNormalizedRemoteIP().ToString()
        }).ConfigureAwait(false);
    }

    private static Guid ResolveMaster(Dictionary<Guid, Guid> links, Guid userId)
        => links.TryGetValue(userId, out var master) ? master : userId;

    private ProfileListDto BuildList(Dictionary<Guid, Guid> links, Guid masterId, Guid currentId)
    {
        var profiles = new List<ProfileDto>();
        var master = ToDto(masterId, true, currentId);
        if (master is not null)
        {
            profiles.Add(master);
        }

        profiles.AddRange(links
            .Where(l => l.Value.Equals(masterId))
            .Select(l => ToDto(l.Key, false, currentId))
            .OfType<ProfileDto>()
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase));

        return new ProfileListDto
        {
            MasterId = masterId,
            MaxProfiles = MaxProfiles,
            CanManage = masterId.Equals(currentId) || IsAdmin,
            Profiles = profiles
        };
    }

    private ProfileDto? ToDto(Guid id, bool isMaster, Guid currentId)
    {
        var user = _userManager.GetUserById(id);
        if (user is null)
        {
            return null;
        }

        var dto = _userManager.GetUserDto(user);
        return new ProfileDto
        {
            Id = id,
            Name = user.Username,
            PrimaryImageTag = dto.PrimaryImageTag,
            IsMaster = isMaster,
            IsCurrent = id.Equals(currentId),
            IsDisabled = dto.Policy?.IsDisabled ?? false
        };
    }

    private async Task<Dictionary<Guid, Guid>> ReadLinksAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ReadLinksAsyncUnlocked().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<Guid, Guid>> ReadLinksAsyncUnlocked()
    {
        if (!System.IO.File.Exists(StorePath))
        {
            return new Dictionary<Guid, Guid>();
        }

        try
        {
            var stream = System.IO.File.OpenRead(StorePath);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync<Dictionary<Guid, Guid>>(stream).ConfigureAwait(false)
                    ?? new Dictionary<Guid, Guid>();
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Invalid profiles file {Path}", StorePath);
            return new Dictionary<Guid, Guid>();
        }
    }

    private async Task WriteLinksAsyncUnlocked(Dictionary<Guid, Guid> links)
    {
        var temp = StorePath + ".tmp";
        var stream = System.IO.File.Create(temp);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, links).ConfigureAwait(false);
        }

        System.IO.File.Move(temp, StorePath, true);
    }
}
