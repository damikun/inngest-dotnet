using Inngest;

// An event is a record that carries its own name, so sending it and triggering a function with it need no string.
public sealed record HelloRequested(string Name) : IInngestEventData
{
    public static string EventName => "app/hello.requested";
}
