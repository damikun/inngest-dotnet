using System.Xml.Linq;
using Inngest.SourceGeneration.Functions;
using Inngest.SourceGeneration.Json;

namespace Inngest.SourceGeneration.Tests;

public sealed class PackageContentTests
{
    [Fact]
    public void The_package_makes_every_MSBuild_property_the_compiler_extensions_read_visible_to_the_compiler()
    {
        using var stream = typeof(PackageContentTests).Assembly.GetManifestResourceStream("Inngest.props")!;
        var props = XDocument.Load(stream);
        var visible = props.Descendants("CompilerVisibleProperty").Select(static element => (string?)element.Attribute("Include")).ToList();

        // A property the compiler is not told about reads as nothing, and the extension quietly does nothing.
        string[] read =
        [
            RegistrationOptions.MethodNameProperty,
            RegistrationOptions.AccessibilityProperty,
            ReflectionJson.PublishAotProperty,
            ReflectionJson.PublishTrimmedProperty,
            ReflectionJson.ReflectionEnabledProperty,
        ];
        foreach (var property in read)
        {
            visible.ShouldContain(property);
        }
    }
}
