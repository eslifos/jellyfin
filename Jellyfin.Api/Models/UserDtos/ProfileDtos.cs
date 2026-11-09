using System;
using System.Collections.Generic;

namespace Jellyfin.Api.Models.UserDtos;

/// <summary>
/// Body of a profile switch.
/// </summary>
public record SwitchProfileDto
{
    /// <summary>
    /// Gets the password of the account (master user). Required when switching away from a secondary profile.
    /// </summary>
    public string? Password { get; init; }
}

/// <summary>
/// A viewing profile of an account (the master user itself or one of its linked profile users).
/// </summary>
public record ProfileDto
{
    /// <summary>
    /// Gets the user id of the profile.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Gets the profile name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the primary image tag of the profile, if any.
    /// </summary>
    public string? PrimaryImageTag { get; init; }

    /// <summary>
    /// Gets a value indicating whether this profile is the master account.
    /// </summary>
    public bool IsMaster { get; init; }

    /// <summary>
    /// Gets a value indicating whether this is the profile of the current session.
    /// </summary>
    public bool IsCurrent { get; init; }

    /// <summary>
    /// Gets a value indicating whether the profile is disabled.
    /// </summary>
    public bool IsDisabled { get; init; }
}

/// <summary>
/// The profiles of an account.
/// </summary>
public record ProfileListDto
{
    /// <summary>
    /// Gets the id of the master account.
    /// </summary>
    public Guid MasterId { get; init; }

    /// <summary>
    /// Gets the maximum number of profiles (including the master).
    /// </summary>
    public int MaxProfiles { get; init; }

    /// <summary>
    /// Gets a value indicating whether the caller can create and delete profiles.
    /// </summary>
    public bool CanManage { get; init; }

    /// <summary>
    /// Gets the profiles, master first.
    /// </summary>
    public required IReadOnlyList<ProfileDto> Profiles { get; init; }
}

/// <summary>
/// Link between a profile user and its master account.
/// </summary>
public record ProfileLinkDto
{
    /// <summary>
    /// Gets the profile user id.
    /// </summary>
    public Guid ProfileId { get; init; }

    /// <summary>
    /// Gets the master user id.
    /// </summary>
    public Guid MasterId { get; init; }
}

/// <summary>
/// Request to create a profile.
/// </summary>
public record CreateProfileDto
{
    /// <summary>
    /// Gets the profile name.
    /// </summary>
    public required string Name { get; init; }
}
