namespace Inngest.SourceGeneration.Tests.Support;

// Pieces of source that many tests share.
internal static class Sources
{
    public const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Text.Json;
        using System.Text.Json.Serialization;
        using System.Threading;
        using System.Threading.Tasks;
        using Inngest;

        """;

    // What the source generator of System.Text.Json writes for a context, which the analyzers do not run. The compiler needs
    // the abstract members, and nothing in the tests calls them.
    public static string ContextImplementation(string contextName, string @namespace = "Shop") => $$"""
        using System;
        using System.Text.Json;
        using System.Text.Json.Serialization;
        using System.Text.Json.Serialization.Metadata;

        namespace {{@namespace}}
        {
            internal sealed partial class {{contextName}}
            {
                public {{contextName}}(JsonSerializerOptions options) : base(options)
                {
                }

                protected override JsonSerializerOptions? GeneratedSerializerOptions => null;

                public override JsonTypeInfo? GetTypeInfo(Type type) => null;
            }
        }
        """;

    // The MSBuild properties of an app that publishes with Native AOT, as the compiler sees them.
    public const string PublishAot = "is_global = true\nbuild_property.PublishAot = true\n";
}
