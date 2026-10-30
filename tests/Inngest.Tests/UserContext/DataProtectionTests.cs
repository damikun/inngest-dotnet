using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json;
using Inngest.Tests.Client;
using Inngest.UserContext;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.UserContext;

// The default protector against a real key ring in a folder: what two apps need to share, and what a key's life does to
// payloads that were protected with it.
public sealed class DataProtectionTests : IDisposable
{
    private readonly DirectoryInfo _keys = Directory.CreateTempSubdirectory("inngest-user-context-keys-");
    private readonly List<ServiceProvider> _rings = [];

    public void Dispose()
    {
        foreach (var ring in _rings)
        {
            ring.Dispose();
        }

        _keys.Delete(recursive: true);
    }

    // An app's Data Protection as the app would set it up: a shared folder and an application name.
    private ServiceProvider KeyRing(string applicationName = "shop", Action<IDataProtectionBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddDataProtection().SetApplicationName(applicationName).PersistKeysToFileSystem(_keys);
        configure?.Invoke(builder);
        var ring = services.BuildServiceProvider();
        _rings.Add(ring);
        return ring;
    }

    private static UserContextApp AppOn(IServiceProvider ring) =>
        new(ring.GetRequiredService<IDataProtectionProvider>(), options => options.Claims = ["sub", "name", "role"]);

    private static string Read(UserContextApp app, string user, string eventName = UserContextApp.EventName, DateTimeOffset? at = null)
    {
        var envelope = JsonDocument.Parse(user).RootElement.GetProperty(UserContextCodec.Key).Clone();
        var read = app.Codec.Read(envelope, eventName, at ?? app.Time.GetUtcNow());
        return read.Snapshot is null ? read.Problem.ToString() : "ok:" + string.Join(",", read.Snapshot.Claims.Select(pair => pair.Key));
    }

    // Data Protection writes the id of the key after a four byte header.
    private static Guid KeyIdOf(string user)
    {
        var text = JsonDocument.Parse(user).RootElement.GetProperty(UserContextCodec.Key).GetProperty("t").GetString()!;
        return new Guid(WebEncoders.Base64UrlDecode(text).AsSpan(4, 16));
    }

    private static async Task<T> UntilAsync<T>(Func<T> produce, Func<T, bool> done)
    {
        var value = produce();
        for (var attempt = 0; attempt < 100 && !done(value); attempt++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            value = produce();
        }

        return value;
    }

    [Fact]
    public async Task An_app_that_shares_the_key_ring_and_the_application_name_reads_what_the_other_sent()
    {
        await using var sender = AppOn(KeyRing());
        await using var worker = AppOn(KeyRing());

        var user = sender.Seal(UserContextApp.Ada());

        Read(worker, user).ShouldBe("ok:sub,name,role");
    }

    [Fact]
    public async Task An_app_with_another_application_name_cannot_read_it_even_with_the_same_keys()
    {
        await using var sender = AppOn(KeyRing("shop"));
        await using var worker = AppOn(KeyRing("billing"));

        var user = sender.Seal(UserContextApp.Ada());

        Read(worker, user).ShouldBe("Unreadable");
    }

    [Fact]
    public async Task An_app_with_a_key_ring_of_its_own_cannot_read_it()
    {
        await using var sender = AppOn(KeyRing());
        var elsewhere = new ServiceCollection();
        elsewhere.AddLogging();
        elsewhere.AddDataProtection().SetApplicationName("shop").UseEphemeralDataProtectionProvider();
        await using var worker = AppOn(elsewhere.BuildServiceProvider());

        var user = sender.Seal(UserContextApp.Ada());

        Read(worker, user).ShouldBe("Unreadable");
    }

    [Fact]
    public async Task A_payload_another_feature_protected_with_the_same_keys_is_not_accepted()
    {
        var ring = KeyRing();
        await using var app = AppOn(ring);
        var otherPurpose = ring.GetRequiredService<IDataProtectionProvider>().CreateProtector("Some.Cookie", "v1");
        var plaintext = """{"v":1,"iat":1,"exp":9999999999,"ev":"test/event","c":{"sub":["u-1"]}}""";

        var user = UserContextApp.Envelope(otherPurpose.Protect(plaintext));

        Read(app, user, at: DateTimeOffset.FromUnixTimeSeconds(2)).ShouldBe("Unreadable");
    }

    [Fact]
    public async Task A_payload_made_before_the_keys_rotated_is_still_read_until_its_key_is_revoked()
    {
        var ring = KeyRing();
        await using var app = AppOn(ring);
        var manager = ring.GetRequiredService<IKeyManager>();
        var before = app.Seal(UserContextApp.Ada());
        var oldKey = manager.GetAllKeys().Single();

        // A key that activates a little later is the default from then on, well within the clock skew Data Protection
        // allows. A running app uses it once its cached key ring has refreshed, which Data Protection does in the background.
        var newKey = manager.CreateNewKey(DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddDays(90));
        var after = await UntilAsync(() => app.Seal(UserContextApp.Ada()), user => KeyIdOf(user) == newKey.KeyId);

        KeyIdOf(before).ShouldBe(oldKey.KeyId);
        KeyIdOf(after).ShouldBe(newKey.KeyId);
        Read(app, before).ShouldBe("ok:sub,name,role");
        Read(app, after).ShouldBe("ok:sub,name,role");

        manager.RevokeKey(oldKey.KeyId, "rotation test");

        await UntilAsync(() => Read(app, before), result => result == "Unreadable");
        Read(app, before).ShouldBe("Unreadable");
        Read(app, after).ShouldBe("ok:sub,name,role");
    }

    [Fact]
    public async Task A_payload_protected_with_a_key_that_has_since_expired_is_still_read()
    {
        var ring = KeyRing();
        await using var app = AppOn(ring);
        var manager = ring.GetRequiredService<IKeyManager>();
        var shortLived = manager.CreateNewKey(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        var user = app.Seal(UserContextApp.Ada());

        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);

        // A key that activates later takes over, as the next key of a rotation does.
        manager.CreateNewKey(DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddDays(90));
        shortLived.ExpirationDate.ShouldBeLessThan(DateTimeOffset.UtcNow);
        Read(app, user).ShouldBe("ok:sub,name,role");
        Read(app, app.Seal(UserContextApp.Ada())).ShouldBe("ok:sub,name,role");
    }

    [Fact]
    public async Task Another_protector_can_replace_the_default_and_data_protection_is_then_not_needed()
    {
        var time = new FakeTimeProvider(UserContextApp.Now);
        await using var provider = ClientServices.Build(new FakeHttpHandler(), time, inngest =>
        {
            inngest.Services.AddSingleton<IUserContextProtector, Base64Protector>();
            inngest.AddUserContext(options => options.Claims = ["sub"]);
        });
        var codec = provider.GetRequiredService<UserContextCodec>();

        var text = codec.Protect(codec.Capture(UserContextApp.Principal("Cookies", new Claim("sub", "u-1")))!, UserContextApp.EventName)!;
        var read = codec.Read(JsonDocument.Parse(UserContextApp.Envelope(text)).RootElement.GetProperty(UserContextCodec.Key), UserContextApp.EventName, time.GetUtcNow());

        provider.GetRequiredService<IUserContextProtector>().ShouldBeOfType<Base64Protector>();
        read.Snapshot.ShouldNotBeNull().Claims.Single().Key.ShouldBe("sub");
        new UserContextOptionsValidator(provider).Validate(null, new UserContextOptions()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task A_protector_registered_after_AddUserContext_wins_too()
    {
        await using var provider = ClientServices.Build(new FakeHttpHandler(), new FakeTimeProvider(UserContextApp.Now), inngest =>
        {
            inngest.AddUserContext();
            inngest.Services.AddSingleton<IUserContextProtector, Base64Protector>();
        });

        provider.GetRequiredService<IUserContextProtector>().ShouldBeOfType<Base64Protector>();
    }

    [Fact]
    public async Task Without_data_protection_or_another_protector_the_app_is_told_what_to_do_at_startup()
    {
        await using var provider = ClientServices.Build(new FakeHttpHandler(), new FakeTimeProvider(UserContextApp.Now), inngest => inngest.AddUserContext());

        var failure = Should.Throw<InvalidOperationException>(() => new UserContextOptionsValidator(provider).Validate(null, new UserContextOptions()));

        failure.Message.ShouldContain("AddDataProtection");
        failure.Message.ShouldContain("IUserContextProtector");
    }

    [Fact]
    public async Task The_default_protector_reports_a_changed_payload_as_not_valid_and_not_as_an_exception()
    {
        await using var app = AppOn(KeyRing());
        var text = app.Protector.Protect("""{"a":1}""");

        app.Protector.TryUnprotect(text, out var plaintext).ShouldBeTrue();
        plaintext.ShouldBe("""{"a":1}""");
        app.Protector.TryUnprotect(text + "A", out var changed).ShouldBeFalse();
        changed.ShouldBeNull();
        app.Protector.TryUnprotect(string.Empty, out _).ShouldBeFalse();
    }

    // Not protection at all, which is the point: only a replacement that the app chose is needed to prove it is used.
    private sealed class Base64Protector : IUserContextProtector
    {
        public string Protect(string plaintext) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));

        public bool TryUnprotect(string protectedText, [NotNullWhen(true)] out string? plaintext)
        {
            try
            {
                plaintext = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedText));
                return true;
            }
            catch (FormatException)
            {
                plaintext = null;
                return false;
            }
        }
    }
}
