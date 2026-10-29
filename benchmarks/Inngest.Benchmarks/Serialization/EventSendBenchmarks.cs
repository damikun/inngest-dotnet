using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Inngest.Benchmarks.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Benchmarks.Serialization;

// A send through the client, up to an in-memory HTTP handler that answers right away. The client writes the body in a
// private method, so the request, the HTTP client and the reply are part of the number. The difference between the
// two sizes is the cost of the extra events.
[MemoryDiagnoser]
public class EventSendBenchmarks
{
    private ServiceProvider _provider = null!;
    private IInngestClient _client = null!;
    private InngestEvent[] _events = null!;

    [Params(1, 100)]
    public int Events { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var handler = new SendEventsHandler(CreateReply(Events));
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
            options.EventKey = "bench-event-key";
        });

        // One handler for the life of the benchmark, so that the factory never rotates it.
        services.AddHttpClient("Inngest").ConfigurePrimaryHttpMessageHandler(() => handler).SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        _provider = services.BuildServiceProvider();
        _client = _provider.GetRequiredService<IInngestClient>();
        _events = [.. Enumerable.Range(0, Events).Select(static index => new InngestEvent(Samples.EventName, Samples.Order(index)) { User = new UserRef($"usr_{index:D6}") })];

        handler.Capture = true;
        var sent = await _client.SendAsync(_events);
        handler.Capture = false;

        Expect.Equal(Events, sent.Ids.Count, "the number of event ids returned");
        Expect.Equal("/e/bench-event-key", handler.Url!.AbsolutePath, "the path of the send");
        using var body = JsonDocument.Parse(handler.Body!);
        Expect.Equal(Events, body.RootElement.GetArrayLength(), "the number of events in the body");
        var index = 0;
        foreach (var @event in body.RootElement.EnumerateArray())
        {
            Expect.Equal(Samples.EventName, Expect.Property(@event, "name").GetString(), "the event name");
            Expect.Equal(JsonSerializer.Serialize(Samples.Order(index), JsonSerializerOptions.Web), Expect.Property(@event, "data").GetRawText(), "the event data");
            Expect.Equal($$"""{"id":"usr_{{index:D6}}"}""", Expect.Property(@event, "user").GetRawText(), "the event user");
            Expect.That(Expect.Property(@event, "ts").TryGetInt64(out _), "the event has a timestamp");
            index++;
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _provider.Dispose();

    [Benchmark]
    public async ValueTask<int> SendEvents() => (await _client.SendAsync(_events)).Ids.Count;

    // What the event API answers: one id per event, and a status in the body.
    private static byte[] CreateReply(int count) =>
        Encoding.UTF8.GetBytes($$"""{"ids":[{{string.Join(',', Enumerable.Range(0, count).Select(static index => $"\"01JC8Y3N5W9X2K4M7P6Q{index:D6}\""))}}],"status":200}""");
}
