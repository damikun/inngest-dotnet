using Inngest.SourceGeneration.Json;
using Inngest.SourceGeneration.Tests.Support;

namespace Inngest.SourceGeneration.Tests.Json;

public sealed class AddJsonSerializableCodeFixTests
{
    [Fact]
    public async Task Lists_the_type_after_the_types_the_context_lists_already()
    {
        const string source = """
            namespace Shop
            {
                public sealed record Order(string Id);

                public sealed record Refund(string OrderId);

                [JsonSerializable(typeof(Order))]
                internal sealed partial class AppJson : JsonSerializerContext;

                [InngestFunction("refund")]
                public sealed class OnRefund : InngestFunction<{|INNGEST010:Refund|}>
                {
                    public override Task<object?> ExecuteAsync(FunctionContext<Refund> context, CancellationToken cancellationToken) =>
                        Task.FromResult<object?>(null);
                }
            }
            """;
        const string fixedSource = """
            namespace Shop
            {
                public sealed record Order(string Id);

                public sealed record Refund(string OrderId);

                [JsonSerializable(typeof(Order))]
                [JsonSerializable(typeof(Refund))]
                internal sealed partial class AppJson : JsonSerializerContext;

                [InngestFunction("refund")]
                public sealed class OnRefund : InngestFunction<Refund>
                {
                    public override Task<object?> ExecuteAsync(FunctionContext<Refund> context, CancellationToken cancellationToken) =>
                        Task.FromResult<object?>(null);
                }
            }
            """;

        await Run(source, fixedSource);
    }

    [Fact]
    public async Task Keeps_the_comment_above_a_context_that_has_no_attribute_yet()
    {
        const string source = """
            namespace Shop
            {
                public sealed record Refund(string OrderId);

                /// <summary>The JSON of the app.</summary>
                internal sealed partial class AppJson : JsonSerializerContext;

                [InngestFunction("refund")]
                public sealed class OnRefund : InngestFunction<{|INNGEST010:Refund|}>
                {
                    public override Task<object?> ExecuteAsync(FunctionContext<Refund> context, CancellationToken cancellationToken) =>
                        Task.FromResult<object?>(null);
                }
            }
            """;
        const string fixedSource = """
            namespace Shop
            {
                public sealed record Refund(string OrderId);

                /// <summary>The JSON of the app.</summary>
                [JsonSerializable(typeof(Refund))]
                internal sealed partial class AppJson : JsonSerializerContext;

                [InngestFunction("refund")]
                public sealed class OnRefund : InngestFunction<Refund>
                {
                    public override Task<object?> ExecuteAsync(FunctionContext<Refund> context, CancellationToken cancellationToken) =>
                        Task.FromResult<object?>(null);
                }
            }
            """;

        await Run(source, fixedSource);
    }

    [Fact]
    public async Task Adds_the_attribute_after_other_attributes_of_a_context_that_lists_nothing()
    {
        const string source = """
            namespace Shop
            {
                public sealed record Refund(string OrderId);

                [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
                internal sealed partial class AppJson : JsonSerializerContext;

                [InngestFunction("refund")]
                public sealed class OnRefund : InngestFunction<{|INNGEST010:Refund|}>
                {
                    public override Task<object?> ExecuteAsync(FunctionContext<Refund> context, CancellationToken cancellationToken) =>
                        Task.FromResult<object?>(null);
                }
            }
            """;
        const string fixedSource = """
            namespace Shop
            {
                public sealed record Refund(string OrderId);

                [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
                [JsonSerializable(typeof(Refund))]
                internal sealed partial class AppJson : JsonSerializerContext;

                [InngestFunction("refund")]
                public sealed class OnRefund : InngestFunction<Refund>
                {
                    public override Task<object?> ExecuteAsync(FunctionContext<Refund> context, CancellationToken cancellationToken) =>
                        Task.FromResult<object?>(null);
                }
            }
            """;

        await Run(source, fixedSource);
    }

    [Fact]
    public async Task Edits_the_file_that_holds_the_context_and_adds_the_using_directive_it_needs()
    {
        const string source = """
            namespace Shop.Functions
            {
                [InngestFunction("refund")]
                public sealed class OnRefund : InngestFunction<{|INNGEST010:Shop.Contracts.Refund|}>
                {
                    public override Task<object?> ExecuteAsync(FunctionContext<Shop.Contracts.Refund> context, CancellationToken cancellationToken) =>
                        Task.FromResult<object?>(null);
                }
            }
            """;
        const string contracts = """
            namespace Shop.Contracts
            {
                public sealed record Refund(string OrderId);
            }
            """;
        const string context = """
            using System.Text.Json.Serialization;

            namespace Billing
            {
                internal sealed partial class AppJson : JsonSerializerContext;
            }
            """;
        const string fixedContext = """
            using System.Text.Json.Serialization;
            using Shop.Contracts;

            namespace Billing
            {
                [JsonSerializable(typeof(Refund))]
                internal sealed partial class AppJson : JsonSerializerContext;
            }
            """;

        var test = NewTest(Sources.Usings + source, Sources.Usings + source.Replace("{|INNGEST010:Shop.Contracts.Refund|}", "Shop.Contracts.Refund", StringComparison.Ordinal));
        test.AddUnchanged("Contracts.cs", contracts);
        test.TestState.Sources.Add(("AppJson.cs", context));
        test.FixedState.Sources.Add(("AppJson.cs", fixedContext));
        test.AddUnchanged("AppJson.Generated.cs", Sources.ContextImplementation("AppJson", "Billing"));
        await test.RunAsync();
    }

    [Fact]
    public async Task Writes_a_generic_type_with_the_using_directive_of_the_types_it_names()
    {
        const string source = """
            namespace Shop
            {
                public sealed record Line(int Quantity);

                [InngestFunction("lines")]
                public sealed class OnLines : InngestFunction<Line>
                {
                    public override async Task<object?> ExecuteAsync(FunctionContext<Line> context, CancellationToken cancellationToken)
                    {
                        await context.Step.{|INNGEST010:RunAsync|}("lines", () => new List<Line>());
                        return null;
                    }
                }
            }
            """;
        const string context = """
            using System.Text.Json.Serialization;

            namespace Shop
            {
                [JsonSerializable(typeof(Line))]
                internal sealed partial class AppJson : JsonSerializerContext;
            }
            """;
        const string fixedContext = """
            using System.Collections.Generic;
            using System.Text.Json.Serialization;

            namespace Shop
            {
                [JsonSerializable(typeof(Line))]
                [JsonSerializable(typeof(List<Line>))]
                internal sealed partial class AppJson : JsonSerializerContext;
            }
            """;

        var test = NewTest(Sources.Usings + source, Sources.Usings + source.Replace("{|INNGEST010:RunAsync|}", "RunAsync", StringComparison.Ordinal));
        test.TestState.Sources.Add(("AppJson.cs", context));
        test.FixedState.Sources.Add(("AppJson.cs", fixedContext));
        test.AddUnchanged("AppJson.Generated.cs", Sources.ContextImplementation("AppJson"));
        await test.RunAsync();
    }

    [Fact]
    public async Task Offers_a_fix_for_each_context_of_the_project()
    {
        const string source = """
            namespace Shop
            {
                public sealed record Refund(string OrderId);

                internal sealed partial class FirstJson : JsonSerializerContext;

                internal sealed partial class SecondJson : JsonSerializerContext;

                [InngestFunction("refund")]
                public sealed class OnRefund : InngestFunction<{|INNGEST010:Refund|}>
                {
                    public override Task<object?> ExecuteAsync(FunctionContext<Refund> context, CancellationToken cancellationToken) =>
                        Task.FromResult<object?>(null);
                }
            }
            """;
        const string fixedSource = """
            namespace Shop
            {
                public sealed record Refund(string OrderId);

                internal sealed partial class FirstJson : JsonSerializerContext;

                [JsonSerializable(typeof(Refund))]
                internal sealed partial class SecondJson : JsonSerializerContext;

                [InngestFunction("refund")]
                public sealed class OnRefund : InngestFunction<Refund>
                {
                    public override Task<object?> ExecuteAsync(FunctionContext<Refund> context, CancellationToken cancellationToken) =>
                        Task.FromResult<object?>(null);
                }
            }
            """;

        var test = NewTest(Sources.Usings + source, Sources.Usings + fixedSource);
        test.AddUnchanged("FirstJson.Generated.cs", Sources.ContextImplementation("FirstJson"));
        test.AddUnchanged("SecondJson.Generated.cs", Sources.ContextImplementation("SecondJson"));

        // One fix is offered for each context, so the second one is chosen by the context it says.
        test.CodeActionEquivalenceKey = "AddJsonSerializableCodeFixProvider:Shop.SecondJson";
        await test.RunAsync();
    }

    [Fact]
    public async Task Fixing_all_adds_each_type_once_in_a_single_edit()
    {
        // Refund is the event data of two functions, and Receipt is the result of one.
        const string source = """
            namespace Shop
            {
                public sealed record Order(string Id);

                public sealed record Refund(string OrderId);

                public sealed record Receipt(Guid Id);

                [JsonSerializable(typeof(Order))]
                internal sealed partial class AppJson : JsonSerializerContext;

                [InngestFunction("refund")]
                public sealed class OnRefund : InngestFunction<{|INNGEST010:Refund|}, {|INNGEST010:Receipt|}>
                {
                    public override Task<Receipt> ExecuteAsync(FunctionContext<Refund> context, CancellationToken cancellationToken) =>
                        Task.FromResult(new Receipt(Guid.NewGuid()));
                }

                [InngestFunction("audit")]
                public sealed class OnAudit : InngestFunction<{|INNGEST010:Refund|}>
                {
                    public override Task<object?> ExecuteAsync(FunctionContext<Refund> context, CancellationToken cancellationToken) =>
                        Task.FromResult<object?>(null);
                }
            }
            """;
        var fixedSource = source
            .Replace("{|INNGEST010:Refund|}", "Refund", StringComparison.Ordinal)
            .Replace("{|INNGEST010:Receipt|}", "Receipt", StringComparison.Ordinal)
            .Replace("    [JsonSerializable(typeof(Order))]\n", "    [JsonSerializable(typeof(Order))]\n    [JsonSerializable(typeof(Refund))]\n    [JsonSerializable(typeof(Receipt))]\n", StringComparison.Ordinal);
        fixedSource.ShouldContain("typeof(Receipt)");

        await Run(source, fixedSource);
    }

    [Fact]
    public async Task Fixing_all_in_a_project_collects_the_types_of_every_file()
    {
        const string first = """
            namespace Shop
            {
                public sealed record Refund(string OrderId);

                [InngestFunction("refund")]
                public sealed class OnRefund : InngestFunction<{|INNGEST010:Refund|}>
                {
                    public override Task<object?> ExecuteAsync(FunctionContext<Refund> context, CancellationToken cancellationToken) =>
                        Task.FromResult<object?>(null);
                }
            }
            """;
        const string second = """
            namespace Shop
            {
                public sealed record Invoice(string Id);

                [InngestFunction("invoice")]
                public sealed class OnInvoice : InngestFunction<{|INNGEST010:Invoice|}>
                {
                    public override Task<object?> ExecuteAsync(FunctionContext<Invoice> context, CancellationToken cancellationToken) =>
                        Task.FromResult<object?>(null);
                }
            }
            """;
        const string context = """
            using System.Text.Json.Serialization;

            namespace Shop
            {
                internal sealed partial class AppJson : JsonSerializerContext;
            }
            """;
        const string fixedContext = """
            using System.Text.Json.Serialization;

            namespace Shop
            {
                [JsonSerializable(typeof(Refund))]
                [JsonSerializable(typeof(Invoice))]
                internal sealed partial class AppJson : JsonSerializerContext;
            }
            """;

        var test = NewTest(Sources.Usings + first, Sources.Usings + first.Replace("{|INNGEST010:Refund|}", "Refund", StringComparison.Ordinal));
        test.TestState.Sources.Add(("Invoices.cs", Sources.Usings + second));
        test.FixedState.Sources.Add(("Invoices.cs", Sources.Usings + second.Replace("{|INNGEST010:Invoice|}", "Invoice", StringComparison.Ordinal)));
        test.TestState.Sources.Add(("AppJson.cs", context));
        test.FixedState.Sources.Add(("AppJson.cs", fixedContext));
        test.AddUnchanged("AppJson.Generated.cs", Sources.ContextImplementation("AppJson"));
        await test.RunAsync();
    }

    [Fact]
    public async Task Offers_nothing_when_the_project_has_no_context_to_add_the_type_to()
    {
        const string source = """
            namespace Shop
            {
                public sealed record Refund(string OrderId);

                [InngestFunction("refund")]
                public sealed class OnRefund : InngestFunction<{|INNGEST010:Refund|}>
                {
                    public override Task<object?> ExecuteAsync(FunctionContext<Refund> context, CancellationToken cancellationToken) =>
                        Task.FromResult<object?>(null);
                }
            }
            """;

        // The code after the fix is the code before it, with the diagnostic still in it.
        await Run(source, source, withContext: false);
    }

    private static async Task Run(string source, string fixedSource, bool withContext = true)
    {
        var test = NewTest(Sources.Usings + source, Sources.Usings + fixedSource);
        if (withContext)
        {
            test.AddUnchanged("AppJson.Generated.cs", Sources.ContextImplementation("AppJson"));
        }

        await test.RunAsync();
    }

    private static InngestCodeFixTest<JsonSerializableAnalyzer, AddJsonSerializableCodeFixProvider> NewTest(string source, string fixedSource)
    {
        var test = new InngestCodeFixTest<JsonSerializableAnalyzer, AddJsonSerializableCodeFixProvider>();
        test.TestState.Sources.Add(source);
        test.FixedState.Sources.Add(fixedSource);
        return test;
    }
}
