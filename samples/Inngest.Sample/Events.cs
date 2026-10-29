namespace Inngest.Sample;

public static class Events
{
    public const string UserSignedUp = "sample/user.signed-up";
    public const string UserDeleted = "sample/user.deleted";
    public const string UserActivated = "sample/user.activated";
    public const string SummaryRequested = "sample/summary.requested";
}

public sealed record UserSignedUp(string UserId, string Email);

public sealed record UserActivated(string UserId);

public sealed record AccountScore(string AccountId, int Score);
