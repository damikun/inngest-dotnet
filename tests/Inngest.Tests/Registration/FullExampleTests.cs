using Inngest.Internal;
using Inngest.Registration;
using static Inngest.Tests.Registration.RegistryFixtures;

namespace Inngest.Tests.Registration;

public sealed class FullExampleTests
{
    // The function and sync of the TypeScript SDK notes that use every option, written with the .NET options. Cron
    // triggers take a time zone instead of a TZ= prefix, and have no jitter.
    [Fact]
    public void Writes_a_function_with_every_option_and_its_failure_handler()
    {
        var options = new FunctionOptions
        {
            Id = "sync-user",
            Name = "Sync user",
            Triggers =
            [
                Trigger.Event("app/user.created", "event.data.plan == 'pro'"),
                Trigger.Cron("0 9 * * 1-5", "Europe/Prague"),
            ],
            Retries = 5,
            Concurrency =
            [
                new ConcurrencyLimit(10),
                new ConcurrencyLimit(1) { Key = "event.data.user_id", Scope = ConcurrencyScope.Environment },
            ],
            Throttle = new Throttle(5, TimeSpan.FromMinutes(1)) { Burst = 2, Key = "event.data.user_id" },
            RateLimit = new RateLimit(1, TimeSpan.FromHours(4)) { Key = "event.data.user_id" },
            Idempotency = "event.data.user_id",
            Debounce = new Debounce(TimeSpan.FromSeconds(5)) { Key = "event.data.user_id", Timeout = TimeSpan.FromMinutes(1) },
            Priority = new Priority("event.data.plan == 'pro' ? 120 : 0"),
            Timeouts = new FunctionTimeouts { Start = TimeSpan.FromMinutes(10), Finish = TimeSpan.FromHours(1) },
            Singleton = new Singleton(SingletonMode.Skip) { Key = "event.data.user_id" },
            CancelOn =
            [
                new CancelOn("app/user.deleted") { Match = "data.user_id", Timeout = TimeSpan.FromHours(1) },
                new CancelOn("app/user.paused") { If = "async.data.id == event.data.id", Timeout = TimeSpan.FromMinutes(1) },
            ],
        };
        var registry = new FunctionRegistry(WithFailureHandler(options), Settings());

        var body = Write(registry);

        body.ShouldBe(Compact($$"""
            {
              "url": "https://example.com/api/inngest",
              "deployType": "ping",
              "appName": "my-app",
              "sdk": "{{SdkInfo.RegistrationValue}}",
              "v": "0.1",
              "framework": "aspnetcore",
              "capabilities": { "trust_probe": "v1" },
              "functions": [
                {
                  "id": "my-app-sync-user",
                  "name": "Sync user",
                  "triggers": [
                    { "event": "app/user.created", "expression": "event.data.plan == 'pro'" },
                    { "cron": "TZ=Europe/Prague 0 9 * * 1-5" }
                  ],
                  "steps": {
                    "step": {
                      "id": "step",
                      "name": "step",
                      "runtime": {
                        "type": "http",
                        "url": "https://example.com/api/inngest?fnId=my-app-sync-user&stepId=step"
                      },
                      "retries": { "attempts": 5 }
                    }
                  },
                  "concurrency": [
                    { "limit": 10, "scope": "fn" },
                    { "limit": 1, "key": "event.data.user_id", "scope": "env" }
                  ],
                  "throttle": { "limit": 5, "period": "1m", "burst": 2, "key": "event.data.user_id" },
                  "rateLimit": { "limit": 1, "period": "4h", "key": "event.data.user_id" },
                  "idempotency": "event.data.user_id",
                  "debounce": { "period": "5s", "key": "event.data.user_id", "timeout": "1m" },
                  "priority": { "run": "event.data.plan == 'pro' ? 120 : 0" },
                  "cancel": [
                    { "event": "app/user.deleted", "timeout": "1h", "if": "event.data.user_id == async.data.user_id" },
                    { "event": "app/user.paused", "timeout": "1m", "if": "async.data.id == event.data.id" }
                  ],
                  "timeouts": { "start": "10m", "finish": "1h" },
                  "singleton": { "mode": "skip", "key": "event.data.user_id" }
                },
                {
                  "id": "my-app-sync-user-failure",
                  "name": "Sync user (failure)",
                  "triggers": [
                    { "event": "inngest/function.failed", "expression": "event.data.function_id == 'my-app-sync-user'" }
                  ],
                  "steps": {
                    "step": {
                      "id": "step",
                      "name": "step",
                      "runtime": {
                        "type": "http",
                        "url": "https://example.com/api/inngest?fnId=my-app-sync-user-failure&stepId=step"
                      },
                      "retries": { "attempts": 1 }
                    }
                  }
                }
              ]
            }
            """));
    }

    [Fact]
    public void Writes_the_sync_of_a_request_that_carries_a_deploy_id_and_other_parameters()
    {
        var registry = Registry(Orders with { Retries = 3 });

        var body = Write(registry, "https://example.com/api/inngest?region=eu&deployId=01HZ", "aspnetcore");

        body.ShouldBe(Compact($$"""
            {
              "url": "https://example.com/api/inngest?region=eu",
              "deployType": "ping",
              "appName": "my-app",
              "sdk": "{{SdkInfo.RegistrationValue}}",
              "v": "0.1",
              "framework": "aspnetcore",
              "capabilities": { "trust_probe": "v1" },
              "functions": [
                {
                  "id": "my-app-orders",
                  "name": "orders",
                  "triggers": [ { "event": "order/created" } ],
                  "steps": {
                    "step": {
                      "id": "step",
                      "name": "step",
                      "runtime": {
                        "type": "http",
                        "url": "https://example.com/api/inngest?region=eu&fnId=my-app-orders&stepId=step"
                      },
                      "retries": { "attempts": 3 }
                    }
                  }
                }
              ]
            }
            """));
    }
}
