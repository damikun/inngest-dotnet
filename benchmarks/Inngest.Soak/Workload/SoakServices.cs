using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Soak.Workload;

internal static class SoakServices
{
    public const string AppId = "soak";
    public const string CheckoutCompositeId = AppId + "-" + Checkout.Id;

    // The SDK as an app registers it, with the checkout function and a middleware. Event sends go to a stub
    // instead of the network, so that only the SDK's own work is measured.
    public static IInngestBuilder AddSoakApp(this IServiceCollection services, Action<InngestOptions> configure)
    {
        var builder = services
            .AddInngest(options =>
            {
                options.AppId = AppId;
                configure(options);
            })
            .AddFunction<Checkout>(new FunctionOptions { Id = Checkout.Id, Triggers = [Trigger.Event(Checkout.EventName)] })
            .AddMiddleware<StepTimingMiddleware>();
        services.AddHttpClient("Inngest").ConfigurePrimaryHttpMessageHandler(static () => new EventApiStub());
        return builder;
    }
}

// Answers event sends the way the Inngest event API does, with one id per event in the body.
internal sealed class EventApiStub : HttpMessageHandler
{
    private static long _sends;

    public static long Sends => Interlocked.Read(ref _sends);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var events = body.AsSpan().Count("\"name\""u8);
        var number = Interlocked.Increment(ref _sends);
        var ids = string.Join(',', Enumerable.Range(0, events).Select(index => $"\"01JSTUB{number:D12}{index:D4}\""));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"ids":[{{ids}}],"status":200}""", Encoding.UTF8, "application/json"),
        };
    }
}
