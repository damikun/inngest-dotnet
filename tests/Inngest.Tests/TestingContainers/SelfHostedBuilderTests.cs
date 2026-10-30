using System.Text.RegularExpressions;
using static Inngest.Tests.TestingContainers.BuilderHarness;

namespace Inngest.Tests.TestingContainers;

public sealed class SelfHostedBuilderTests
{
    private const string SigningKey = "9f2c5a7e1b3d4f60a8c9e0d1b2a3f4e5d6c7b8a9f0e1d2c3b4a5968778695a4b";

    [Fact]
    public void The_self_hosted_server_runs_inngest_start_with_the_keys_it_was_given()
    {
        var container = Builder().WithSelfHosting().WithSigningKey(SigningKey).WithEventKey("events-1").Build();

        container.CommandOf().ShouldBe(
            ["inngest", "start", "--signing-key", SigningKey, "--event-key", "events-1", "--poll-interval", "1", "--retry-interval", "1", "--tick", "50"]);
        container.IsDevServer.ShouldBeFalse();
        container.SigningKey.ShouldBe(SigningKey);
        container.EventKey.ShouldBe("events-1");
    }

    [Fact]
    public void It_neither_scans_ports_nor_is_told_not_to_poll_because_start_has_no_such_flags()
    {
        var command = Builder().WithSelfHosting().CommandOf();

        command.ShouldNotContain("--no-discovery");
        command.ShouldNotContain("--no-poll");
    }

    [Fact]
    public void Keys_that_were_not_given_are_generated_and_the_container_tells_them()
    {
        var container = Builder().WithSelfHosting().Build();

        container.SigningKey.ShouldNotBeNull().ShouldMatch("^[0-9a-f]{64}$");
        container.EventKey.ShouldNotBeNull().ShouldNotBeEmpty();
        container.CommandOf().SkipWhile(argument => argument != "--signing-key").Skip(1).First().ShouldBe(container.SigningKey);
        container.CommandOf().SkipWhile(argument => argument != "--event-key").Skip(1).First().ShouldBe(container.EventKey);
    }

    [Fact]
    public void Every_container_gets_keys_of_its_own()
    {
        var builder = Builder().WithSelfHosting();

        var first = builder.Build();
        var second = builder.Build();

        second.SigningKey.ShouldNotBe(first.SigningKey);
        second.EventKey.ShouldNotBe(first.EventKey);
    }

    [Fact]
    public void A_key_given_for_one_of_the_two_leaves_the_other_to_be_generated()
    {
        var container = Builder().WithSelfHosting().WithEventKey("events-1").Build();

        container.EventKey.ShouldBe("events-1");
        container.SigningKey.ShouldNotBeNull().ShouldMatch("^[0-9a-f]{64}$");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("zz")]
    [InlineData("")]
    [InlineData("signkey-prod-9f2c5a7e1b3d4f60")]
    public void The_signing_key_must_be_hexadecimal_digits_in_pairs(string signingKey)
    {
        var exception = Should.Throw<ArgumentException>(() => Builder().WithSelfHosting().WithSigningKey(signingKey).Build());

        exception.Message.ShouldContain("hexadecimal digits in pairs");
    }

    [Fact]
    public void The_event_key_cannot_be_blank()
    {
        Should.Throw<ArgumentException>(() => Builder().WithSelfHosting().WithEventKey(" ").Build());
    }

    [Fact]
    public void The_app_url_and_extra_arguments_follow_the_keys_and_timing()
    {
        var command = Builder().WithSelfHosting().WithSigningKey(SigningKey).WithEventKey("events-1")
            .WithAppUrl("http://localhost:5000/api/inngest").WithCommand("--no-ui").CommandOf();

        command.TakeLast(3).ShouldBe(["-u", "http://host.docker.internal:5000/api/inngest", "--no-ui"]);
    }

    [Fact]
    public void The_generated_signing_key_is_a_key_the_server_accepts()
    {
        // Whatever the generator makes must pass the check that rejects user keys.
        for (var i = 0; i < 20; i++)
        {
            Regex.IsMatch(Builder().WithSelfHosting().Build().SigningKey!, "^([0-9a-f]{2})+$").ShouldBeTrue();
        }
    }
}
