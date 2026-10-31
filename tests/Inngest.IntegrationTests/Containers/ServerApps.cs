using System.Net.Http.Json;
using System.Text.Json;
using Inngest.Testing.Containers;

namespace Inngest.IntegrationTests.Containers;

// How the guide has a test wait for an app that the container is to sync: the GraphQL API of the Dev UI lists the apps the
// server has synced. The guide shows this loop, so that it stays checked against a real server.
internal static class ServerApps
{
    public static async Task WaitForAsync(InngestServerContainer server, string appId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        using var http = new HttpClient { BaseAddress = server.GetBaseAddress() };
        while (true)
        {
            using var response = await http.PostAsJsonAsync("v0/gql", new { query = "{ apps { externalID functionCount } }" }, timeout.Token);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            var apps = body.GetProperty("data").GetProperty("apps").EnumerateArray();
            if (apps.Any(app => app.GetProperty("externalID").GetString() == appId && app.GetProperty("functionCount").GetInt32() > 0))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }
    }
}
