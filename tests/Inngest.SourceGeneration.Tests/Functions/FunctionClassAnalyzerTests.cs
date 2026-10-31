using Inngest.SourceGeneration.Functions;
using Inngest.SourceGeneration.Tests.Support;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;

namespace Inngest.SourceGeneration.Tests.Functions;

public sealed class FunctionClassAnalyzerTests
{
    private const string Body = "public override Task<object?> ExecuteAsync(FunctionContext<string> context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);";

    private const string GenericBody = "public override Task<object?> ExecuteAsync(FunctionContext<T> context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);";

    [Fact]
    public async Task A_class_that_AddInngestFunctions_registers_has_no_diagnostic()
    {
        var source = $$"""
            namespace Shop
            {
                [InngestFunction("public")]
                public sealed class PublicFunction : InngestFunction<string>
                {
                    {{Body}}
                }

                [InngestFunction("internal")]
                internal sealed class InternalFunction : InngestFunction<string>
                {
                    {{Body}}
                }

                public class Container
                {
                    [InngestFunction("nested")]
                    protected internal sealed class NestedFunction : InngestFunction<string>
                    {
                        {{Body}}
                    }
                }

                // The attribute is what opts a class in.
                public sealed class WithoutAttribute : InngestFunction<string>
                {
                    {{Body}}
                }
            }
            """;

        await Verify(source);
    }

    [Fact]
    public async Task A_class_with_the_attribute_that_is_not_a_function_is_reported()
    {
        const string source = """
            namespace Shop
            {
                [InngestFunction("not-a-function")]
                public sealed class {|#0:NotAFunction|}
                {
                }

                [InngestFunction("record")]
                public sealed record {|#1:ARecord|}(string Id);

                [InngestFunction("static")]
                public static class {|#2:AStaticClass|}
                {
                }
            }
            """;

        await Verify(
            source,
            Expect(DiagnosticDescriptors.NotAFunction, 0, "Shop.NotAFunction"),
            Expect(DiagnosticDescriptors.NotAFunction, 1, "Shop.ARecord"),
            Expect(DiagnosticDescriptors.NotAFunction, 2, "Shop.AStaticClass"));
    }

    [Fact]
    public async Task An_abstract_class_is_not_registered_and_its_derived_classes_do_not_inherit_the_attribute()
    {
        const string source = """
            namespace Shop
            {
                [InngestFunction("abstract")]
                public abstract class {|#0:AbstractFunction|} : InngestFunction<string>
                {
                }
            }
            """;

        await Verify(source, Expect(DiagnosticDescriptors.AbstractFunction, 0, "Shop.AbstractFunction"));
    }

    [Fact]
    public async Task A_generic_class_cannot_be_registered_because_its_type_arguments_are_not_known()
    {
        var source = $$"""
            namespace Shop
            {
                [InngestFunction("generic")]
                public sealed class {|#0:GenericFunction|}<T> : InngestFunction<T>
                {
                    public override Task<object?> ExecuteAsync(FunctionContext<T> context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);
                }

                public sealed class Container<T>
                {
                    [InngestFunction("nested")]
                    public sealed class {|#1:InGeneric|} : InngestFunction<string>
                    {
                        {{Body}}
                    }
                }
            }
            """;

        await Verify(
            source,
            Expect(DiagnosticDescriptors.GenericFunction, 0, "Shop.GenericFunction<T>"),
            Expect(DiagnosticDescriptors.GenericFunction, 1, "Shop.Container<T>.InGeneric"));
    }

    [Fact]
    public async Task A_class_that_generated_code_cannot_reach_is_not_registered()
    {
        var source = $$"""
            namespace Shop
            {
                public sealed class Container
                {
                    [InngestFunction("private")]
                    private sealed class {|#0:PrivateFunction|} : InngestFunction<string>
                    {
                        {{Body}}
                    }

                    [InngestFunction("protected")]
                    protected sealed class {|#1:ProtectedFunction|} : InngestFunction<string>
                    {
                        {{Body}}
                    }

                    [InngestFunction("private-protected")]
                    private protected sealed class {|#2:PrivateProtectedFunction|} : InngestFunction<string>
                    {
                        {{Body}}
                    }

                    // Public, but inside a private class.
                    private sealed class Hidden
                    {
                        [InngestFunction("hidden")]
                        public sealed class {|#3:InHidden|} : InngestFunction<string>
                        {
                            {{Body}}
                        }
                    }
                }

                [InngestFunction("file")]
                file sealed class {|#4:FileLocal|} : InngestFunction<string>
                {
                    {{Body}}
                }
            }
            """;

        await Verify(
            source,
            Expect(DiagnosticDescriptors.InaccessibleFunction, 0, "Shop.Container.PrivateFunction"),
            Expect(DiagnosticDescriptors.InaccessibleFunction, 1, "Shop.Container.ProtectedFunction"),
            Expect(DiagnosticDescriptors.InaccessibleFunction, 2, "Shop.Container.PrivateProtectedFunction"),
            Expect(DiagnosticDescriptors.InaccessibleFunction, 3, "Shop.Container.Hidden.InHidden"),
            Expect(DiagnosticDescriptors.InaccessibleFunction, 4, "Shop.FileLocal"));
    }

    [Fact]
    public async Task A_class_without_a_public_constructor_cannot_be_created_by_the_service_provider()
    {
        var source = $$"""
            namespace Shop
            {
                [InngestFunction("no-constructor")]
                public sealed class {|#0:NoPublicConstructor|} : InngestFunction<string>
                {
                    internal NoPublicConstructor()
                    {
                    }

                    {{Body}}
                }
            }
            """;

        await Verify(source, Expect(DiagnosticDescriptors.NoPublicConstructor, 0, "Shop.NoPublicConstructor"));
    }

    [Fact]
    public async Task A_base_type_that_is_missing_is_left_to_the_compiler()
    {
        const string source = """
            namespace Shop
            {
                [InngestFunction("unresolved")]
                public sealed class Unresolved : {|CS0246:MissingBase|}
                {
                }
            }
            """;

        await Verify(source);
    }

    [Fact]
    public async Task A_project_that_does_not_use_the_attribute_has_nothing_to_report()
    {
        const string source = """
            namespace Shop
            {
                public sealed class Plain
                {
                }
            }
            """;

        await Verify(source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("is_global = true\nbuild_property.InngestGenerateFunctionRegistration = false\n")]
    [InlineData("is_global = true\nbuild_property.InngestGenerateFunctionRegistration = \n")]
    public async Task A_project_that_has_not_turned_generation_on_is_told_nothing(string? properties)
    {
        // Every kind of class the diagnostics are about, as a project that registers its classes by hand has them: a generic
        // class closed by hand, a private nested class registered from its container, an abstract base, and an attribute on a
        // class that is not a function. None of it is wrong there, and none of it may fail a build that warns as an error.
        var source = $$"""
            namespace Shop
            {
                [InngestFunction("generic")]
                public sealed class GenericFunction<T> : InngestFunction<T>
                {
                    {{GenericBody}}
                }

                [InngestFunction("abstract")]
                public abstract class AbstractFunction : InngestFunction<string>
                {
                }

                [InngestFunction("not-a-function")]
                public sealed class NotAFunction
                {
                }

                [InngestFunction("no-constructor")]
                public sealed class NoPublicConstructor : InngestFunction<string>
                {
                    internal NoPublicConstructor()
                    {
                    }

                    {{Body}}
                }

                public static class Registrations
                {
                    [InngestFunction("hidden")]
                    private sealed class Hidden : InngestFunction<string>
                    {
                        {{Body}}
                    }

                    public static IInngestBuilder Add(IInngestBuilder builder) => builder
                        .AddFunction<GenericFunction<string>>()
                        .AddFunction<Hidden>();
                }
            }
            """;

        await new InngestAnalyzerTest<FunctionClassAnalyzer>(Sources.Usings + source, properties).RunAsync();
    }

    [Fact]
    public async Task The_switch_may_be_written_in_any_case()
    {
        const string source = """
            namespace Shop
            {
                [InngestFunction("abstract")]
                public abstract class {|#0:AbstractFunction|} : InngestFunction<string>
                {
                }
            }
            """;

        var test = new InngestAnalyzerTest<FunctionClassAnalyzer>(Sources.Usings + source, "is_global = true\nbuild_property.InngestGenerateFunctionRegistration = True\n");
        test.ExpectedDiagnostics.Add(Expect(DiagnosticDescriptors.AbstractFunction, 0, "Shop.AbstractFunction"));
        await test.RunAsync();
    }

    private static DiagnosticResult Expect(DiagnosticDescriptor descriptor, int markup, string name) =>
        new DiagnosticResult(descriptor).WithLocation(markup).WithArguments(name);

    private static async Task Verify(string source, params DiagnosticResult[] expected)
    {
        var test = new InngestAnalyzerTest<FunctionClassAnalyzer>(Sources.Usings + source, Sources.GenerationOn);
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
