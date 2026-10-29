using System.Text.Json;
using Inngest.Functions;
using Inngest.Registration;
using Inngest.Tests.Events;
using Microsoft.Extensions.DependencyInjection;
using static Inngest.Tests.Registration.RegistryFixtures;

namespace Inngest.Tests.Registration;

// Attributes are another way to write FunctionOptions: each sets the member of the same name, AddFunction<T>() reads
// them once, and the result goes through the startup validator like options written in code.
public sealed class FunctionAttributeTests
{
    // What Everything says with attributes, written in code.
    private static readonly FunctionOptions EverythingInCode = new()
    {
        Id = "everything",
        Name = "Every attribute",
        Retries = 3,
        Idempotency = "event.data.orderId",
        Triggers =
        [
            Trigger.Event("shop/order.created", "event.data.total > 100"),
            Trigger.Event<OrderPaid>(),
            Trigger.Cron("0 9 * * 1-5", TimeSpan.FromSeconds(30), "Europe/Prague"),
        ],
        Concurrency =
        [
            new ConcurrencyLimit(10),
            new ConcurrencyLimit(1) { Key = "event.data.accountId", Scope = ConcurrencyScope.Account },
        ],
        Throttle = new Throttle(5, TimeSpan.FromMinutes(1)) { Burst = 2, Key = "event.data.accountId" },
        RateLimit = new RateLimit(3, TimeSpan.FromHours(24)) { Key = "event.data.accountId" },
        Debounce = new Debounce(TimeSpan.FromSeconds(5)) { Key = "event.data.accountId", Timeout = TimeSpan.FromMinutes(1) },
        Priority = new Priority("event.data.vip ? 120 : 0"),
        Singleton = new Singleton(SingletonMode.Cancel) { Key = "event.data.accountId" },
        CancelOn =
        [
            new CancelOn("shop/order.cancelled") { Match = "data.orderId", Timeout = TimeSpan.FromHours(1) },
            CancelOn.For<OrderRefunded>() with { If = "async.data.full" },
        ],
        Timeouts = new FunctionTimeouts { Start = TimeSpan.FromMinutes(10), Finish = TimeSpan.FromHours(1) },
    };

    [Fact]
    public void Each_attribute_sets_the_option_of_the_same_name()
    {
        var options = FunctionAttributes.Read(typeof(Everything));

        options.Id.ShouldBe(EverythingInCode.Id);
        options.Name.ShouldBe(EverythingInCode.Name);
        options.Retries.ShouldBe(EverythingInCode.Retries);
        options.Idempotency.ShouldBe(EverythingInCode.Idempotency);
        options.Triggers.ShouldBe(EverythingInCode.Triggers);
        options.Concurrency.ShouldBe(EverythingInCode.Concurrency);
        options.Throttle.ShouldBe(EverythingInCode.Throttle);
        options.RateLimit.ShouldBe(EverythingInCode.RateLimit);
        options.Debounce.ShouldBe(EverythingInCode.Debounce);
        options.Priority.ShouldBe(EverythingInCode.Priority);
        options.Singleton.ShouldBe(EverythingInCode.Singleton);
        options.CancelOn.ShouldBe(EverythingInCode.CancelOn);
        options.Timeouts.ShouldBe(EverythingInCode.Timeouts);
        options.BatchEvents.ShouldBeNull();
        options.Middleware.ShouldBeNull();
        options.Checkpointing.ShouldBeNull();
    }

    [Fact]
    public void Attributes_and_the_same_options_in_code_sync_the_same_function()
    {
        Function(FunctionAttributes.Read(typeof(Everything))).ShouldBe(Function(EverythingInCode));
    }

    [Fact]
    public void Batching_is_read_from_its_attribute()
    {
        var options = FunctionAttributes.Read(typeof(Batched));

        options.BatchEvents.ShouldBe(new BatchEvents(50, TimeSpan.FromSeconds(5)) { Key = "event.data.tenant", If = "event.data.batch == true" });
        Property(options, "batchEvents").ShouldBe("""{"maxSize":50,"timeout":"5s","key":"event.data.tenant","if":"event.data.batch == true"}""");
    }

    [Fact]
    public void The_generic_attributes_take_the_event_name_from_the_type()
    {
        var options = FunctionAttributes.Read(typeof(Typed));

        options.Triggers.ShouldBe([new EventTrigger("shop/order.paid") { If = "event.data.amount > 100" }, new EventTrigger("shop/order.refunded")]);
        options.CancelOn.ShouldBe([new CancelOn("shop/account.closed") { Match = "data.accountId", Timeout = TimeSpan.FromDays(7) }]);
    }

    [Fact]
    public void A_class_with_only_the_function_attribute_has_no_triggers_and_no_flow_control()
    {
        var options = FunctionAttributes.Read(typeof(Bare));

        options.Id.ShouldBe("bare");
        options.Name.ShouldBeNull();
        options.Triggers.ShouldBeEmpty();
        options.Retries.ShouldBeNull();
        options.Idempotency.ShouldBeNull();
        options.Concurrency.ShouldBeNull();
        options.Throttle.ShouldBeNull();
        options.RateLimit.ShouldBeNull();
        options.Debounce.ShouldBeNull();
        options.BatchEvents.ShouldBeNull();
        options.Priority.ShouldBeNull();
        options.CancelOn.ShouldBeNull();
        options.Timeouts.ShouldBeNull();
        options.Singleton.ShouldBeNull();
    }

    [Fact]
    public void Zero_retries_and_a_zero_burst_are_kept_apart_from_unset_ones()
    {
        var options = FunctionAttributes.Read(typeof(Zeroes));

        options.Retries.ShouldBe(0);
        options.Throttle!.Burst.ShouldBe(0);
        FunctionAttributes.Read(typeof(Bare)).Retries.ShouldBeNull();
        FunctionAttributes.Read(typeof(Batched)).Retries.ShouldBeNull();
    }

    [Fact]
    public void A_timeouts_attribute_may_set_only_one_of_the_timeouts()
    {
        FunctionAttributes.Read(typeof(Zeroes)).Timeouts.ShouldBe(new FunctionTimeouts { Finish = TimeSpan.FromDays(1) });
    }

    [Fact]
    public void AddFunction_registers_the_class_with_the_options_its_attributes_give()
    {
        var registry = RegistryOf(inngest => inngest.AddFunction<Everything>());

        var function = registry.Functions.ShouldHaveSingleItem();
        function.FunctionType.ShouldBe(typeof(Everything));
        FunctionAt(Write(registry), 0).ShouldBe(Function(EverythingInCode));
    }

    [Fact]
    public void Explicit_options_replace_the_attributes()
    {
        var registry = RegistryOf(inngest => inngest.AddFunction<Everything>(new FunctionOptions { Id = "explicit", Triggers = [Trigger.Event("shop/other")] }));

        FunctionAt(Write(registry), 0).ShouldBe(
            """{"id":"my-app-explicit","name":"explicit","triggers":[{"event":"shop/other"}],"steps":{"step":{"id":"step","name":"step","runtime":{"type":"http","url":"https://example.com/api/inngest?fnId=my-app-explicit&stepId=step"}}}}""");
    }

    [Fact]
    public void Configure_gets_the_options_of_the_attributes_and_returns_what_is_registered()
    {
        FunctionOptions? received = null;
        var registry = RegistryOf(inngest => inngest.AddFunction<Everything>(options =>
        {
            received = options;
            return options with { Concurrency = [new ConcurrencyLimit(2)], Retries = 0 };
        }));

        received.ShouldNotBeNull();
        Function(received).ShouldBe(Function(EverythingInCode));
        FunctionAt(Write(registry), 0).ShouldBe(Function(EverythingInCode with { Concurrency = [new ConcurrencyLimit(2)], Retries = 0 }));
    }

    [Fact]
    public void Configure_can_add_what_attributes_cannot_say()
    {
        var checkpointing = new CheckpointingOptions { BufferedSteps = 3 };
        var registry = RegistryOf(inngest => inngest.AddFunction<Bare>(options => options with { Checkpointing = checkpointing }));

        registry.Functions.ShouldHaveSingleItem().Options.Checkpointing.ShouldBe(checkpointing);
    }

    [Fact]
    public void Configure_must_return_options()
    {
        var services = new ServiceCollection();
        var inngest = services.AddInngest(options => options.AppId = AppId);

        var exception = Should.Throw<InvalidOperationException>(() => inngest.AddFunction<Bare>(_ => null!));

        exception.Message.ShouldBe("The configure callback for Bare returned null instead of the function's options.");
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        var inngest = new ServiceCollection().AddInngest(options => options.AppId = AppId);

        Should.Throw<ArgumentNullException>(() => ((IInngestBuilder)null!).AddFunction<Bare>());
        Should.Throw<ArgumentNullException>(() => ((IInngestBuilder)null!).AddFunction<Bare>(options => options));
        Should.Throw<ArgumentNullException>(() => inngest.AddFunction<Bare>((Func<FunctionOptions, FunctionOptions>)null!));
    }

    [Fact]
    public void A_class_without_the_function_attribute_cannot_be_added_by_its_attributes()
    {
        var inngest = new ServiceCollection().AddInngest(options => options.AppId = AppId);

        var exception = Should.Throw<InvalidOperationException>(() => inngest.AddFunction<Unmarked>());

        exception.Message.ShouldBe(
            "Unmarked has no [InngestFunction] attribute. Add one with the function's id, or pass FunctionOptions to AddFunction<Unmarked>.");
    }

    [Fact]
    public void Attributes_of_a_base_class_do_not_apply_to_the_classes_derived_from_it()
    {
        Should.Throw<InvalidOperationException>(() => FunctionAttributes.Read(typeof(Derived)))
            .Message.ShouldStartWith("Derived has no [InngestFunction] attribute.");
    }

    [Theory]
    [InlineData(typeof(BadThrottle), "Function \"bad-throttle\": Period of [Throttle] is \"5 minutes\", which is not a time string.")]
    [InlineData(typeof(BadRateLimit), "Function \"bad-rate-limit\": Period of [RateLimit] is \"1.5h\", which is not a time string.")]
    [InlineData(typeof(BadDebounce), "Function \"bad-debounce\": Timeout of [Debounce] is \"\", which is not a time string.")]
    [InlineData(typeof(BadBatch), "Function \"bad-batch\": Timeout of [BatchEvents] is \"00:00:05\", which is not a time string.")]
    [InlineData(typeof(BadJitter), "Function \"bad-jitter\": Jitter of [CronTrigger] is \"30\", which is not a time string.")]
    [InlineData(typeof(BadCancelOn), "Function \"bad-cancel-on\": Timeout of [CancelOn] is \"1 day\", which is not a time string.")]
    [InlineData(typeof(BadTimeouts), "Function \"bad-timeouts\": Start of [Timeouts] is \"10M\", which is not a time string.")]
    public void A_duration_that_is_not_a_time_string_fails_at_registration_and_names_the_function(Type functionType, string expected)
    {
        var exception = Should.Throw<InvalidOperationException>(() => FunctionAttributes.Read(functionType));

        exception.Message.ShouldBe(expected + " Use whole numbers with w, d, h, m or s, such as \"30s\", \"5m\" or \"1h30m\".");
    }

    [Fact]
    public void A_bad_duration_fails_the_call_to_AddFunction()
    {
        var inngest = new ServiceCollection().AddInngest(options => options.AppId = AppId);

        Should.Throw<InvalidOperationException>(() => inngest.AddFunction<BadThrottle>())
            .Message.ShouldStartWith("Function \"bad-throttle\": Period of [Throttle]");
    }

    [Theory]
    [InlineData(typeof(TooManyRetries), "Function \"too-many-retries\": Retries must be between 0 and 20, but was 25.")]
    [InlineData(typeof(ShortThrottle), "Function \"short-throttle\": Throttle.Period must be at least 1s, but was 0s.")]
    [InlineData(typeof(TooManyConcurrencyLimits), "Function \"too-many-limits\": Concurrency allows at most 2 limits, but 3 were given.")]
    [InlineData(typeof(BatchedAndCancelled), "Function \"batched-and-cancelled\": BatchEvents cannot be combined with CancelOn.")]
    [InlineData(typeof(UnscopedKeylessLimit), "Function \"keyless\": A concurrency limit with the Account scope needs a Key. Only the Function scope can go without one.")]
    [InlineData(typeof(BlankId), "A function has no Id. Set FunctionOptions.Id to an id that is unique in the app and never changes.")]
    public void Values_Inngest_rejects_fail_at_startup_in_the_same_validator_as_options_in_code(Type functionType, string expected)
    {
        var options = FunctionAttributes.Read(functionType);

        Should.Throw<InvalidOperationException>(() => Registry(options)).Message.ShouldBe(expected);
    }

    [Fact]
    public void The_startup_validator_rejects_attribute_values_when_the_app_resolves_its_functions()
    {
        var services = new ServiceCollection();
        services.AddInngest(options => options.AppId = AppId).AddFunction<TooManyRetries>();
        using var provider = services.BuildServiceProvider();

        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<IFunctionRegistry>())
            .Message.ShouldBe("Function \"too-many-retries\": Retries must be between 0 and 20, but was 25.");
    }

    [Fact]
    public void A_class_with_attributes_that_handles_its_failures_gets_its_failure_function()
    {
        var registry = RegistryOf(inngest => inngest.AddFunction<HandlesFailures>());

        FunctionIds(Write(registry)).ShouldBe(["my-app-handles-failures", "my-app-handles-failures-failure"]);
        registry.Functions[1].Options.Name.ShouldBe("Handles failures (failure)");
    }

    private static IFunctionRegistry RegistryOf(Action<IInngestBuilder> configure)
    {
        var services = new ServiceCollection();
        configure(services.AddInngest(options => options.AppId = AppId));
        return services.BuildServiceProvider().GetRequiredService<IFunctionRegistry>();
    }

    [InngestFunction("everything", Name = "Every attribute", Retries = 3, Idempotency = "event.data.orderId")]
    [EventTrigger("shop/order.created", If = "event.data.total > 100")]
    [EventTrigger<OrderPaid>]
    [CronTrigger("0 9 * * 1-5", Timezone = "Europe/Prague", Jitter = "30s")]
    [Concurrency(10)]
    [Concurrency(1, Key = "event.data.accountId", Scope = ConcurrencyScope.Account)]
    [Throttle(5, "1m", Burst = 2, Key = "event.data.accountId")]
    [RateLimit(3, "1d", Key = "event.data.accountId")]
    [Debounce("5s", Key = "event.data.accountId", Timeout = "1m")]
    [Priority("event.data.vip ? 120 : 0")]
    [Singleton(SingletonMode.Cancel, Key = "event.data.accountId")]
    [CancelOn("shop/order.cancelled", Match = "data.orderId", Timeout = "1h")]
    [CancelOn<OrderRefunded>(If = "async.data.full")]
    [Timeouts(Start = "10m", Finish = "1h")]
    public sealed class Everything : NoOp;

    [InngestFunction("batched")]
    [EventTrigger<OrderPaid>]
    [BatchEvents(50, "5s", Key = "event.data.tenant", If = "event.data.batch == true")]
    public sealed class Batched : NoOp;

    [InngestFunction("typed")]
    [EventTrigger<OrderPaid>(If = "event.data.amount > 100")]
    [EventTrigger<OrderRefunded>]
    [CancelOn<AccountClosed>(Match = "data.accountId", Timeout = "1w")]
    public sealed class Typed : NoOp;

    [InngestFunction("bare")]
    public sealed class Bare : NoOp;

    [InngestFunction("zeroes", Retries = 0)]
    [Throttle(1, "1s", Burst = 0)]
    [Timeouts(Finish = "1d")]
    public sealed class Zeroes : NoOp;

    public sealed class Unmarked : NoOp;

    [InngestFunction("base")]
    [Concurrency(3)]
    public abstract class Base : NoOp;

    public sealed class Derived : Base;

    [InngestFunction("bad-throttle")]
    [Throttle(5, "5 minutes")]
    public sealed class BadThrottle : NoOp;

    [InngestFunction("bad-rate-limit")]
    [RateLimit(5, "1.5h")]
    public sealed class BadRateLimit : NoOp;

    [InngestFunction("bad-debounce")]
    [Debounce("5s", Timeout = "")]
    public sealed class BadDebounce : NoOp;

    [InngestFunction("bad-batch")]
    [BatchEvents(10, "00:00:05")]
    public sealed class BadBatch : NoOp;

    [InngestFunction("bad-jitter")]
    [CronTrigger("* * * * *", Jitter = "30")]
    public sealed class BadJitter : NoOp;

    [InngestFunction("bad-cancel-on")]
    [CancelOn("shop/order.cancelled", Timeout = "1 day")]
    public sealed class BadCancelOn : NoOp;

    [InngestFunction("bad-timeouts")]
    [Timeouts(Start = "10M")]
    public sealed class BadTimeouts : NoOp;

    [InngestFunction("too-many-retries", Retries = 25)]
    public sealed class TooManyRetries : NoOp;

    [InngestFunction("short-throttle")]
    [Throttle(5, "0s")]
    public sealed class ShortThrottle : NoOp;

    [InngestFunction("too-many-limits")]
    [Concurrency(1)]
    [Concurrency(2)]
    [Concurrency(3)]
    public sealed class TooManyConcurrencyLimits : NoOp;

    [InngestFunction("batched-and-cancelled")]
    [BatchEvents(10, "5s")]
    [CancelOn<OrderRefunded>]
    public sealed class BatchedAndCancelled : NoOp;

    [InngestFunction("keyless")]
    [Concurrency(5, Scope = ConcurrencyScope.Account)]
    public sealed class UnscopedKeylessLimit : NoOp;

    [InngestFunction(" ")]
    public sealed class BlankId : NoOp;

    [InngestFunction("handles-failures", Name = "Handles failures")]
    [EventTrigger<OrderPaid>]
    public sealed class HandlesFailures : NoOp, IFailureHandler
    {
        public Task OnFailureAsync(FunctionContext<FunctionFailure> context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public abstract class NoOp : InngestFunction<JsonElement>
    {
        public override Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken) =>
            Task.FromResult<object?>(null);
    }
}
