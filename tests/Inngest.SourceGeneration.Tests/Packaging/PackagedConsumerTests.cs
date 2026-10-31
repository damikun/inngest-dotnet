namespace Inngest.SourceGeneration.Tests.Packaging;

// Everything else in this project runs the generator and the analyzers from the build output. A project gets them from the
// package, so these pack Inngest and Inngest.AspNetCore, restore them into a small app the way a user does, and build it. They
// take a minute or two, and need the NuGet feed for the packages' own dependencies.
public sealed class PackagedConsumerTests(PackageFeed feed) : IClassFixture<PackageFeed>
{
    [Theory]
    [InlineData("default")]
    [InlineData("8.0")]
    public async Task The_packages_do_nothing_to_a_project_until_it_asks_and_then_do_what_it_asked(string sdk)
    {
        var sdkVersion = sdk == "8.0" ? await DotnetCli.FindSdk8Async() : null;
        if (sdk == "8.0" && sdkVersion is null)
        {
            Assert.Skip("No .NET 8 SDK is installed, so the packages are only checked with the SDK the tests run under.");
        }

        // The app only references Inngest.AspNetCore: the generator and the analyzers have to come through it.
        var consumer = await Consumer.CreateAsync(feed, sdk == "8.0" ? "sdk8" : "default", sdk == "8.0" ? "net8.0" : "net10.0", sdkVersion);

        // Today's projects: classes registered by hand that the generated method would have to leave out, and a build that
        // treats every warning as an error.
        var untouched = await consumer.BuildAsync("Off", "-warnaserror");
        untouched.ExitCode.ShouldBe(0, untouched.Output);
        untouched.Output.ShouldNotContain("INNGEST", Case.Sensitive, untouched.Output);

        // Generation on: the method is written and called, and the classes it cannot register are warnings.
        var generated = await consumer.BuildAsync("On", "-p:InngestGenerateFunctionRegistration=true");
        generated.ExitCode.ShouldBe(0, generated.Output);
        generated.Output.ShouldContain("warning INNGEST002", Case.Sensitive, generated.Output);

        var asErrors = await consumer.BuildAsync("On", "-p:InngestGenerateFunctionRegistration=true", "-warnaserror");
        asErrors.ExitCode.ShouldNotBe(0);
        asErrors.Output.ShouldContain("error INNGEST002", Case.Sensitive, asErrors.Output);

        // Without the property, the method does not exist.
        var notGenerated = await consumer.BuildAsync("On");
        notGenerated.ExitCode.ShouldNotBe(0);
        notGenerated.Output.ShouldContain("CS1061", Case.Sensitive, notGenerated.Output);

        // Reflection-based JSON off: the analyzer finds the type no context lists. On, it has nothing to say.
        var withoutReflection = await consumer.BuildAsync("Json", "-p:JsonSerializerIsReflectionEnabledByDefault=false");
        withoutReflection.ExitCode.ShouldBe(0, withoutReflection.Output);
        withoutReflection.Output.ShouldContain("warning INNGEST010", Case.Sensitive, withoutReflection.Output);

        var withReflection = await consumer.BuildAsync("Json");
        withReflection.ExitCode.ShouldBe(0, withReflection.Output);
        withReflection.Output.ShouldNotContain("INNGEST", Case.Sensitive, withReflection.Output);

        // The switch takes both out of the build.
        var generatorOut = await consumer.BuildAsync("On", "-p:InngestGenerateFunctionRegistration=true", "-p:DisableInngestAnalyzers=true");
        generatorOut.ExitCode.ShouldNotBe(0);
        generatorOut.Output.ShouldContain("CS1061", Case.Sensitive, generatorOut.Output);
        generatorOut.Output.ShouldNotContain("INNGEST", Case.Sensitive, generatorOut.Output);

        var analyzerOut = await consumer.BuildAsync("Json", "-p:JsonSerializerIsReflectionEnabledByDefault=false", "-p:DisableInngestAnalyzers=true");
        analyzerOut.ExitCode.ShouldBe(0, analyzerOut.Output);
        analyzerOut.Output.ShouldNotContain("INNGEST", Case.Sensitive, analyzerOut.Output);
    }
}
