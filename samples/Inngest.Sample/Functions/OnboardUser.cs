namespace Inngest.Sample.Functions;

// Welcomes a new user, waits up to a day for them to activate their account and nudges them if they do not. Deleting
// the user cancels the run.
[InngestFunction("onboard-user", Name = "Onboard a new user", Retries = 3)]
[EventTrigger<UserSignedUp>]
[Concurrency(10)]
[CancelOn<UserDeleted>(Match = "data.userId")]
public sealed class OnboardUser(IEmailSender email) : InngestFunction<UserSignedUp>, IFailureHandler
{
    public override async Task<object?> ExecuteAsync(FunctionContext<UserSignedUp> context, CancellationToken cancellationToken)
    {
        var user = context.Event.Data;

        var welcomeId = await context.Step.RunAsync("send-welcome-email",
            ct => email.SendAsync(user.Email, "Welcome!", ct));

        var activated = await context.Step.WaitForEventAsync<UserActivated>("wait-for-activation",
            TimeSpan.FromDays(1), match: "data.userId");

        if (activated is null)
        {
            await context.Step.RunAsync("send-reminder", ct => email.SendAsync(user.Email, "Still there?", ct));
            return new { user.UserId, activated = false, welcomeId };
        }

        await context.Step.SleepAsync("pause-before-tips", TimeSpan.FromSeconds(5));
        await context.Step.RunAsync("send-tips", ct => email.SendAsync(user.Email, "Getting started", ct));
        return new { user.UserId, activated = true, welcomeId };
    }

    public Task OnFailureAsync(FunctionContext<FunctionFailure> context, CancellationToken cancellationToken)
    {
        context.Logger.LogError("Onboarding run {RunId} failed: {Message}", context.Event.Data.RunId, context.Event.Data.Error.Message);
        return Task.CompletedTask;
    }
}
