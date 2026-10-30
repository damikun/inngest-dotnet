# inngest-dotnet

Durable functions for .NET. Write background jobs and workflows as plain C# methods. Inngest runs every step once,
retries only what fails and lets a run wait for days, so you ship reliable workflows without queues, state tables or
cron glue.

inngest-dotnet is a community .NET SDK for [Inngest](https://www.inngest.com), ported from the Inngest TypeScript SDK,
[inngest-js](https://github.com/inngest/inngest-js). It targets .NET 8 and .NET 10 and is Native AOT compatible.

## Packages

- [`Inngest`](https://www.nuget.org/packages/Inngest/): functions, steps, triggers, flow control, middleware, the
  client that sends events, and request signing.
- [`Inngest.AspNetCore`](https://www.nuget.org/packages/Inngest.AspNetCore/): `MapInngest`, the endpoint Inngest calls
  in an ASP.NET Core app.
- [`Inngest.Connect`](https://www.nuget.org/packages/Inngest.Connect/): `AddConnect`, a hosted worker that receives
  calls over an outbound WebSocket, with no endpoint to expose.
- [`Inngest.Testing`](https://www.nuget.org/packages/Inngest.Testing/): an in-memory test host that runs your functions
  on the real execution engine.
- [`Inngest.OpenTelemetry`](https://www.nuget.org/packages/Inngest.OpenTelemetry/): traces and metrics for function
  calls and steps.
- [`Inngest.Encryption`](https://www.nuget.org/packages/Inngest.Encryption/): end-to-end encryption of event data, step
  results and function output, in the format of the TypeScript SDK's encryption middleware.

Install `Inngest.AspNetCore` for a web app, or `Inngest.Connect` for a worker. Both bring in `Inngest`:

```bash
dotnet add package Inngest.AspNetCore
```

```bash
dotnet add package Inngest.Connect
```

## A durable function

Attributes give the function its id and the event that starts it. Each call to a step tool is a step. Inngest saves
its result, so a retry runs only the step that failed.

```csharp
using Inngest;

public sealed record OrderPaid(string OrderId, string CustomerId, decimal Amount);

[InngestFunction("checkout")]
[EventTrigger("shop/order.paid")]
public sealed class CheckoutFunction(IPayments pay, IMailer mail, IWarehouse warehouse)
    : InngestFunction<OrderPaid>
{
    public override async Task<object?> ExecuteAsync(
        FunctionContext<OrderPaid> context, CancellationToken cancellationToken)
    {
        var order = context.Event.Data;

        var charge = await context.Step.RunAsync("charge",
            ct => pay.ChargeAsync(order, ct));

        await context.Step.RunAsync("send-receipt",
            ct => mail.SendReceiptAsync(order, ct));

        await context.Step.SleepAsync("cooling-off",
            TimeSpan.FromMinutes(30));

        await context.Step.RunAsync("ship",
            ct => warehouse.ShipAsync(order, ct));

        return new { charge.Id };
    }
}
```

Register it and map the endpoint Inngest calls:

```csharp
builder.Services
    .AddInngest(options => options.AppId = "shop")
    .AddFunction<CheckoutFunction>();

var app = builder.Build();
app.MapInngest("/api/inngest");
app.Run();
```

Start the app with `INNGEST_DEV=1`, run the [Inngest dev server](https://www.inngest.com/docs/local-development),
send an event with `IInngestClient`, and watch the run at http://localhost:8288.

## Features

- Steps that run once, sleeps, waits for events, parallel steps with `Task.WhenAll`, and invoking other functions.
- Triggers from events, filtered events and cron schedules.
- Flow control as attributes or options: concurrency, throttling, rate limits, debounce, batching, priority, idempotency,
  singletons, cancellation and timeouts.
- Retries per step, `NonRetriableException`, `RetryAfterException` and failure handlers.
- Middleware with observer and wrap hooks, resolved from DI.
- Signed requests in cloud mode, with a fallback key for key rotation.
- Serve from ASP.NET Core with `MapInngest`, or from a worker with `AddConnect`.

## Documentation

- [Documentation](https://damikun.github.io/inngest-dotnet/)
- [Getting started](https://damikun.github.io/inngest-dotnet/guides/getting-started/)
- [How it works](https://damikun.github.io/inngest-dotnet/how-it-works/)
- [Examples](https://damikun.github.io/inngest-dotnet/examples/order-fulfillment/)
- [Source code and samples](https://github.com/damikun/inngest-dotnet)

## License

Apache License 2.0. inngest-dotnet is a C# port of inngest-js, Copyright 2022 Inngest Inc., also licensed under the
Apache License 2.0; the package includes the NOTICE file. This project is not affiliated with or endorsed by Inngest
Inc. "Inngest" is a trademark of Inngest Inc.
