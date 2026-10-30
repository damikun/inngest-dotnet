using System.Net;
using System.Text;
using Microsoft.Azure.Functions.Worker.Http;

namespace Inngest.Tests.AzureFunctions.Fakes;

// A response as the worker keeps it until the function returns: a status, headers and a MemoryStream for the body.
internal sealed class FakeHttpResponseData(Microsoft.Azure.Functions.Worker.FunctionContext functionContext) : HttpResponseData(functionContext)
{
    public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

    public override HttpHeadersCollection Headers { get; set; } = new();

    public override Stream Body { get; set; } = new MemoryStream();

    public override HttpCookies Cookies => throw new NotSupportedException();

    public byte[] BodyBytes => ((MemoryStream)Body).ToArray();

    public string BodyText => Encoding.UTF8.GetString(BodyBytes);

    // The value the worker would send for the header: several values are joined with commas.
    public string? Header(string name) => Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
}
