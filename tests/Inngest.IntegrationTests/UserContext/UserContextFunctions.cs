using Inngest.IntegrationTests.Harness;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.UserContext;

public sealed record UserEvent(string TestId);

public sealed record ReportResult(string Direct, string InStep, string Child, string RunId);

// The functions of the user context scenarios, in an app that restores users and shares its Data Protection key ring
// and application name with the web app that sends them.
internal static class UserContextFunctions
{
    public const string AppId = "itests-user";
    public const string ApplicationName = "itests-user-context";

    public const string Report = "itests/user.report";
    public const string Followup = "itests/user.followup";
    public const string Child = "itests/user.child";
    public const string Failing = "itests/user.failing";

    // The ids the server must list once the app is synced.
    public static IReadOnlyList<string> Ids { get; } = ["report", "followup", "child", "failing", "failing-failure"];

    public static void Add(IInngestBuilder inngest, DirectoryInfo keys)
    {
        inngest.Services.AddDataProtection().SetApplicationName(ApplicationName).PersistKeysToFileSystem(keys);
        inngest.AddUserContext(options => options.Claims = ["sub", "name", "role"]);

        // Reads the user at the top of the function, after a sleep, inside a step, and passes it on in an event and
        // an invocation. The top of the function runs again on every call of the run.
        inngest.AddFunction<UserEvent>(
            new FunctionOptions { Id = "report", Triggers = [Trigger.Event(Report)] },
            async (context, _) =>
            {
                var probe = context.Services.GetRequiredService<Probe>().For(context.Event.Data.TestId);
                var user = context.Services.GetRequiredService<ICurrentUser>();
                probe.Record("start", Describe(user));
                await context.Step.SleepAsync("pause", TimeSpan.FromSeconds(2));
                probe.Record("after-sleep", Describe(user));
                var inStep = await context.Step.RunAsync("in-step", () => Task.FromResult(Describe(user)));
                await context.Step.SendEventAsync("notify", [new InngestEvent(Followup, new UserEvent(context.Event.Data.TestId))]);
                var child = await context.Step.InvokeAsync<string>(
                    "ask",
                    new InvokeOptions("child") { Data = new UserEvent(context.Event.Data.TestId), Timeout = TimeSpan.FromMinutes(1) }.WithUser(context));
                return new ReportResult(Describe(user), inStep, child, context.RunId);
            });

        inngest.AddFunction<UserEvent>(
            new FunctionOptions { Id = "followup", Triggers = [Trigger.Event(Followup)] },
            (context, _) =>
            {
                var user = context.Services.GetRequiredService<ICurrentUser>();
                context.Services.GetRequiredService<Probe>().For(context.Event.Data.TestId).Record("followup", Describe(user));
                return Task.FromResult<object?>("ok");
            });

        // Only invoked. It still has an event trigger, which nothing sends.
        inngest.AddFunction<UserEvent>(
            new FunctionOptions { Id = "child", Triggers = [Trigger.Event(Child)] },
            (context, _) =>
            {
                var user = context.Services.GetRequiredService<ICurrentUser>();
                context.Services.GetRequiredService<Probe>().For(context.Event.Data.TestId).Record("child", Describe(user));
                return Task.FromResult<object?>(Describe(user));
            });

        inngest.AddFunction<UserEvent>(
            new FunctionOptions { Id = "failing", Triggers = [Trigger.Event(Failing)], Retries = 0 },
            (_, _) => throw new NonRetriableException("the user context scenario fails on purpose"),
            (context, _) =>
            {
                var user = context.Services.GetRequiredService<ICurrentUser>();
                var testId = context.Event.Data.Event.GetProperty("data").GetProperty("testId").GetString()!;
                context.Services.GetRequiredService<Probe>().For(testId).Record("failure-handler", Describe(user));
                return Task.CompletedTask;
            });
    }

    // What a function saw as its user, as one line the tests compare.
    public static string Describe(ICurrentUser user) =>
        user.IsAuthenticated
            ? $"{user.Principal.Identity?.Name}|{string.Join(",", user.Principal.Claims.Select(claim => $"{claim.Type}={claim.Value}"))}|admin={user.Principal.IsInRole("admin")}"
            : "nobody";
}
