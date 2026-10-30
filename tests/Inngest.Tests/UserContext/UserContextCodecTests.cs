using System.Security.Claims;
using System.Text.Json;
using Inngest.UserContext;

namespace Inngest.Tests.UserContext;

// The format of a user in an event: which claims are taken, what the protected text holds and when it is refused.
public sealed class UserContextCodecTests
{
    private static readonly string[] Allowed = ["sub", "name", "email", "role"];

    private static UserContextApp NewApp(Action<UserContextOptions>? configure = null) =>
        new(configure: options =>
        {
            options.Claims = Allowed;
            configure?.Invoke(options);
        });

    private static JsonElement EnvelopeOf(string user) =>
        JsonDocument.Parse(user).RootElement.GetProperty(UserContextCodec.Key).Clone();

    [Fact]
    public async Task Only_the_allowed_claims_of_the_principal_are_taken()
    {
        await using var app = NewApp();

        var snapshot = app.Codec.Capture(UserContextApp.Ada()).ShouldNotBeNull();

        snapshot.Claims.Select(pair => pair.Key).ShouldBe(["sub", "name", "email", "role"]);
        snapshot.Claims.Single(pair => pair.Key == "role").Value.ShouldBe(["admin", "support"]);
        snapshot.Claims.Any(pair => pair.Key == "password").ShouldBeFalse();
    }

    [Fact]
    public async Task The_default_allow_list_is_the_subject_only()
    {
        await using var app = new UserContextApp();

        var snapshot = app.Codec.Capture(UserContextApp.Ada(new Claim(ClaimTypes.NameIdentifier, "u-42"))).ShouldNotBeNull();

        snapshot.Claims.Select(pair => pair.Key).ShouldBe(["sub", ClaimTypes.NameIdentifier]);
    }

    [Fact]
    public async Task Claims_are_matched_exactly_as_they_appear_in_the_principal()
    {
        await using var app = NewApp(options => options.Claims = ["Sub", "NAME"]);

        app.Codec.Capture(UserContextApp.Ada()).ShouldBeNull();
    }

    [Fact]
    public async Task Duplicate_and_empty_values_are_dropped()
    {
        await using var app = NewApp();
        var principal = UserContextApp.Principal("Cookies", new Claim("sub", "u-1"), new Claim("role", "a"), new Claim("role", "a"), new Claim("role", string.Empty), new Claim("name", string.Empty));

        var snapshot = app.Codec.Capture(principal).ShouldNotBeNull();

        snapshot.Claims.Select(pair => pair.Key).ShouldBe(["sub", "role"]);
        snapshot.Claims.Single(pair => pair.Key == "role").Value.ShouldBe(["a"]);
    }

    [Fact]
    public async Task Nobody_is_taken_when_there_is_no_authenticated_user_or_none_of_the_claims_is_allowed()
    {
        await using var app = NewApp();

        app.Codec.Capture(null).ShouldBeNull();
        app.Codec.Capture(UserContextApp.Principal(null, new Claim("sub", "u-1"))).ShouldBeNull();
        app.Codec.Capture(UserContextApp.Principal(string.Empty, new Claim("sub", "u-1"))).ShouldBeNull();
        app.Codec.Capture(UserContextApp.Principal("Cookies", new Claim("password", "hunter2"))).ShouldBeNull();
        app.Codec.Capture(new ClaimsPrincipal()).ShouldBeNull();
    }

    [Fact]
    public async Task The_claims_of_every_authenticated_identity_are_taken_and_anonymous_identities_are_skipped()
    {
        await using var app = NewApp();
        var principal = new ClaimsPrincipal(
        [
            new ClaimsIdentity([new Claim("sub", "u-1")]),
            new ClaimsIdentity([new Claim("sub", "u-1"), new Claim("email", "a@example.com")], "Cookies"),
            new ClaimsIdentity([new Claim("role", "admin")], "Bearer"),
        ]);

        var snapshot = app.Codec.Capture(principal).ShouldNotBeNull();

        snapshot.Claims.Select(pair => pair.Key).ShouldBe(["sub", "email", "role"]);
        snapshot.Claims.Single(pair => pair.Key == "sub").Value.ShouldBe(["u-1"]);
    }

    [Fact]
    public async Task Claims_round_trip_with_their_times_and_the_name_and_role_types()
    {
        await using var app = NewApp();
        var snapshot = app.Codec.Capture(UserContextApp.Ada()).ShouldNotBeNull();
        var user = app.Seal(UserContextApp.Ada());

        var read = app.Codec.Read(EnvelopeOf(user), UserContextApp.EventName, app.Time.GetUtcNow());

        read.Problem.ShouldBe(UserReadProblem.None);
        var restored = read.Snapshot.ShouldNotBeNull();
        restored.IssuedAt.ShouldBe(snapshot.IssuedAt);
        restored.ExpiresAt.ShouldBe(snapshot.IssuedAt.AddDays(7));
        restored.NameClaimType.ShouldBe("name");
        restored.RoleClaimType.ShouldBe("role");
        restored.Claims.Select(pair => (pair.Key, string.Join(",", pair.Value))).ShouldBe(
            [("sub", "u-42"), ("name", "Ada Lovelace"), ("email", "ada@example.com"), ("role", "admin,support")]);
    }

    [Fact]
    public async Task The_default_name_and_role_types_are_not_written()
    {
        await using var app = NewApp();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u-1")], "Cookies"));

        var snapshot = app.Codec.Capture(principal).ShouldNotBeNull();

        snapshot.NameClaimType.ShouldBeNull();
        snapshot.RoleClaimType.ShouldBeNull();
    }

    [Fact]
    public async Task The_protected_text_does_not_show_the_claims()
    {
        await using var app = NewApp();

        var user = app.Seal(UserContextApp.Ada());

        user.ShouldNotContain("u-42");
        user.ShouldNotContain("Ada");
        user.ShouldNotContain("ada@example.com");
        user.ShouldStartWith("""{"_inngest_dotnet_user":{"v":1,"t":"CfDJ8""");
    }

    [Fact]
    public async Task Text_that_was_changed_cannot_be_unprotected()
    {
        await using var app = NewApp();
        var text = EnvelopeOf(app.Seal(UserContextApp.Ada())).GetProperty("t").GetString()!;
        var changed = text[..^3] + (text[^3] == 'A' ? 'B' : 'A') + text[^2..];

        var read = app.Codec.Read(EnvelopeOf(UserContextApp.Envelope(changed)), UserContextApp.EventName, app.Time.GetUtcNow());

        read.Snapshot.ShouldBeNull();
        read.Problem.ShouldBe(UserReadProblem.Unreadable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not protected at all")]
    [InlineData("CfDJ8")]
    [InlineData("%%%")]
    public async Task Text_that_was_never_protected_cannot_be_unprotected(string text)
    {
        await using var app = NewApp();

        var read = app.Codec.Read(EnvelopeOf(UserContextApp.Envelope(text)), UserContextApp.EventName, app.Time.GetUtcNow());

        read.Snapshot.ShouldBeNull();
        read.Problem.ShouldBeOneOf(UserReadProblem.Malformed, UserReadProblem.Unreadable);
    }

    [Fact]
    public async Task Text_larger_than_the_bound_allows_is_refused_without_unprotecting_it()
    {
        await using var app = NewApp(options => options.MaxSize = 64);
        var text = new string('A', 64 * 2 + 1024 + 1);

        var read = app.Codec.Read(EnvelopeOf(UserContextApp.Envelope(text)), UserContextApp.EventName, app.Time.GetUtcNow());

        read.Problem.ShouldBe(UserReadProblem.Malformed);
    }

    [Fact]
    public async Task A_payload_for_another_event_is_refused()
    {
        await using var app = NewApp();
        var user = app.Seal(UserContextApp.Ada(), "shop/order.paid");

        var read = app.Codec.Read(EnvelopeOf(user), "shop/account.deleted", app.Time.GetUtcNow());

        read.Snapshot.ShouldBeNull();
        read.Problem.ShouldBe(UserReadProblem.WrongEvent);
    }

    [Fact]
    public async Task The_event_name_is_compared_exactly()
    {
        await using var app = NewApp();
        var user = app.Seal(UserContextApp.Ada(), "shop/order.paid");

        app.Codec.Read(EnvelopeOf(user), "Shop/order.paid", app.Time.GetUtcNow()).Problem.ShouldBe(UserReadProblem.WrongEvent);
        app.Codec.Read(EnvelopeOf(user), "shop/order.paid", app.Time.GetUtcNow()).Problem.ShouldBe(UserReadProblem.None);
    }

    [Fact]
    public async Task A_payload_is_valid_up_to_its_expiry_and_refused_after()
    {
        await using var app = NewApp(options => options.Lifetime = TimeSpan.FromHours(1));
        var user = EnvelopeOf(app.Seal(UserContextApp.Ada()));
        var expires = app.Time.GetUtcNow().AddHours(1);

        app.Codec.Read(user, UserContextApp.EventName, expires).Problem.ShouldBe(UserReadProblem.None);
        app.Codec.Read(user, UserContextApp.EventName, expires.AddSeconds(1)).Problem.ShouldBe(UserReadProblem.Expired);
        app.Codec.Read(user, UserContextApp.EventName, expires.AddDays(30)).Problem.ShouldBe(UserReadProblem.Expired);
    }

    [Fact]
    public async Task The_lifetime_starts_when_the_claims_are_taken_not_when_the_text_is_made()
    {
        await using var app = NewApp(options => options.Lifetime = TimeSpan.FromHours(1));
        var snapshot = app.Codec.Capture(UserContextApp.Ada()).ShouldNotBeNull();
        app.Time.Advance(TimeSpan.FromMinutes(59));
        var text = app.Codec.Protect(snapshot, UserContextApp.EventName)!;

        var read = app.Codec.Read(EnvelopeOf(UserContextApp.Envelope(text)), UserContextApp.EventName, snapshot.IssuedAt.AddMinutes(61));

        read.Problem.ShouldBe(UserReadProblem.Expired);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public async Task A_version_this_package_does_not_know_is_refused_before_anything_is_unprotected(int version)
    {
        await using var app = NewApp();
        var text = EnvelopeOf(app.Seal(UserContextApp.Ada())).GetProperty("t").GetString()!;

        var read = app.Codec.Read(EnvelopeOf(UserContextApp.Envelope(text, version)), UserContextApp.EventName, app.Time.GetUtcNow());

        read.Problem.ShouldBe(UserReadProblem.Unsupported);
    }

    [Theory]
    [InlineData("""{"_inngest_dotnet_user":"text"}""")]
    [InlineData("""{"_inngest_dotnet_user":{"t":"x"}}""")]
    [InlineData("""{"_inngest_dotnet_user":{"v":"1","t":"x"}}""")]
    [InlineData("""{"_inngest_dotnet_user":{"v":1}}""")]
    [InlineData("""{"_inngest_dotnet_user":{"v":1,"t":5}}""")]
    [InlineData("""{"_inngest_dotnet_user":[]}""")]
    public async Task An_envelope_of_the_wrong_shape_is_refused(string user)
    {
        await using var app = NewApp();

        var read = app.Codec.Read(EnvelopeOf(user), UserContextApp.EventName, app.Time.GetUtcNow());

        read.Snapshot.ShouldBeNull();
        read.Problem.ShouldBe(UserReadProblem.Malformed);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"v":1,"iat":1,"exp":2,"ev":"test/event"}""")]
    [InlineData("""{"v":1,"iat":1,"exp":2,"ev":"test/event","c":{}}""")]
    [InlineData("""{"v":1,"iat":1,"exp":2,"ev":"test/event","c":{"sub":[]}}""")]
    [InlineData("""{"v":1,"iat":1,"exp":2,"ev":"test/event","c":{"sub":["a",5]}}""")]
    [InlineData("""{"v":1,"iat":1,"exp":2,"ev":"test/event","c":{"sub":"a"}}""")]
    [InlineData("""{"v":2,"iat":1,"exp":2,"ev":"test/event","c":{"sub":["a"]}}""")]
    [InlineData("""{"v":1,"iat":5,"exp":2,"ev":"test/event","c":{"sub":["a"]}}""")]
    [InlineData("""{"v":1,"iat":-1,"exp":2,"ev":"test/event","c":{"sub":["a"]}}""")]
    [InlineData("""{"v":1,"iat":1,"exp":99999999999999,"ev":"test/event","c":{"sub":["a"]}}""")]
    [InlineData("""{"v":1,"iat":1,"exp":2,"ev":5,"c":{"sub":["a"]}}""")]
    [InlineData("""{"v":1,"iat":1,"exp":2,"ev":"test/event","nt":5,"c":{"sub":["a"]}}""")]
    public async Task A_payload_that_was_protected_but_is_not_a_user_is_refused(string payload)
    {
        await using var app = NewApp();
        var text = app.Protector.Protect(payload);

        var read = app.Codec.Read(EnvelopeOf(UserContextApp.Envelope(text)), UserContextApp.EventName, DateTimeOffset.FromUnixTimeSeconds(1));

        read.Snapshot.ShouldBeNull();
        read.Problem.ShouldBe(UserReadProblem.Malformed);
    }

    [Fact]
    public async Task Claims_larger_than_the_bound_are_not_protected()
    {
        await using var app = NewApp(options => options.MaxSize = 200);
        var small = UserContextApp.Principal("Cookies", new Claim("sub", "u-1"));
        var large = UserContextApp.Principal("Cookies", new Claim("sub", "u-1"), new Claim("name", new string('x', 300)));

        app.Codec.Protect(app.Codec.Capture(small)!, UserContextApp.EventName).ShouldNotBeNull();
        app.Codec.Protect(app.Codec.Capture(large)!, UserContextApp.EventName).ShouldBeNull();
    }

    [Fact]
    public async Task The_bound_counts_utf8_bytes_and_text_is_not_escaped_into_more()
    {
        await using var app = NewApp(options => options.MaxSize = 250);
        var principal = UserContextApp.Principal("Cookies", new Claim("sub", "u-1"), new Claim("name", new string('Ž', 40)));

        var snapshot = app.Codec.Capture(principal)!;
        var text = app.Codec.Protect(snapshot, UserContextApp.EventName);

        text.ShouldNotBeNull();
        app.Protector.TryUnprotect(text, out var plaintext).ShouldBeTrue();
        plaintext.ShouldContain(new string('Ž', 40));
    }

    [Fact]
    public async Task Unicode_in_claims_survives_the_round_trip()
    {
        await using var app = NewApp();
        var principal = UserContextApp.Principal("Cookies", new Claim("sub", "u-1"), new Claim("name", "Žofie Nováková 🎉"));

        var read = app.Codec.Read(EnvelopeOf(app.Seal(principal)), UserContextApp.EventName, app.Time.GetUtcNow());

        read.Snapshot.ShouldNotBeNull().Claims.Single(pair => pair.Key == "name").Value.ShouldBe(["Žofie Nováková 🎉"]);
    }

    [Fact]
    public void Invalid_options_are_reported_all_at_once()
    {
        var problems = UserContextOptionsValidator.Check(new UserContextOptions
        {
            Claims = [],
            Lifetime = TimeSpan.Zero,
            MaxSize = 10,
            OnInvalid = (InvalidUserContextAction)9,
        });

        problems.Count.ShouldBe(4);
        problems.ShouldContain(problem => problem.Contains("Claims", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.Contains("Lifetime", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.Contains("MaxSize", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.Contains("OnInvalid", StringComparison.Ordinal));
        UserContextOptionsValidator.Check(new UserContextOptions { Claims = ["sub", " "] }).ShouldHaveSingleItem();
        UserContextOptionsValidator.Check(new UserContextOptions { Lifetime = TimeSpan.FromDays(3651) }).ShouldHaveSingleItem();
        UserContextOptionsValidator.Check(new UserContextOptions()).ShouldBeEmpty();
    }
}
