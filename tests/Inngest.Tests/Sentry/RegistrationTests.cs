using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Inngest.Tests.Sentry;

public sealed class RegistrationTests
{
    [Fact]
    public void Adding_the_reporting_twice_registers_one_middleware()
    {
        var services = new ServiceCollection();
        services.AddInngest(options => options.AppId = "shop").AddSentryReporting().AddSentryReporting();

        services.Count(descriptor => descriptor.ServiceType == typeof(InngestMiddleware)).ShouldBe(1);
    }

    [Fact]
    public void The_options_default_to_reporting_the_final_attempt_of_functions_and_flushing_after_every_call()
    {
        var options = new SentryReportingOptions();

        options.OnlyCaptureFinalAttempt.ShouldBeTrue();
        options.CaptureStepErrors.ShouldBeFalse();
        options.DisableAutomaticFlush.ShouldBeFalse();
        options.FlushTimeout.ShouldBe(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Every_callback_changes_the_options_in_the_order_it_was_added()
    {
        var services = new ServiceCollection();
        services.AddInngest(options => options.AppId = "shop")
            .AddSentryReporting(options =>
            {
                options.CaptureStepErrors = true;
                options.FlushTimeout = TimeSpan.FromSeconds(5);
            })
            .AddSentryReporting(options => options.FlushTimeout = TimeSpan.FromSeconds(9));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<SentryReportingOptions>>().Value;

        options.CaptureStepErrors.ShouldBeTrue();
        options.FlushTimeout.ShouldBe(TimeSpan.FromSeconds(9));
    }

    [Fact]
    public void A_builder_is_required()
    {
        IInngestBuilder builder = null!;

        Should.Throw<ArgumentNullException>(() => builder.AddSentryReporting());
    }
}
