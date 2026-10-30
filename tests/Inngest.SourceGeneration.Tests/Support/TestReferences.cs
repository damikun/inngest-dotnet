using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inngest.SourceGeneration.Tests.Support;

// What the compilations under test build against: the reference assemblies of net10.0, and Inngest with the libraries that
// its public API shows. Nothing is downloaded, so the tests run offline.
internal static class TestReferences
{
    // The framework comes with AdditionalReferences, so the testing framework has no package of its own to resolve.
    public static ReferenceAssemblies Framework { get; } = new("net10.0");

    public static ImmutableArray<MetadataReference> Libraries { get; } = Create();

    private static ImmutableArray<MetadataReference> Create()
    {
        var references = ImmutableArray.CreateBuilder<MetadataReference>();
        references.AddRange(Basic.Reference.Assemblies.Net100.References.All);
        foreach (var type in new[] { typeof(IInngestBuilder), typeof(IServiceCollection), typeof(ILogger), typeof(IOptions<>), typeof(IConfiguration) })
        {
            references.Add(MetadataReference.CreateFromFile(type.Assembly.Location));
        }

        return references.ToImmutable();
    }
}
