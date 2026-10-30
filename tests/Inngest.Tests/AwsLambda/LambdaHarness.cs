using System.Text;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using Inngest.Configuration;
using Inngest.Signing;
using Inngest.Tests.Serving;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.AwsLambda;

// The Lambda handler over the serve handler's fakes, so that tests see what reaches the engine and the registry.
internal sealed class LambdaHarness
{
    public CommHandlerHarness Serving { get; } = new();

    public TestLambdaContext Context { get; } = new() { RemainingTime = TimeSpan.FromSeconds(30) };

    public InngestLambdaHandler Create(InngestSettings settings)
    {
        var services = new ServiceCollection().AddSingleton(Serving.CreateHandler(settings)).BuildServiceProvider();
        return new InngestLambdaHandler(services);
    }

    // The signature header Inngest would send for a body, made with the key the settings verify against.
    public string Sign(string body, SigningKey? key = null) =>
        Serving.Signer.Sign(Encoding.UTF8.GetBytes(body), key ?? CommHandlerHarness.PrimaryKey);

    // Syncs the app with a PUT and returns the URL it registered, which is the URL the serve handler thinks it is
    // served at. Each call forgets the registrations of the one before.
    public async Task<Uri> SyncUrlAsync(APIGatewayProxyRequest request, InngestSettings? settings = null)
    {
        Serving.Registry.Registrations.Clear();
        var response = await Create(settings ?? CommHandlerHarness.DevSettings).HandleAsync(request, Context);

        response.StatusCode.ShouldBe(200);
        return RegisteredUrl();
    }

    public async Task<Uri> SyncUrlAsync(APIGatewayHttpApiV2ProxyRequest request, InngestSettings? settings = null)
    {
        Serving.Registry.Registrations.Clear();
        var response = await Create(settings ?? CommHandlerHarness.DevSettings).HandleAsync(request, Context);

        response.StatusCode.ShouldBe(200);
        return RegisteredUrl();
    }

    public Uri RegisteredUrl()
    {
        var (serveUrl, framework) = Serving.Registry.Registrations.ShouldHaveSingleItem();
        framework.ShouldBe("aws-lambda");
        return serveUrl;
    }
}
