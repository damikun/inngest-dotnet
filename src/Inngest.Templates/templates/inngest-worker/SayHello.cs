using Inngest;

// The attributes give the function its id and the event that starts it. A step runs once and Inngest saves its result,
// so a retry repeats only the step that failed.
[InngestFunction("say-hello", Name = "Say hello")]
[EventTrigger<HelloRequested>]
public sealed class SayHello : InngestFunction<HelloRequested>
{
    public override async Task<object?> ExecuteAsync(FunctionContext<HelloRequested> context, CancellationToken cancellationToken)
    {
        var greeting = await context.Step.RunAsync("compose-greeting", () => $"Hello, {context.Event.Data.Name}!");

        context.Logger.LogInformation("{Greeting}", greeting);
        return new { greeting };
    }
}
