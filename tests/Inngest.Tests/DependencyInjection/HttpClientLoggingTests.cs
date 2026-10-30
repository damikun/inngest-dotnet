using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.DependencyInjection;

public sealed class HttpClientLoggingTests
{
    // The URL of an event send holds the event key, and the default HttpClient logging writes URLs at Information.
    [Fact]
    public async Task The_sdk_http_client_does_not_log_the_urls_it_calls()
    {
        var services = new ServiceCollection();
        services.AddFakeLogging();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace));
        services.AddInngest(options => options.AppId = "shop");
        services.AddHttpClient(InngestServiceCollectionExtensions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new Answers200());
        await using var provider = services.BuildServiceProvider();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(InngestServiceCollectionExtensions.HttpClientName);
        using var response = await client.PostAsync(new Uri("https://inn.gs/e/the-event-key"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        provider.GetFakeLogCollector().GetSnapshot().ShouldNotContain(record => record.Message.Contains("the-event-key"));
    }

    private sealed class Answers200 : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
