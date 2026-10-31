using Inngest.Tool;

namespace Inngest.Tests.Tool;

public sealed class ArgumentsTests
{
    [Theory]
    [InlineData]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("help")]
    [InlineData("dev", "--help")]
    [InlineData("dev", "-h")]
    [InlineData("dev", "-u", "http://localhost:5000/api/inngest", "--help")]
    public void Help_is_what_the_tool_shows_when_it_is_asked_for_or_given_nothing(params string[] args)
    {
        Arguments.Parse(args).Kind.ToString().ShouldBe("Help");
    }

    [Fact]
    public void The_version_is_asked_for_with_an_option()
    {
        Arguments.Parse(["--version"]).Kind.ToString().ShouldBe("Version");
    }

    [Fact]
    public void A_command_the_tool_does_not_have_is_invalid()
    {
        var arguments = Arguments.Parse(["start"]);

        arguments.Kind.ToString().ShouldBe("Invalid");
        arguments.Error.ShouldBe("Unknown command 'start'.");
    }

    [Fact]
    public void Dev_alone_forwards_nothing_and_names_no_app()
    {
        var arguments = Arguments.Parse(["dev"]);

        arguments.Kind.ToString().ShouldBe("Dev");
        arguments.Forwarded.ShouldBeEmpty();
        arguments.NamesApp.ShouldBeFalse();
        arguments.CliVersion.ShouldBeNull();
        arguments.Project.ShouldBeNull();
    }

    [Theory]
    [InlineData("-u", "http://localhost:5000/api/inngest")]
    [InlineData("--sdk-url", "http://localhost:5000/api/inngest")]
    [InlineData("-u=http://localhost:5000/api/inngest")]
    [InlineData("--sdk-url=http://localhost:5000/api/inngest")]
    [InlineData("--no-discovery")]
    [InlineData("--no-discovery=true")]
    public void Options_that_name_an_app_are_forwarded_as_they_are_and_stop_the_tool_from_adding_one(params string[] options)
    {
        var arguments = Arguments.Parse(["dev", .. options]);

        arguments.NamesApp.ShouldBeTrue();
        arguments.Forwarded.ShouldBe(options);
    }

    [Fact]
    public void Several_urls_are_forwarded_in_order()
    {
        var arguments = Arguments.Parse(["dev", "-u", "http://localhost:5000/api/inngest", "-u", "http://localhost:6000/api/inngest"]);

        arguments.Forwarded.ShouldBe(["-u", "http://localhost:5000/api/inngest", "-u", "http://localhost:6000/api/inngest"]);
    }

    [Fact]
    public void Options_the_tool_does_not_know_are_forwarded_in_their_place()
    {
        var arguments = Arguments.Parse(["dev", "--port", "9000", "--persist", "-u", "http://localhost:5000/api/inngest", "--poll-interval=5"]);

        arguments.Forwarded.ShouldBe(["--port", "9000", "--persist", "-u", "http://localhost:5000/api/inngest", "--poll-interval=5"]);
        arguments.NamesApp.ShouldBeTrue();
    }

    [Theory]
    [InlineData("--cli-version", "1.50.0")]
    [InlineData("--cli-version=1.50.0")]
    [InlineData("--cli-version", "v1.50.0")]
    public void The_cli_version_is_the_tools_own_and_is_not_forwarded(params string[] options)
    {
        var arguments = Arguments.Parse(["dev", .. options, "--port", "9000"]);

        arguments.CliVersion.ShouldBe("1.50.0");
        arguments.Forwarded.ShouldBe(["--port", "9000"]);
    }

    [Theory]
    [InlineData("--project", "src/Shop")]
    [InlineData("--project=src/Shop")]
    public void The_project_is_the_tools_own_and_is_not_forwarded(params string[] options)
    {
        var arguments = Arguments.Parse(["dev", "--no-poll", .. options]);

        arguments.Project.ShouldBe("src/Shop");
        arguments.Forwarded.ShouldBe(["--no-poll"]);
    }

    [Theory]
    [InlineData("--cli-version", "--cli-version needs a version.")]
    [InlineData("--project", "--project needs a folder or a project file.")]
    public void An_option_of_the_tool_at_the_end_needs_its_value(string option, string error)
    {
        var arguments = Arguments.Parse(["dev", option]);

        arguments.Kind.ToString().ShouldBe("Invalid");
        arguments.Error.ShouldBe(error);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("1.49")]
    [InlineData("../1.49.0")]
    [InlineData("1.49.0\n")]
    [InlineData("\u0661.\u0662.\u0663")]
    public void A_cli_version_that_is_no_version_is_invalid(string version)
    {
        var arguments = Arguments.Parse(["dev", "--cli-version", version]);

        arguments.Kind.ToString().ShouldBe("Invalid");
        arguments.Error.ShouldNotBeNull().ShouldContain("is not a release version");
    }

    // The tool's own options end at --, so that the CLI can still be given an option that has the name of one of them.
    [Fact]
    public void Everything_after_a_double_dash_is_forwarded_even_when_it_looks_like_an_option_of_the_tool()
    {
        var arguments = Arguments.Parse(["dev", "--cli-version", "1.50.0", "--", "--project", "x", "--help", "--cli-version", "2.0.0"]);

        arguments.CliVersion.ShouldBe("1.50.0");
        arguments.Project.ShouldBeNull();
        arguments.Forwarded.ShouldBe(["--project", "x", "--help", "--cli-version", "2.0.0"]);
        arguments.Kind.ToString().ShouldBe("Dev");
    }

    [Fact]
    public void An_app_named_after_a_double_dash_still_counts()
    {
        Arguments.Parse(["dev", "--", "-u", "http://localhost:5000/api/inngest"]).NamesApp.ShouldBeTrue();
        Arguments.Parse(["dev", "--", "--no-discovery"]).NamesApp.ShouldBeTrue();
    }

    [Fact]
    public void A_value_with_spaces_stays_one_argument()
    {
        Arguments.Parse(["dev", "--config", "my config.json"]).Forwarded.ShouldBe(["--config", "my config.json"]);
    }
}
