using Inngest;

public sealed record UserSignedUp(string UserId, string Email);

public sealed record Welcome(string UserId);

[InngestFunction("send-welcome-email")]
[EventTrigger("user/signed-up")]
public sealed class SendWelcomeEmail : InngestFunction<UserSignedUp, Welcome>
{
    public override async Task<Welcome> ExecuteAsync(FunctionContext<UserSignedUp> context, CancellationToken cancellationToken)
    {
        var user = context.Event.Data;

        await context.Step.RunAsync("send-welcome", () => $"Welcome, {user.Email}!");
        await context.Step.SleepAsync("wait-before-tips", TimeSpan.FromDays(1));
        await context.Step.RunAsync("send-tips", () => $"Three things to try first, {user.Email}.");

        return new Welcome(user.UserId);
    }
}
