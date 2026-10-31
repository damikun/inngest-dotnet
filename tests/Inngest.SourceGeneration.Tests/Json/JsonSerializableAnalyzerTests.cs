using Inngest.SourceGeneration.Json;
using Inngest.SourceGeneration.Tests.Support;
using Microsoft.CodeAnalysis.Testing;

namespace Inngest.SourceGeneration.Tests.Json;

public sealed class JsonSerializableAnalyzerTests
{
    // The types the tests below use. Only Order is listed, and the types it holds come with it.
    private const string Types = """
        namespace Shop
        {
            public sealed record Line(int Quantity);

            public sealed record Order(string Id, Line Line, List<string> Notes);

            public sealed record Receipt(Guid Id);

            [JsonSerializable(typeof(Order))]
            internal sealed partial class AppJson : JsonSerializerContext;

        """;

    [Theory]
    [InlineData(null)]
    [InlineData("is_global = true\nbuild_property.PublishAot = false\n")]
    [InlineData("is_global = true\nbuild_property.PublishAot = true\nbuild_property.JsonSerializerIsReflectionEnabledByDefault = true\n")]
    [InlineData("is_global = true\nbuild_property.JsonSerializerIsReflectionEnabledByDefault = true\nbuild_property.PublishTrimmed = true\n")]
    public async Task An_app_that_keeps_reflection_on_has_nothing_to_report(string? properties)
    {
        const string source = Types + """
                [InngestFunction("order")]
                public sealed class OnOrder : InngestFunction<Order, Receipt>
                {
                    public override Task<Receipt> ExecuteAsync(FunctionContext<Order> context, CancellationToken cancellationToken) =>
                        context.Step.RunAsync("receipt", () => new Receipt(Guid.NewGuid()));
                }
            }
            """;

        await Verify(source, properties);
    }

    [Theory]
    [InlineData("is_global = true\nbuild_property.PublishAot = true\n")]
    [InlineData("is_global = true\nbuild_property.PublishTrimmed = true\n")]
    [InlineData("is_global = true\nbuild_property.JsonSerializerIsReflectionEnabledByDefault = false\n")]
    [InlineData("is_global = true\nbuild_property.PublishAot = true\nbuild_property.JsonSerializerIsReflectionEnabledByDefault = false\n")]
    public async Task An_app_that_turns_reflection_off_is_checked(string properties)
    {
        const string source = Types + """
                [InngestFunction("order")]
                public sealed class OnOrder : InngestFunction<Order, {|#0:Receipt|}>
                {
                    public override Task<Receipt> ExecuteAsync(FunctionContext<Order> context, CancellationToken cancellationToken) =>
                        Task.FromResult(new Receipt(Guid.NewGuid()));
                }
            }
            """;

        await Verify(source, properties, Missing(0, "Shop.Receipt", "the result of function 'Shop.OnOrder'"));
    }

    [Fact]
    public async Task Reports_the_event_data_and_the_result_of_a_function_class_where_they_are_named()
    {
        const string source = Types + """
                public sealed record Refund(string OrderId);

                [InngestFunction("refund")]
                public sealed class OnRefund : InngestFunction<{|#0:Refund|}, {|#1:Receipt|}>
                {
                    public override Task<Receipt> ExecuteAsync(FunctionContext<Refund> context, CancellationToken cancellationToken) =>
                        Task.FromResult(new Receipt(Guid.NewGuid()));
                }

                // The result of a function without one is object, which the SDK serializes itself.
                [InngestFunction("audit")]
                public sealed class OnAudit : InngestFunction<{|#2:Refund|}>
                {
                    public override Task<object?> ExecuteAsync(FunctionContext<Refund> context, CancellationToken cancellationToken) =>
                        Task.FromResult<object?>(null);
                }
            }
            """;

        await Verify(
            source,
            Sources.PublishAot,
            Missing(0, "Shop.Refund", "the event data of function 'Shop.OnRefund'"),
            Missing(1, "Shop.Receipt", "the result of function 'Shop.OnRefund'"),
            Missing(2, "Shop.Refund", "the event data of function 'Shop.OnAudit'"));
    }

    [Fact]
    public async Task A_listed_type_and_the_types_it_holds_are_covered()
    {
        const string source = Types + """
                [InngestFunction("order")]
                public sealed class OnOrder : InngestFunction<Order, Line>
                {
                    public override async Task<Line> ExecuteAsync(FunctionContext<Order> context, CancellationToken cancellationToken)
                    {
                        // Line is a property of Order, List<string> and string are properties too, and so is the int in Line.
                        var notes = await context.Step.RunAsync("notes", () => new List<string>());
                        var id = await context.Step.RunAsync("id", () => context.Event.Data.Id);
                        var quantity = await context.Step.RunAsync("quantity", () => context.Event.Data.Line.Quantity);
                        return context.Event.Data.Line;
                    }
                }
            }
            """;

        await Verify(source, Sources.PublishAot);
    }

    [Fact]
    public async Task A_type_is_covered_only_by_a_context_that_reaches_it()
    {
        // Order holds a Line, not a list of them, and nothing holds a Guid or a nullable int.
        const string source = Types + """
                [InngestFunction("order")]
                public sealed class OnOrder : InngestFunction<Order>
                {
                    public override async Task<object?> ExecuteAsync(FunctionContext<Order> context, CancellationToken cancellationToken)
                    {
                        await context.Step.{|#0:RunAsync|}("lines", () => new List<Line>());
                        await context.Step.{|#1:RunAsync|}("guid", () => Guid.NewGuid());
                        await context.Step.{|#2:RunAsync|}<int?>("quantity", () => 3);
                        return null;
                    }
                }
            }
            """;

        await Verify(
            source,
            Sources.PublishAot,
            Missing(0, "System.Collections.Generic.List<Shop.Line>", "the result of a step"),
            Missing(1, "System.Guid", "the result of a step"),
            Missing(2, "int?", "the result of a step"));
    }

    [Fact]
    public async Task Listing_a_type_covers_every_use_of_it()
    {
        var source = Types.Replace(
            "[JsonSerializable(typeof(Order))]",
            "[JsonSerializable(typeof(Order))]\n    [JsonSerializable(typeof(List<Line>))]\n    [JsonSerializable(typeof(Guid))]\n    [JsonSerializable(typeof(int?))]",
            StringComparison.Ordinal) + """
                [InngestFunction("order")]
                public sealed class OnOrder : InngestFunction<Order>
                {
                    public override async Task<object?> ExecuteAsync(FunctionContext<Order> context, CancellationToken cancellationToken)
                    {
                        await context.Step.RunAsync("lines", () => new List<Line>());
                        await context.Step.RunAsync("guid", () => Guid.NewGuid());
                        await context.Step.RunAsync<int?>("quantity", () => 3);
                        return null;
                    }
                }
            }
            """;

        await Verify(source, Sources.PublishAot);
    }

    [Fact]
    public async Task Reports_the_result_of_every_form_of_RunAsync_that_has_one()
    {
        const string source = Types + """
                [InngestFunction("order")]
                public sealed class OnOrder : InngestFunction<Order>
                {
                    public override async Task<object?> ExecuteAsync(FunctionContext<Order> context, CancellationToken cancellationToken)
                    {
                        await context.Step.{|#0:RunAsync|}("sync", () => new Receipt(Guid.NewGuid()));
                        await context.Step.{|#1:RunAsync|}("async", async () => { await Task.Yield(); return new Receipt(Guid.NewGuid()); });
                        await context.Step.{|#2:RunAsync|}("token", async token => { await Task.Yield(); return new Receipt(Guid.NewGuid()); });
                        await context.Step.{|#3:RunAsync|}<Receipt>("explicit", _ => Task.FromResult(new Receipt(Guid.NewGuid())));
                        await context.Step.{|#4:RunAsync|}(new StepOptions("options") { Name = "With options" }, () => new Receipt(Guid.NewGuid()));

                        // No result: the step saves nothing the app has to read back.
                        await context.Step.RunAsync("action", () => { });
                        await context.Step.RunAsync("task", () => Task.CompletedTask);
                        await context.Step.RunAsync<object?>("object", () => Task.FromResult<object?>(null));
                        return null;
                    }
                }
            }
            """;

        var result = "the result of a step";
        await Verify(
            source,
            Sources.PublishAot,
            Missing(0, "Shop.Receipt", result),
            Missing(1, "Shop.Receipt", result),
            Missing(2, "Shop.Receipt", result),
            Missing(3, "Shop.Receipt", result),
            Missing(4, "Shop.Receipt", result));
    }

    [Fact]
    public async Task Reports_the_types_that_the_other_typed_steps_save_and_read_back()
    {
        const string source = Types + """
                public sealed record Paid(string OrderId) : IInngestEventData
                {
                    public static string EventName => "shop/order.paid";
                }

                public sealed record Approval(string Id);

                public sealed record Score(int Value);

                public sealed record Prompt(string Text);

                public sealed record Reply(string Text);

                [InngestFunction("scorer")]
                public sealed class Scorer : InngestFunction<Order, {|#0:Score|}>
                {
                    public override Task<Score> ExecuteAsync(FunctionContext<Order> context, CancellationToken cancellationToken) =>
                        Task.FromResult(new Score(1));
                }

                [InngestFunction("steps")]
                public sealed class Steps : InngestFunction<Order>
                {
                    public override async Task<object?> ExecuteAsync(FunctionContext<Order> context, CancellationToken cancellationToken)
                    {
                        await context.Step.{|#1:WaitForEventAsync|}<Paid>("paid", TimeSpan.FromMinutes(5));
                        await context.Step.{|#2:WaitForSignalAsync|}<Approval>("approval", new WaitForSignalOptions("approval", TimeSpan.FromMinutes(5)));
                        await context.Step.{|#3:InvokeAsync|}<Score>("score", new InvokeOptions("shop-scorer"));
                        await context.Step.{|#4:InvokeAsync|}<Scorer, Score>("typed");
                        await context.Step.{|#5:InferAsync|}<Reply>("infer", new InferOptions(AIFormat.OpenAIChat, new Uri("https://api.example.com"), new object()));
                        await context.Step.{|#6:AIWrapAsync|}<Prompt, Reply>("wrap", new Prompt("hi"), (prompt, token) => Task.FromResult(new Reply(prompt.Text)));

                        // The untyped forms read a JsonElement.
                        await context.Step.WaitForEventAsync("untyped", new WaitForEventOptions("shop/order.paid", TimeSpan.FromMinutes(5)));
                        await context.Step.InferAsync("untyped-infer", new InferOptions(AIFormat.OpenAIChat, new Uri("https://api.example.com"), new object()));
                        return null;
                    }
                }
            }
            """;

        await Verify(
            source,
            Sources.PublishAot,
            Missing(0, "Shop.Score", "the result of function 'Shop.Scorer'"),
            Missing(1, "Shop.Paid", "the data of an event that a step waits for"),
            Missing(2, "Shop.Approval", "the data of a signal that a step waits for"),
            Missing(3, "Shop.Score", "the result of an invoked function"),
            Missing(4, "Shop.Score", "the result of an invoked function"),
            Missing(5, "Shop.Reply", "the response of an inference step"),
            Missing(6, "Shop.Prompt", "the input of a step"),
            Missing(6, "Shop.Reply", "the result of a step"));
    }

    [Fact]
    public async Task The_other_typed_steps_are_fine_with_listed_types()
    {
        const string source = Types + """
                [InngestFunction("steps")]
                public sealed class Steps : InngestFunction<Order>
                {
                    public override async Task<object?> ExecuteAsync(FunctionContext<Order> context, CancellationToken cancellationToken)
                    {
                        await context.Step.WaitForSignalAsync<Order>("approval", new WaitForSignalOptions("approval", TimeSpan.FromMinutes(5)));
                        await context.Step.InvokeAsync<Order>("order", new InvokeOptions("shop-order"));
                        await context.Step.InferAsync<Line>("infer", new InferOptions(AIFormat.OpenAIChat, new Uri("https://api.example.com"), new object()));
                        await context.Step.AIWrapAsync<Line, Order>("wrap", context.Event.Data.Line, (line, token) => Task.FromResult(context.Event.Data));
                        return null;
                    }
                }
            }
            """;

        await Verify(source, Sources.PublishAot);
    }

    [Fact]
    public async Task Reports_the_data_that_a_step_publishes_to_a_realtime_topic()
    {
        const string source = Types + """
                [InngestFunction("publish")]
                public sealed class Publisher : InngestFunction<Order>
                {
                    public override async Task<object?> ExecuteAsync(FunctionContext<Order> context, CancellationToken cancellationToken)
                    {
                        await context.Step.{|#0:PublishAsync|}("receipt", new RealtimeTopic("orders", "receipts"), new Receipt(Guid.NewGuid()));
                        await context.Step.PublishAsync("order", new RealtimeTopic("orders", "orders"), context.Event.Data);
                        return null;
                    }
                }
            }
            """;

        await Verify(source, Sources.PublishAot, Missing(0, "Shop.Receipt", "the data a step publishes"));
    }

    [Fact]
    public async Task Does_not_ask_for_the_types_of_the_context_the_SDK_adds_even_when_the_apps_reaches_none_of_them()
    {
        // The app's context knows a record with an int. The SDK adds the context of its own types to the options of every
        // app, and that one has strings, lists of strings and the types it lists, so the app never has to.
        const string source = """
            namespace Shop
            {
                public sealed record Counter(int Value);

                [JsonSerializable(typeof(Counter))]
                internal sealed partial class AppJson : JsonSerializerContext;

                [InngestFunction("counter")]
                public sealed class OnCounter : InngestFunction<Counter>
                {
                    public override async Task<object?> ExecuteAsync(FunctionContext<Counter> context, CancellationToken cancellationToken)
                    {
                        await context.Step.RunAsync("text", () => "hello");
                        await context.Step.RunAsync("texts", () => (IReadOnlyList<string>)new List<string>());
                        await context.Step.RunAsync("failure", () => (FunctionFailure)null!);
                        await context.Step.RunAsync("sent", () => (SendEventsResult)null!);

                        // The SDK's context has nothing for a Guid, and neither has the app's.
                        await context.Step.{|#0:RunAsync|}("guid", () => Guid.NewGuid());
                        return null;
                    }
                }
            }
            """;

        await Verify(source, Sources.PublishAot, Missing(0, "System.Guid", "the result of a step"));
    }

    [Fact]
    public async Task Reports_the_event_data_of_a_function_registered_with_a_delegate()
    {
        const string source = Types + """
                public sealed class Registrations
                {
                    public static void Register(IInngestBuilder builder)
                    {
                        builder.{|#0:AddFunction|}<Receipt>(
                            new FunctionOptions { Id = "receipt", Triggers = [Trigger.Event("shop/receipt")] },
                            (context, cancellationToken) => Task.FromResult<object?>(null));

                        // Listed, and the untyped overload reads a JsonElement.
                        builder.AddFunction<Order>(
                            new FunctionOptions { Id = "order", Triggers = [Trigger.Event("shop/order")] },
                            (context, cancellationToken) => Task.FromResult<object?>(null));
                        builder.AddFunction(
                            new FunctionOptions { Id = "raw", Triggers = [Trigger.Event("shop/raw")] },
                            (context, cancellationToken) => Task.FromResult<object?>(null));
                    }
                }
            }
            """;

        await Verify(source, Sources.PublishAot, Missing(0, "Shop.Receipt", "the event data of a function registered with a delegate"));
    }

    [Fact]
    public async Task Does_not_ask_for_the_types_the_SDK_serializes_itself()
    {
        const string source = Types + """
                [InngestFunction("failure")]
                public sealed class OnFailure : InngestFunction<FunctionFailure, JsonElement>
                {
                    public override async Task<JsonElement> ExecuteAsync(FunctionContext<FunctionFailure> context, CancellationToken cancellationToken)
                    {
                        await context.Step.RunAsync("sent", () => (SendEventsResult)null!);
                        await context.Step.RunAsync("element", () => context.RawEvent);
                        await context.Step.RunAsync<object?>("object", () => Task.FromResult<object?>(null));
                        return context.RawEvent;
                    }
                }

                [InngestFunction("raw")]
                public sealed class OnRaw : InngestFunction<JsonElement, object>
                {
                    public override Task<object> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken) => Task.FromResult(new object());
                }
            }
            """;

        await Verify(source, Sources.PublishAot);
    }

    [Fact]
    public async Task An_anonymous_type_cannot_be_listed_so_it_gets_a_message_of_its_own()
    {
        const string source = Types + """
                [InngestFunction("order")]
                public sealed class OnOrder : InngestFunction<Order>
                {
                    public override async Task<object?> ExecuteAsync(FunctionContext<Order> context, CancellationToken cancellationToken)
                    {
                        await context.Step.{|#0:RunAsync|}("anonymous", () => new { context.Event.Data.Id });
                        await context.Step.{|#1:RunAsync|}("list", () => new[] { new { Count = 1 } }.ToList());
                        return null;
                    }
                }
            }
            """;

        await Verify(
            source,
            Sources.PublishAot,
            new DiagnosticResult(DiagnosticDescriptors.AnonymousType).WithLocation(0).WithArguments("The result of a step"),
            new DiagnosticResult(DiagnosticDescriptors.AnonymousType).WithLocation(1).WithArguments("The result of a step"));
    }

    [Fact]
    public async Task A_function_that_derives_through_a_base_class_is_reported_on_its_name()
    {
        const string source = Types + """
                public abstract class AuditedFunction<T> : InngestFunction<T, Receipt>
                {
                }

                [InngestFunction("audited")]
                public sealed class {|#0:Audited|} : AuditedFunction<Order>
                {
                    public override Task<Receipt> ExecuteAsync(FunctionContext<Order> context, CancellationToken cancellationToken) =>
                        Task.FromResult(new Receipt(Guid.NewGuid()));
                }
            }
            """;

        // The base class is generic and abstract: it is not a function, and its type parameter is not a type to list. The
        // derived class is one, and names neither type in its own base list.
        await Verify(source, Sources.PublishAot, Missing(0, "Shop.Receipt", "the result of function 'Shop.Audited'"));
    }

    [Fact]
    public async Task A_derived_type_listed_on_a_base_type_is_covered()
    {
        const string source = """
            namespace Shop
            {
                [JsonDerivedType(typeof(Circle))]
                public abstract record Shape;

                public sealed record Circle(double Radius) : Shape;

                public sealed record Drawing(Shape Shape);

                [JsonSerializable(typeof(Drawing))]
                internal sealed partial class AppJson : JsonSerializerContext;

                [InngestFunction("draw")]
                public sealed class OnDrawing : InngestFunction<Drawing, Circle>
                {
                    public override Task<Circle> ExecuteAsync(FunctionContext<Drawing> context, CancellationToken cancellationToken) =>
                        Task.FromResult(new Circle(1));
                }
            }
            """;

        await Verify(source, Sources.PublishAot);
    }

    [Fact]
    public async Task A_context_in_several_declarations_covers_what_each_one_lists()
    {
        const string source = """
            namespace Shop
            {
                public sealed record Order(string Id);

                public sealed record Receipt(Guid Id);

                [JsonSerializable(typeof(Order))]
                internal sealed partial class AppJson : JsonSerializerContext;

                [InngestFunction("order")]
                public sealed class OnOrder : InngestFunction<Order, Receipt>
                {
                    public override Task<Receipt> ExecuteAsync(FunctionContext<Order> context, CancellationToken cancellationToken) =>
                        Task.FromResult(new Receipt(Guid.NewGuid()));
                }
            }
            """;
        const string otherDeclaration = """
            using System.Text.Json.Serialization;

            namespace Shop
            {
                [JsonSerializable(typeof(Receipt))]
                internal sealed partial class AppJson;
            }
            """;

        var test = new InngestAnalyzerTest<JsonSerializableAnalyzer>(Sources.Usings + source, Sources.PublishAot, Sources.ContextImplementation("AppJson"), otherDeclaration);
        await test.RunAsync();
    }

    [Fact]
    public async Task A_context_in_another_project_of_the_app_covers_its_types()
    {
        const string source = """
            namespace Shop
            {
                [InngestFunction("order")]
                public sealed class OnOrder : InngestFunction<Order, Receipt>
                {
                    public override Task<Receipt> ExecuteAsync(FunctionContext<Order> context, CancellationToken cancellationToken) =>
                        Task.FromResult(new Receipt(Guid.NewGuid()));
                }
            }
            """;
        const string contracts = """
            using System;
            using System.Text.Json.Serialization;

            namespace Shop
            {
                public sealed record Order(string Id);

                public sealed record Receipt(Guid Id);

                [JsonSerializable(typeof(Order))]
                [JsonSerializable(typeof(Receipt))]
                public sealed partial class ContractsJson : JsonSerializerContext;
            }
            """;

        var test = new InngestAnalyzerTest<JsonSerializableAnalyzer>(Sources.Usings + source, Sources.PublishAot);
        var project = test.TestState.AdditionalProjects["Contracts"];
        project.Sources.Add(contracts);
        project.Sources.Add(Sources.ContextImplementation("ContractsJson").Replace("internal sealed partial", "public sealed partial", StringComparison.Ordinal));
        project.AdditionalReferences.AddRange(TestReferences.Libraries);
        test.TestState.AdditionalProjectReferences.Add("Contracts");
        await test.RunAsync();
    }

    [Fact]
    public async Task Reports_the_types_when_the_project_has_no_context_at_all()
    {
        const string source = """
            namespace Shop
            {
                public sealed record Order(string Id);

                [InngestFunction("order")]
                public sealed class OnOrder : InngestFunction<{|#0:Order|}>
                {
                    public override Task<object?> ExecuteAsync(FunctionContext<Order> context, CancellationToken cancellationToken) =>
                        Task.FromResult<object?>(null);
                }
            }
            """;

        var test = new InngestAnalyzerTest<JsonSerializableAnalyzer>(Sources.Usings + source, Sources.PublishAot);
        test.ExpectedDiagnostics.Add(Missing(0, "Shop.Order", "the event data of function 'Shop.OnOrder'"));
        await test.RunAsync();
    }

    private static DiagnosticResult Missing(int markup, string type, string usage) =>
        new DiagnosticResult(DiagnosticDescriptors.TypeNotInJsonContext).WithLocation(markup).WithArguments(type, usage);

    private static async Task Verify(string source, string? properties, params DiagnosticResult[] expected)
    {
        // The context the source declares needs the members that the generator of System.Text.Json writes.
        var test = new InngestAnalyzerTest<JsonSerializableAnalyzer>(Sources.Usings + source, properties, Sources.ContextImplementation("AppJson"));
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
