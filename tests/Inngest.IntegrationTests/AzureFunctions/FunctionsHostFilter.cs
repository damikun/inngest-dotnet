using Inngest.IntegrationTests.Harness;
using Inngest.Tests.AzureFunctions.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.AzureFunctions;

// The two ways the isolated worker hands an HTTP request to a function.
public enum FunctionsHttpModel
{
    // HttpRequestData and HttpResponseData: the worker has the whole request in memory and keeps the response until the
    // function returns.
    BuiltIn,

    // ConfigureFunctionsWebApplication: the function runs inside a request of Kestrel, and its HttpContext is in the items of
    // the FunctionContext.
    AspNetCore,
}

// Stands in for the Functions host and the worker in front of the app under test. A request for the route of the function
// that serves Inngest is handed to that function the way the worker hands it, and what the function returns goes back to the
// caller; every other request goes on to the app. TestApp maps MapInngest at the same route behind this, which no request
// reaches.
internal sealed class FunctionsHostFilter(FunctionsHttpModel model) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, pipeline) =>
        {
            if (context.Request.Path != TestApp.ServePath)
            {
                await pipeline(context);
                return;
            }

            // Through the request log of the harness, which records what the app answered.
            var log = context.RequestServices.GetRequiredService<RequestLog>();
            await log.InvokeAsync(context, model == FunctionsHttpModel.BuiltIn ? ServeBuiltInAsync : ServeAspNetCoreAsync);
        });
        next(app);
    };

    // What the host sends the worker as the URL of the request: the scheme, the host the caller used, the path and the query.
    private static string UrlOf(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.Path}{request.QueryString}";

    private static async Task ServeBuiltInAsync(HttpContext context)
    {
        var request = context.Request;

        // The host sends the worker the whole body in one message, and only when the request declares a length.
        byte[] body = [];
        if (request.ContentLength > 0)
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, context.RequestAborted);
            body = buffer.ToArray();
        }

        // The worker creates a scope for every invocation, and the host has put the header names in lower case.
        await using var scope = context.RequestServices.CreateAsyncScope();
        var functionContext = new FakeFunctionContext(scope.ServiceProvider, context.RequestAborted);
        var data = new FakeHttpRequestData(functionContext, request.Method, UrlOf(request), FakeHttpRequestData.BodyOf(body));
        foreach (var header in request.Headers)
        {
            data.WithHeader(header.Key, header.Value.ToString());
        }

        // The function of the app: it serves Inngest from the request it gets.
        var response = (FakeHttpResponseData)await data.ServeInngestAsync();

        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var (name, values) in response.Headers)
        {
            context.Response.Headers[name] = string.Join(",", values);
        }

        await context.Response.Body.WriteAsync(response.BodyBytes, context.RequestAborted);
    }

    private static async Task ServeAspNetCoreAsync(HttpContext context)
    {
        // The integration runs the function inside the request, in the services of the invocation, which the request then
        // uses too.
        var functionContext = new FakeFunctionContext(context.RequestServices, context.RequestAborted);
        functionContext.Items["HttpRequestContext"] = context;
        var data = new FakeHttpRequestData(functionContext, context.Request.Method, UrlOf(context.Request), context.Request.Body);

        // The function of the app. The answer is on the response of the request when it returns.
        await data.ServeInngestAsync();
    }
}
