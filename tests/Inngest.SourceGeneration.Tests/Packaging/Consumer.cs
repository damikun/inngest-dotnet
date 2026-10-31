namespace Inngest.SourceGeneration.Tests.Packaging;

// A small app that references Inngest.AspNetCore from the packed feed, as a user's project does, with a source file for each
// thing the tests build it as.
internal sealed class Consumer
{
    private readonly string _folder;

    private Consumer(string folder)
    {
        _folder = folder;
    }

    // The project is restored once; each build is then one of the scenarios, switched by properties on the command line.
    public static async Task<Consumer> CreateAsync(PackageFeed feed, string name, string targetFramework, string? sdkVersion)
    {
        var folder = Path.Combine(feed.ConsumersFolder, name);
        Directory.CreateDirectory(Path.Combine(folder, "Scenarios"));

        await File.WriteAllTextAsync(Path.Combine(folder, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="feed" value="{feed.Folder}" />
                <add key="nuget" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """);

        if (sdkVersion is not null)
        {
            // Pins the SDK the project is built with, so that it is not the one the tests run under.
            await File.WriteAllTextAsync(Path.Combine(folder, "global.json"), $$"""{ "sdk": { "version": "{{sdkVersion}}", "rollForward": "latestPatch" } }""");
        }

        await File.WriteAllTextAsync(Path.Combine(folder, "Consumer.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>{targetFramework}</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <Scenario Condition="'$(Scenario)' == ''">Off</Scenario>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Scenarios/$(Scenario).cs" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Include="Inngest.AspNetCore" Version="{feed.Version}" />
              </ItemGroup>
            </Project>
            """);

        foreach (var (scenario, source) in Scenarios)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "Scenarios", scenario + ".cs"), source);
        }

        var consumer = new Consumer(folder);
        var (exitCode, output) = await DotnetCli.RunAsync(folder, "restore", "Consumer.csproj", "-p:NuGetAudit=false", "--nologo");
        exitCode.ShouldBe(0, "Restoring the consumer failed:\n" + output);
        return consumer;
    }

    public Task<(int ExitCode, string Output)> BuildAsync(string scenario, params string[] arguments) =>
        DotnetCli.RunAsync(
            _folder,
            [
                "build", "Consumer.csproj", "-c", "Release", "--no-restore", "--no-incremental", "--nologo",
                "-p:NuGetAudit=false", "-p:Scenario=" + scenario,
                .. arguments,
            ]);

    // A project that registers its function classes by hand, the way every project does today: a generic class closed by hand,
    // a private nested class registered from the class that holds it, an abstract class and a class that is not a function,
    // all with the attribute. Nothing here is a mistake, and nothing in the packages may say it is.
    private const string Off = """
        using Demo;
        using Inngest;

        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Services
            .AddInngest(options => options.AppId = "demo")
            .AddFunction<Generic<string>>()
            .AddHidden();

        namespace Demo
        {
            [InngestFunction("generic")]
            public sealed class Generic<T> : InngestFunction<T>
            {
                public override Task<object?> ExecuteAsync(FunctionContext<T> context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);
            }

            public static class Registrations
            {
                [InngestFunction("hidden")]
                private sealed class Hidden : InngestFunction<string>
                {
                    public override Task<object?> ExecuteAsync(FunctionContext<string> context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);
                }

                public static IInngestBuilder AddHidden(this IInngestBuilder builder) => builder.AddFunction<Hidden>();
            }

            [InngestFunction("abstract")]
            public abstract class AbstractFunction : InngestFunction<string>
            {
            }

            [InngestFunction("not-a-function")]
            public sealed class NotAFunction
            {
            }
        }
        """;

    // A project that registers its classes with the generated method, with an abstract class that the method has to leave out.
    private const string On = """
        using Inngest;

        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Services
            .AddInngest(options => options.AppId = "demo")
            .AddInngestFunctions();

        namespace Demo
        {
            [InngestFunction("welcome")]
            [EventTrigger("demo/welcome")]
            public sealed class Welcome : InngestFunction<string>
            {
                public override Task<object?> ExecuteAsync(FunctionContext<string> context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);
            }

            [InngestFunction("abstract")]
            public abstract class AbstractFunction : InngestFunction<string>
            {
            }
        }
        """;

    // A function whose event data no JsonSerializerContext lists.
    private const string Json = """
        using Inngest;

        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Services
            .AddInngest(options => options.AppId = "demo")
            .AddFunction<Demo.OnOrder>();

        namespace Demo
        {
            public sealed record Order(string Id);

            [InngestFunction("order")]
            [EventTrigger("demo/order")]
            public sealed class OnOrder : InngestFunction<Order>
            {
                public override Task<object?> ExecuteAsync(FunctionContext<Order> context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);
            }
        }
        """;

    private static readonly (string Name, string Source)[] Scenarios = [(nameof(Off), Off), (nameof(On), On), (nameof(Json), Json)];
}
