namespace Inngest.Sample;

// Each event's name lives on the record that carries its data, so sends, triggers and waits name the type.
public sealed record UserSignedUp(string UserId, string Email) : IInngestEventData
{
    public static string EventName => "sample/user.signed-up";
}

public sealed record UserActivated(string UserId) : IInngestEventData
{
    public static string EventName => "sample/user.activated";
}

public sealed record UserDeleted(string UserId) : IInngestEventData
{
    public static string EventName => "sample/user.deleted";
}

public sealed record SummaryRequested : IInngestEventData
{
    public static string EventName => "sample/summary.requested";
}

public sealed record SummaryReady(int Total, int Accounts) : IInngestEventData
{
    public static string EventName => "sample/summary.ready";
}

public sealed record ScoreRequest(string AccountId);

public sealed record AccountScore(string AccountId, int Score);
