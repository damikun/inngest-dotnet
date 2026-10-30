# inngest-dotnet

Durable functions for .NET. Write background jobs and workflows as plain C# methods. Inngest runs every step once,
retries only what fails and lets a run wait for days, so you ship reliable workflows without queues, state tables or
cron glue.

inngest-dotnet is a community .NET SDK for [Inngest](https://www.inngest.com), ported from the Inngest TypeScript SDK,
[inngest-js](https://github.com/inngest/inngest-js). It targets .NET 8 and .NET 10 and is Native AOT compatible, except
`Inngest.EntityFrameworkCore`, which runs on Entity Framework Core, and the packages for tests and Aspire app hosts,
which are never compiled ahead of time.

## Packages

- [`Inngest`](https://www.nuget.org/packages/Inngest/): functions, steps, triggers, flow control, middleware, the
  client that sends events, and request signing. A source generator that registers your function classes, which a
  project turns on, and an analyzer that finds the types a Native AOT app has not listed, come with it.
- [`Inngest.AspNetCore`](https://www.nuget.org/packages/Inngest.AspNetCore/): `MapInngest`, the endpoint Inngest calls
  in an ASP.NET Core app, and a health check for readiness probes.
- [`Inngest.AwsLambda`](https://www.nuget.org/packages/Inngest.AwsLambda/): `InngestLambdaHandler`, which serves the
  endpoint from an AWS Lambda function behind API Gateway or a function URL.
- [`Inngest.AzureFunctions`](https://www.nuget.org/packages/Inngest.AzureFunctions/): `ServeInngestAsync`, the endpoint
  Inngest calls, served from an HTTP-triggered function of an Azure Functions app in the isolated worker model.
- [`Inngest.Connect`](https://www.nuget.org/packages/Inngest.Connect/): `AddConnect`, a hosted worker that receives
  calls over an outbound WebSocket, with no endpoint to expose.
- [`Inngest.Aspire.Hosting`](https://www.nuget.org/packages/Inngest.Aspire.Hosting/): an Aspire hosting integration
  that runs the Inngest dev server, or a self-hosted server, as a container of the AppHost, links its UI in the
  dashboard and gives projects the settings the SDK reads.
- [`Inngest.Testing`](https://www.nuget.org/packages/Inngest.Testing/): an in-memory test host that runs your functions
  on the real execution engine.
- [`Inngest.Testing.Containers`](https://www.nuget.org/packages/Inngest.Testing.Containers/): a Testcontainers module
  that runs the Inngest dev server or a self-hosted server in Docker for end-to-end tests.
- [`Inngest.Testing.Containers.AspNetCore`](https://www.nuget.org/packages/Inngest.Testing.Containers.AspNetCore/): a
  `WebApplicationFactory` that runs an ASP.NET Core app on a real port against that server (.NET 10).
- [`Inngest.OpenTelemetry`](https://www.nuget.org/packages/Inngest.OpenTelemetry/): traces and metrics for function
  calls and steps.
- [`Inngest.Encryption`](https://www.nuget.org/packages/Inngest.Encryption/): end-to-end encryption of event data, step
  results and function output, in the format of the TypeScript SDK's encryption middleware.
- [`Inngest.Sentry`](https://www.nuget.org/packages/Inngest.Sentry/): reports the errors of functions and steps to
  Sentry, tagged with the app, the function, the run and the event.
- [`Inngest.Extensions.AI`](https://www.nuget.org/packages/Inngest.Extensions.AI/): makes the model calls and tool
  calls of a Microsoft.Extensions.AI `IChatClient` steps of the run, so a replay never calls the model or a tool twice.
- [`Inngest.EntityFrameworkCore`](https://www.nuget.org/packages/Inngest.EntityFrameworkCore/): a transactional outbox
  for Entity Framework Core: events saved in the same transaction as your data and sent to Inngest afterwards, under
  the same id on every attempt.
- [`Inngest.Templates`](https://www.nuget.org/packages/Inngest.Templates/): `dotnet new inngest-web` and
  `dotnet new inngest-worker`, projects with a typed event, a function and dev mode in their Development settings.
- [`dotnet-inngest`](https://www.nuget.org/packages/dotnet-inngest/): a .NET tool; `dotnet inngest dev` downloads the
  Inngest CLI, checks its SHA-256 and runs the dev server for the app in the current folder.
- [`Inngest.SignalR`](https://www.nuget.org/packages/Inngest.SignalR/): forwards Inngest realtime messages to the
  clients of a SignalR hub, with one subscription to Inngest shared by the clients of a channel.

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
- Durable endpoints: ASP.NET Core endpoints whose handlers run steps and answer the caller directly, with Inngest taking
  over when a step has to wait or fails. Beta in Inngest, so the API is marked experimental.

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
