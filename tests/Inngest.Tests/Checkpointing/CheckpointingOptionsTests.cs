using System.Text.Json;
using Inngest.Client;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.Tests.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.Checkpointing;

public sealed class CheckpointingOptionsTests
{
    private static readonly FunctionOptions Function = new() { Id = "orders", Triggers = [Trigger.Event("order/created")] };

    [Fact]
    public void The_defaults_save_every_step_and_leave_the_runtime_to_the_host()
    {
        var options = new CheckpointingOptions();

        options.Enabled.ShouldBeTrue();
        options.BufferedSteps.ShouldBe(1);
        options.MaxRuntime.ShouldBeNull();
        options.MaxInterval.ShouldBeNull();
        Checkpointer.ServeMaxRuntime.ShouldBe(TimeSpan.FromSeconds(10));
        Checkpointer.ConnectMaxRuntime.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Checkpointing_is_off_unless_the_app_turns_it_on() => TestSettings.Create().Checkpointing.ShouldBeNull();

    [Fact]
    public void The_app_setting_is_kept_once_it_is_valid()
    {
        var checkpointing = new CheckpointingOptions { BufferedSteps = 3, MaxRuntime = Timeout.InfiniteTimeSpan, MaxInterval = TimeSpan.FromSeconds(2) };

        TestSettings.Create(options => options.Checkpointing = checkpointing).Checkpointing.ShouldBeSameAs(checkpointing);
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Invalid_app_settings_fail_at_startup(CheckpointingOptions checkpointing, string problem)
    {
        var exception = Should.Throw<InvalidOperationException>(() => TestSettings.Create(options => options.Checkpointing = checkpointing));

        exception.Message.ShouldBe($"InngestOptions.Checkpointing.{problem}");
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Invalid_function_settings_fail_at_startup_and_name_the_function(CheckpointingOptions checkpointing, string problem)
    {
        var exception = Should.Throw<InvalidOperationException>(() => FunctionOptionsValidator.Validate(Function with { Checkpointing = checkpointing }));

        exception.Message.ShouldBe($"Function \"orders\": Checkpointing.{problem}");
    }

    [Fact]
    public void A_function_may_turn_checkpointing_off_or_lift_the_runtime_limit()
    {
        FunctionOptionsValidator.Validate(Function with { Checkpointing = new CheckpointingOptions { Enabled = false } });
        FunctionOptionsValidator.Validate(Function with { Checkpointing = new CheckpointingOptions { MaxRuntime = Timeout.InfiniteTimeSpan } });
    }

    public static TheoryData<CheckpointingOptions, string> InvalidOptions() => new()
    {
        { new CheckpointingOptions { BufferedSteps = 0 }, "BufferedSteps must be at least 1, but was 0." },
        { new CheckpointingOptions { MaxRuntime = TimeSpan.Zero }, "MaxRuntime must be greater than zero, or Timeout.InfiniteTimeSpan for no limit, but was 00:00:00." },
        { new CheckpointingOptions { MaxRuntime = TimeSpan.FromSeconds(-2) }, "MaxRuntime must be greater than zero, or Timeout.InfiniteTimeSpan for no limit, but was -00:00:02." },
        { new CheckpointingOptions { MaxInterval = TimeSpan.Zero }, "MaxInterval must be greater than zero, but was 00:00:00." },
    };

    [Fact]
    public void A_call_names_the_function_and_queue_item_a_checkpoint_needs()
    {
        using var document = JsonDocument.Parse("""{"ctx":{"fn_id":"0b3c7d2a-9f2e-5b4e-8c1d-6a7b8c9d0e1f","qi_id":"cWktMQ==","run_id":"01JRUN"}}""");

        var request = RunRequest.Parse(document.RootElement);

        request.InternalFunctionId.ShouldBe("0b3c7d2a-9f2e-5b4e-8c1d-6a7b8c9d0e1f");
        request.QueueItemId.ShouldBe("cWktMQ==");
    }

    [Theory]
    [InlineData("""{"ctx":{}}""")]
    [InlineData("""{"ctx":{"fn_id":"","qi_id":""}}""")]
    [InlineData("""{"ctx":{"fn_id":7,"qi_id":null}}""")]
    public void Missing_or_unusable_ids_read_as_none(string json)
    {
        using var document = JsonDocument.Parse(json);

        var request = RunRequest.Parse(document.RootElement);

        request.InternalFunctionId.ShouldBeNull();
        request.QueueItemId.ShouldBeNull();
    }

    [Fact]
    public async Task AddInngest_gives_the_engine_the_checkpoint_client()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInngest(options => options.AppId = "shop");
        await using var provider = services.BuildServiceProvider();

        var engine = provider.GetRequiredService<IExecutionEngine>().ShouldBeOfType<ExecutionEngine>();

        engine.Checkpoints.ShouldBeOfType<CheckpointApiClient>();
        engine.Checkpoints.ShouldBeSameAs(provider.GetRequiredService<ICheckpointApi>());
    }
}
