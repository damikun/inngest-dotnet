using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Inngest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var services = new ServiceCollection();
services.AddLogging(logging => logging.AddConsole());
services
    .AddInngest(options =>
    {
        options.AppId = "shop";
        options.SerializerOptions.TypeInfoResolverChain.Insert(0, FunctionJson.Default);
    })
    .AddFunction<SendWelcomeEmail>();

await using var provider = services.BuildServiceProvider();
var inngest = new InngestLambdaHandler(provider);

Func<APIGatewayHttpApiV2ProxyRequest, ILambdaContext, Task<APIGatewayHttpApiV2ProxyResponse>> handler = inngest.HandleAsync;
await LambdaBootstrapBuilder
    .Create(handler, new SourceGeneratorLambdaJsonSerializer<LambdaJson>())
    .Build()
    .RunAsync();
