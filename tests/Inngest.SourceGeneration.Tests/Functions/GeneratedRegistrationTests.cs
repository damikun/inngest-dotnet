using System.Reflection;
using System.Runtime.Loader;
using Inngest.Functions;
using Inngest.SourceGeneration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.SourceGeneration.Tests.Functions;

// Compiles an app with the generator, runs the method it wrote against the real SDK and looks at what was registered.
public sealed class GeneratedRegistrationTests
{
    private const string Source = """
        using System.Threading;
        using System.Threading.Tasks;
        using Inngest;

        namespace Shop
        {
            public sealed record UserCreated(string Id) : IInngestEventData
            {
                public static string EventName => "shop/user.created";
            }

            [InngestFunction("welcome", Name = "Welcome a user", Retries = 2)]
            [EventTrigger<UserCreated>]
            [Concurrency(3)]
            public sealed class Welcome : InngestFunction<UserCreated, string>
            {
                public override Task<string> ExecuteAsync(FunctionContext<UserCreated> context, CancellationToken cancellationToken) =>
                    Task.FromResult("hello");
            }

            // Handles its own failure: AddFunction registers the second function, and so must the generated call.
            [InngestFunction("cleanup")]
            [CronTrigger("0 3 * * *")]
            internal sealed class Cleanup : InngestFunction<string>, IFailureHandler
            {
                public override Task<object?> ExecuteAsync(FunctionContext<string> context, CancellationToken cancellationToken) =>
                    Task.FromResult<object?>(null);

                public Task OnFailureAsync(FunctionContext<FunctionFailure> context, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            // Not registered: no attribute.
            public sealed class Helper : InngestFunction<string>
            {
                public override Task<object?> ExecuteAsync(FunctionContext<string> context, CancellationToken cancellationToken) =>
                    Task.FromResult<object?>(null);
            }
        }
        """;

    [Fact]
    public void The_generated_method_registers_the_classes_with_the_options_their_attributes_give()
    {
        var services = Register(Source, "GeneratedShop");

        var functions = FunctionsIn(services);
        functions.Select(static function => function.Id).ShouldBe(["cleanup", "cleanup-failure", "welcome"]);

        var welcome = functions.Single(static function => function.Id == "welcome");
        welcome.FunctionType!.FullName.ShouldBe("Shop.Welcome");
        welcome.Options.Name.ShouldBe("Welcome a user");
        welcome.Options.Retries.ShouldBe(2);
        welcome.Options.Triggers.ShouldHaveSingleItem().ShouldBeOfType<EventTrigger>().Event.ShouldBe("shop/user.created");
        welcome.Options.Concurrency.ShouldHaveSingleItem().Limit.ShouldBe(3);

        var cleanup = functions.Single(static function => function.Id == "cleanup");
        cleanup.Options.Triggers.ShouldHaveSingleItem().ShouldBeOfType<CronTrigger>().Schedule.ShouldBe("0 3 * * *");

        functions.Single(static function => function.Id == "cleanup-failure").FailureHandlerFor.ShouldBe("cleanup");
    }

    [Fact]
    public void The_generated_method_returns_the_builder_so_that_registrations_can_be_chained()
    {
        var method = LoadMethod("GeneratedChain", Source);
        var builder = new ServiceCollection().AddInngest(options => options.AppId = "shop");

        method.Invoke(null, [builder]).ShouldBeSameAs(builder);
    }

    [Fact]
    public void The_generated_method_rejects_a_missing_builder()
    {
        var method = LoadMethod("GeneratedNull", Source);

        var thrown = Should.Throw<TargetInvocationException>(() => method.Invoke(null, [null]));

        thrown.InnerException.ShouldBeOfType<ArgumentNullException>().ParamName.ShouldBe("builder");
    }

    private static ServiceCollection Register(string source, string assemblyName)
    {
        var method = LoadMethod(assemblyName, source);
        var services = new ServiceCollection();
        method.Invoke(null, [services.AddInngest(options => options.AppId = "shop")]);
        return services;
    }

    private static List<FunctionDefinition> FunctionsIn(ServiceCollection services) =>
        [.. services
            .Where(static descriptor => descriptor.ServiceType == typeof(FunctionDefinition))
            .Select(static descriptor => (FunctionDefinition)descriptor.ImplementationInstance!)
            .OrderBy(static function => function.Id, StringComparer.Ordinal)];

    // Compiles the app with the generator's output and loads it into a context of its own, which resolves Inngest from the
    // default context: the SDK the tests run against is the one the generated code calls.
    private static MethodInfo LoadMethod(string assemblyName, string source)
    {
        var compilation = Generation.Compile(assemblyName, source);
        Generation.CreateDriver().RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        diagnostics.ShouldBeEmpty();

        using var stream = new MemoryStream();
        var emitted = output.Emit(stream);
        emitted.Success.ShouldBeTrue(string.Join(Environment.NewLine, emitted.Diagnostics));

        stream.Position = 0;
        var assembly = new AssemblyLoadContext(assemblyName).LoadFromStream(stream);
        return assembly.GetType($"Inngest.InngestFunctionRegistration_{assemblyName}", throwOnError: true)!
            .GetMethod("AddInngestFunctions", BindingFlags.Static | BindingFlags.NonPublic)!;
    }
}
