using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// The PIN of an account, to leave its secondary profiles (a kids profile) with the digits of a remote instead of the
/// account password: 4 to 8 digits, kept only as a salted PBKDF2 hash, and a pause after several wrong tries.
/// </summary>
public static class ProfilePins
{
    /// <summary>
    /// Wrong tries in a row allowed before the pause.
    /// </summary>
    public const int MaxFailures = 5;

    /// <summary>
    /// The pause after too many wrong tries.
    /// </summary>
    public static readonly TimeSpan LockTime = TimeSpan.FromMinutes(1);

    private const int MinLength = 4;
    private const int MaxLength = 8;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;

    private static readonly ConcurrentDictionary<Guid, (int Failures, DateTime LockedUntil)> _attempts = new();

    /// <summary>
    /// Whether <paramref name="pin"/> can be a PIN (4 to 8 digits).
    /// </summary>
    /// <param name="pin">The PIN.</param>
    /// <returns><c>true</c> when it is valid.</returns>
    public static bool IsValid(string? pin)
    {
        if (pin is null || pin.Length < MinLength || pin.Length > MaxLength)
        {
            return false;
        }

        foreach (var c in pin)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The stored form of a PIN: "salt:hash", both in base 64.
    /// </summary>
    /// <param name="pin">A valid PIN.</param>
    /// <returns>The salted hash.</returns>
    public static string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(Derive(pin, salt));
    }

    /// <summary>
    /// Whether <paramref name="pin"/> is the one of <paramref name="stored"/> (see <see cref="Hash"/>).
    /// </summary>
    /// <param name="pin">The PIN typed.</param>
    /// <param name="stored">The stored hash.</param>
    /// <returns><c>true</c> when it matches.</returns>
    public static bool Verify(string pin, string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 2)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            return CryptographicOperations.FixedTimeEquals(Derive(pin, salt), expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the PIN of <paramref name="accountId"/> is paused after too many wrong tries.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="now">The current time.</param>
    /// <returns><c>true</c> while it is paused.</returns>
    public static bool IsLocked(Guid accountId, DateTime now)
        => _attempts.TryGetValue(accountId, out var state) && state.LockedUntil > now;

    /// <summary>
    /// Counts the result of a try: a right one clears the count, the last of too many wrong ones starts the pause.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="success">Whether the PIN was right.</param>
    /// <param name="now">The current time.</param>
    public static void RecordAttempt(Guid accountId, bool success, DateTime now)
    {
        if (success)
        {
            _attempts.TryRemove(accountId, out _);
            return;
        }

        _attempts.AddOrUpdate(
            accountId,
            _ => (1, DateTime.MinValue),
            (_, state) =>
            {
                var failures = state.LockedUntil > DateTime.MinValue && state.LockedUntil <= now ? 1 : state.Failures + 1;
                return failures >= MaxFailures ? (0, now + LockTime) : (failures, DateTime.MinValue);
            });
    }

    private static byte[] Derive(string pin, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, HashSize);
}
