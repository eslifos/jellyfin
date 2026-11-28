using System;
using Jellyfin.Api.Helpers;
using Xunit;

namespace Jellyfin.Api.Tests.Helpers;

public class ProfilePinsTests
{
    [Theory]
    [InlineData("1234", true)]
    [InlineData("12345678", true)]
    [InlineData("123", false)]
    [InlineData("123456789", false)]
    [InlineData("12a4", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_TakesFourToEightDigits(string? pin, bool expected)
    {
        Assert.Equal(expected, ProfilePins.IsValid(pin));
    }

    [Fact]
    public void Hash_VerifiesOnlyTheSamePin()
    {
        var stored = ProfilePins.Hash("2580");

        Assert.DoesNotContain("2580", stored, StringComparison.Ordinal);
        Assert.True(ProfilePins.Verify("2580", stored));
        Assert.False(ProfilePins.Verify("2581", stored));
        Assert.False(ProfilePins.Verify("2580", "not-a-hash"));
    }

    [Fact]
    public void Hash_IsSaltedEachTime()
    {
        Assert.NotEqual(ProfilePins.Hash("1111"), ProfilePins.Hash("1111"));
    }

    [Fact]
    public void RecordAttempt_PausesAfterTooManyWrongTries()
    {
        var account = Guid.NewGuid();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < ProfilePins.MaxFailures - 1; i++)
        {
            ProfilePins.RecordAttempt(account, false, now);
        }

        Assert.False(ProfilePins.IsLocked(account, now));
        ProfilePins.RecordAttempt(account, false, now);
        Assert.True(ProfilePins.IsLocked(account, now));
        Assert.False(ProfilePins.IsLocked(account, now + ProfilePins.LockTime + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void RecordAttempt_RightPinClearsTheCount()
    {
        var account = Guid.NewGuid();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < ProfilePins.MaxFailures - 1; i++)
        {
            ProfilePins.RecordAttempt(account, false, now);
        }

        ProfilePins.RecordAttempt(account, true, now);
        ProfilePins.RecordAttempt(account, false, now);
        Assert.False(ProfilePins.IsLocked(account, now));
    }
}
