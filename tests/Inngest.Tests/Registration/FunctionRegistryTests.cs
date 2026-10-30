using Inngest.Functions;
using Inngest.Internal;
using Inngest.Registration;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using static Inngest.Tests.Registration.RegistryFixtures;

namespace Inngest.Tests.Registration;

public sealed class FunctionRegistryTests
{
    [Fact]
    public void Finds_a_function_by_the_app_id_and_the_function_id()
    {
        var orders = Define(Orders);
        var registry = new FunctionRegistry([orders, Define(Orders with { Id = "refunds" })], Settings());

        registry.TryGet("my-app-orders", out var found).ShouldBeTrue();

        found.ShouldBeSameAs(orders);
    }

    [Theory]
    [InlineData("orders")]
    [InlineData("My-App-orders")]
    [InlineData("my-app-Orders")]
    [InlineData("other-app-orders")]
    [InlineData("my-app-orders ")]
    [InlineData("my-app-")]
    [InlineData("")]
    public void Does_not_find_anything_else(string compositeId)
    {
        var registry = Registry(Orders);

        registry.TryGet(compositeId, out _).ShouldBeFalse();
    }

    [Fact]
    public void Resolves_the_function_id_of_every_step_url_it_writes()
    {
        var registry = new FunctionRegistry(
            [Define(Orders with { Id = "Send Email/2" }), Define(Orders with { Id = "a&b=c" })],
            Settings("Shop App"));

        foreach (var step in StepUrls(Write(registry)))
        {
            var fnId = QueryHelpers.ParseQuery(new Uri(step).Query)["fnId"].ToString();

            registry.TryGet(fnId, out _).ShouldBeTrue();
        }
    }

    [Fact]
    public void Lists_functions_in_the_order_they_were_registered()
    {
        var first = Define(Orders with { Id = "b" });
        var second = Define(Orders with { Id = "a" });
        var registry = new FunctionRegistry([first, second], Settings());

        registry.Functions.ShouldBe([first, second]);
    }

    [Fact]
    public void Starts_with_no_functions()
    {
        var registry = new FunctionRegistry([], Settings());

        registry.Functions.ShouldBeEmpty();
        registry.TryGet("my-app-orders", out _).ShouldBeFalse();
    }

    [Fact]
    public void Does_not_change_when_the_source_collection_does()
    {
        var definitions = new List<FunctionDefinition> { Define(Orders) };
        var registry = new FunctionRegistry(definitions, Settings());

        definitions.Add(Define(Orders with { Id = "refunds" }));

        registry.Functions.Count.ShouldBe(1);
        registry.TryGet("my-app-refunds", out _).ShouldBeFalse();
    }

    [Fact]
    public void Looks_functions_up_from_many_threads()
    {
        var registry = Registry(Orders, Orders with { Id = "refunds" });
        var found = 0;

        Parallel.For(0, 1_000, i =>
        {
            if (registry.TryGet(i % 2 == 0 ? "my-app-orders" : "my-app-refunds", out _))
            {
                Interlocked.Increment(ref found);
            }
        });

        found.ShouldBe(1_000);
    }

    [Fact]
    public void Is_built_from_the_definitions_registered_in_the_container()
    {
        using var provider = new ServiceCollection()
            .AddSingleton(Settings())
            .AddSingleton(Define(Orders))
            .AddSingleton(Define(Orders with { Id = "refunds" }))
            .AddSingleton<IFunctionRegistry, FunctionRegistry>()
            .BuildServiceProvider();

        var registry = provider.GetRequiredService<IFunctionRegistry>();

        registry.Functions.Select(function => function.Id).ShouldBe(["orders", "refunds"]);
    }

    [Fact]
    public void Rejects_two_functions_with_the_same_id()
    {
        var exception = Should.Throw<InvalidOperationException>(() => Registry(Orders, Orders with { Name = "Again" }));

        exception.Message.ShouldBe("Function \"orders\" is registered more than once. Function ids must be unique within an app.");
    }

    [Fact]
    public void Rejects_an_id_that_a_failure_handler_already_uses()
    {
        var taken = Orders with { Id = "orders-failure" };

        var exception = Should.Throw<InvalidOperationException>(
            () => new FunctionRegistry([.. WithFailureHandler(Orders), Define(taken)], Settings()));

        exception.Message.ShouldContain("\"orders-failure\" is registered more than once");
    }

    [Fact]
    public void Lists_the_problems_of_every_function_and_every_repeated_id_at_once()
    {
        var exception = Should.Throw<InvalidOperationException>(() => Registry(
            Orders with { Retries = 21 },
            Orders with { Id = "refunds", Throttle = new Throttle(0, TimeSpan.FromMinutes(1)) },
            Orders with { Name = "Again" }));

        exception.Message.ShouldBe(string.Join(
            Environment.NewLine,
            "The function configuration has 3 problems:",
            "1. Function \"orders\": Retries must be between 0 and 20, but was 21.",
            "2. Function \"refunds\": Throttle.Limit must be greater than 0, but was 0.",
            "3. Function \"orders\" is registered more than once. Function ids must be unique within an app."));
    }

    [Fact]
    public void Functions_without_an_id_are_not_also_reported_as_registered_twice()
    {
        var exception = Should.Throw<InvalidOperationException>(() => Registry(Orders with { Id = "" }, Orders with { Id = "", Name = "Refunds" }));

        exception.Message.ShouldBe(string.Join(
            Environment.NewLine,
            "The function configuration has 2 problems:",
            "1. A function has no Id. Set FunctionOptions.Id to an id that is unique in the app and never changes.",
            "2. The function named \"Refunds\" has no Id. Set FunctionOptions.Id to an id that is unique in the app and never changes."));
    }

    [Fact]
    public void Ids_that_differ_only_in_case_are_different_functions()
    {
        var registry = Registry(Orders, Orders with { Id = "Orders" });

        registry.TryGet("my-app-orders", out _).ShouldBeTrue();
        registry.TryGet("my-app-Orders", out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Rejects_an_empty_id(string id)
    {
        var exception = Should.Throw<InvalidOperationException>(() => Registry(Orders with { Id = id, Name = "Process orders" }));

        exception.Message.ShouldBe("The function named \"Process orders\" has no Id. Set FunctionOptions.Id to an id that is unique in the app and never changes.");
    }

    [Fact]
    public void Rejects_invalid_options_when_the_registry_is_built_and_names_the_function()
    {
        var exception = Should.Throw<InvalidOperationException>(
            () => Registry(Orders, Orders with { Id = "refunds", Retries = 21 }));

        exception.Message.ShouldBe("Function \"refunds\": Retries must be between 0 and 20, but was 21.");
    }

    [Fact]
    public void Checks_the_options_of_a_failure_handler_too()
    {
        var failing = Orders with { Id = "refunds", Retries = 0 };
        var handler = FailureFunction.CreateOptions(failing) with { Retries = 99 };

        var exception = Should.Throw<InvalidOperationException>(
            () => new FunctionRegistry([Define(failing), Define(handler, failureHandlerFor: "refunds")], Settings()));

        exception.Message.ShouldBe("Function \"refunds-failure\": Retries must be between 0 and 20, but was 99.");
    }

    [Fact]
    public void Rejects_missing_arguments()
    {
        Should.Throw<ArgumentNullException>(() => new FunctionRegistry(null!, Settings()));
        Should.Throw<ArgumentNullException>(() => new FunctionRegistry([], null!));
    }

    [Fact]
    public void Rejects_a_relative_serve_url()
    {
        var registry = Registry(Orders);
        using var writer = new PooledBufferWriter();

        Should.Throw<ArgumentException>(() => registry.WriteRegistration(writer, new Uri("/api/inngest", UriKind.Relative), "aspnetcore"))
            .ParamName.ShouldBe("serveUrl");
    }

    private static IEnumerable<string> StepUrls(string body) =>
        Parse(body).GetProperty("functions").EnumerateArray()
            .Select(function => function.GetProperty("steps").GetProperty("step").GetProperty("runtime").GetProperty("url").GetString()!);
}
