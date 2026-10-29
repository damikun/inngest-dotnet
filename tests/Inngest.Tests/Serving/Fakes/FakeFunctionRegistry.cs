using System.Text;
using Inngest.Functions;
using Inngest.Internal;
using Inngest.Registration;

namespace Inngest.Tests.Serving.Fakes;

// Two functions, "send-email" and its failure handler, in the app "shop". The registration body is fixed apart from
// the URL and framework, so tests can check exactly what the handler did with it.
internal sealed class FakeFunctionRegistry : IFunctionRegistry
{
    public const string FunctionsJson =
        """[{"id":"shop-send-email","name":"Send email","triggers":[{"event":"user/signed.up"}]},{"id":"shop-send-email-failure","name":"Send email (failure)","triggers":[{"event":"inngest/function.failed","expression":"event.data.function_id == 'shop-send-email'"}]}]""";

    public List<FunctionDefinition> Definitions { get; } = [Create("send-email"), Create("send-email-failure")];

    public List<(Uri ServeUrl, string Framework)> Registrations { get; } = [];

    public Exception? Failure { get; set; }

    public IReadOnlyList<FunctionDefinition> Functions => Definitions;

    public static string RegistrationJson(Uri serveUrl, string framework) =>
        $$"""{"url":"{{serveUrl.AbsoluteUri}}","deployType":"ping","appName":"shop","sdk":"{{SdkInfo.RegistrationValue}}","v":"0.1","framework":"{{framework}}","capabilities":{"trust_probe":"v1"},"functions":{{FunctionsJson}}}""";

    public bool TryGet(string compositeId, out FunctionDefinition function)
    {
        foreach (var definition in Definitions)
        {
            if (compositeId == "shop-" + definition.Id)
            {
                function = definition;
                return true;
            }
        }

        function = null!;
        return false;
    }

    // The fake's functions are delegates, so none can be found by a class.
    public string FunctionIdOf(Type functionType) =>
        throw new InvalidOperationException($"No function is registered as {functionType.Name}.");

    public void WriteRegistration(PooledBufferWriter writer, Uri serveUrl, string framework, string? deployType = null)
    {
        Registrations.Add((serveUrl, framework));
        if (Failure is not null)
        {
            throw Failure;
        }

        writer.Write(Encoding.UTF8.GetBytes(RegistrationJson(serveUrl, framework)));
    }

    private static FunctionDefinition Create(string id) =>
        new(new FunctionOptions { Id = id, Triggers = [Trigger.Event("user/signed.up")] }, _ => throw new NotSupportedException());
}
