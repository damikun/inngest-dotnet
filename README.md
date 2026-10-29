<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset=".github/assets/banner-dark.svg">
  <img alt="inngest-dotnet: durable functions for .NET" src=".github/assets/banner-light.svg">
</picture>

Write background jobs and workflows as plain C# methods. Inngest runs every step once, retries only what fails and
lets a run wait for days, so you ship reliable workflows without queues, state tables or cron glue.

[![CI](https://github.com/damikun/inngest-dotnet/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/damikun/inngest-dotnet/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/Inngest)](https://www.nuget.org/packages/Inngest)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-f65c4f)](LICENSE)

[Documentation](https://damikun.github.io/inngest-dotnet/) ·
[Getting started](https://damikun.github.io/inngest-dotnet/guides/getting-started/) ·
[How it works](https://damikun.github.io/inngest-dotnet/how-it-works/) ·
[Examples](https://damikun.github.io/inngest-dotnet/examples/order-fulfillment/)

</div>

inngest-dotnet is a community .NET SDK for [Inngest](https://www.inngest.com). It is a C# port of the Inngest
TypeScript SDK, [inngest-js](https://github.com/inngest/inngest-js), and it follows .NET conventions: dependency
injection, `IOptions`, `ILogger`, `CancellationToken`, `TimeSpan` and plain `Task`-based code. Serve your functions
from ASP.NET Core, or from a worker that needs no public endpoint.

<p align="center">
<picture>
  <source media="(prefers-color-scheme: dark)" srcset=".github/assets/how-a-run-works-dark.svg">
  <img alt="CheckoutFunction as Inngest runs it, step by step. The charge runs and completes. Send-receipt fails, and Inngest retries that step alone while the charge stays completed; the retry succeeds. The run then sleeps for 30 minutes, and ship runs." src=".github/assets/how-a-run-works-light.svg">
</picture>
</p>

Inngest runs each step once and keeps its result. When `send-receipt` fails, Inngest retries that step alone, so the
card is not charged again, and a sleep holds no thread or worker while it waits.

## Ordinary C#, durable by default

A function is a class. Each call to a step tool, such as `RunAsync` or `SleepAsync`, is a step. The rest is plain C#.

```csharp
using Inngest;

public sealed record OrderPaid(string OrderId, string CustomerId, decimal Amount);

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

- **`charge` runs once.** Inngest stores the charge it returns. Later calls get that stored result instead of
  charging again.
- **A failure retries one step.** If the mail server fails, Inngest retries `send-receipt` alone, and the charge
  stays done.
- **The sleep holds nothing.** `SleepAsync` ends the call, and Inngest calls the function again in 30 minutes.
  Nothing runs or waits in your app meanwhile, and deploys in between do not matter.
- **`return` completes the run.** The SDK reports the output and Inngest marks the run completed.

## Why durable execution

Checkout flows, onboarding emails, billing runs and webhook handlers call systems that fail, and some have to wait
for days. With durable steps, a failure costs one retry of one step, not a second charge.

- **A retry repeats only what failed.** Inngest saves each step's result. When an API returns a `500` or the process
  crashes, the retry runs the step that failed, not the charge and the email before it.
- **Runs can wait for days.** A run can sleep until a date or wait for an event, such as a payment or a reply,
  without a worker, a timer or a table of your own. The app can deploy, restart or scale down in the meantime.
- **No queue plumbing.** Concurrency limits, throttling, rate limits, debounce, batching and cancellation are options
  on the function. Inngest enforces them and keeps the history of every run.

## Features

| Feature | What you write |
|---|---|
| **Steps that run once** | `context.Step.RunAsync(id, handler)` saves the result, and a retry resumes after the last step that finished. |
| **Sleep and wait for events** | `SleepAsync`, `SleepUntilAsync` and `WaitForEventAsync<T>` pause a run without holding a thread, a request or a worker. |
| **Parallel steps** | Start steps together and `await Task.WhenAll(...)`. Inngest runs each in its own call. |
| **Functions that call functions** | `InvokeAsync<T>` runs another function and returns its result. `SendEventAsync` sends events exactly once. |
| **Triggers** | Events, events filtered with an expression, and cron schedules with a time zone. |
| **Flow control** | Concurrency with keys, throttling, rate limits, debounce, batching, priority, idempotency and singletons, as options in `FunctionOptions`. |
| **Errors and retries** | Every step retries on its own. `NonRetriableException` stops, `RetryAfterException` sets the delay, and a failure handler runs when a function fails for good. |
| **Cancellation and timeouts** | `CancelOn` stops a run when a matching event arrives. `Timeouts` cancel a run that waits too long to start or takes too long to finish. |
| **Middleware** | `InngestMiddleware` observes or wraps runs, steps and event sends, with dependencies from DI. |
| **Two ways to serve** | `MapInngest` adds the endpoint Inngest calls to an ASP.NET Core app. `AddConnect` runs a worker over an outbound WebSocket instead. |
| **Signed requests** | In cloud mode every request from Inngest is verified and every response is signed, with a fallback key for key rotation. |
| **.NET conventions** | Functions are classes resolved from DI, or delegates. Settings come from `IOptions` and the usual `INNGEST_*` variables. Logs go to `ILogger`. |

Waiting for an event is one call. It returns `null` when the timeout passes first:

```csharp
var activated = await context.Step.WaitForEventAsync<UserActivated>(
    "wait-for-activation",
    new WaitForEventOptions("user/activated", TimeSpan.FromDays(3)) { Match = "data.userId" });

if (activated is null)
{
    await context.Step.RunAsync("send-reminder",
        ct => email.SendAsync(user.Email, "Your account is waiting", ct));
}
```

Flow control is a set of options on the function. Inngest enforces them:

```csharp
new FunctionOptions
{
    Id = "sync-account",
    Triggers = [Trigger.Event("account/updated")],
    Concurrency = [new ConcurrencyLimit(1) { Key = "event.data.accountId" }],
    Throttle = new Throttle(10, TimeSpan.FromMinutes(1)),
    CancelOn = [new CancelOn("account/deleted") { Match = "data.accountId" }],
}
```

## How it works

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="site/src/assets/architecture-dark.svg">
  <img alt="One run, animated. An event reaches Inngest, which calls your ASP.NET Core app at /api/inngest once per step. The charge step answers 206 with its result. Send-receipt fails, and Inngest retries it after a backoff. The last call answers 200 with the output. Inngest can also reach a Connect worker over a WebSocket the worker opened." src="site/src/assets/architecture-light.svg">
</picture>

Inngest stores the run and calls your app for each step. Your app keeps no state between calls. The SDK replays the
function and tells Inngest what to do next.

1. **An event starts a run.** Your code, a webhook or a schedule sends an event such as `shop/order.paid`, and
   Inngest starts a run of every function it triggers.
2. **Each call replays the function.** Inngest calls `/api/inngest` with the results of the steps that already
   finished. The SDK hands them back at once, so the function picks up where it stopped.
3. **The answer says what is next.** The SDK answers `206` with the step it ran or reached, such as a sleep, or `200`
   with the output once the function returns.

With Connect, the same functions run in a worker that opens a WebSocket to Inngest. Executions arrive over the
socket and results go back over it, so the worker needs no public endpoint and can run behind a firewall.

[Step through a real run, call by call](https://damikun.github.io/inngest-dotnet/how-it-works/)

## Packages

| Package | What it gives you |
|---|---|
| [`Inngest`](https://www.nuget.org/packages/Inngest/) | Functions, steps, triggers, flow control, middleware, the client that sends events, and request signing. Its only dependencies are `Microsoft.Extensions.*` packages. |
| [`Inngest.AspNetCore`](https://www.nuget.org/packages/Inngest.AspNetCore/) | `MapInngest`, the endpoint Inngest calls in an ASP.NET Core app. |
| [`Inngest.Connect`](https://www.nuget.org/packages/Inngest.Connect/) | `AddConnect`, a hosted worker that receives calls over an outbound WebSocket, with no endpoint to expose. |
| [`Inngest.Testing`](https://www.nuget.org/packages/Inngest.Testing/) | An in-memory test host that runs your functions on the real execution engine. |
| [`Inngest.OpenTelemetry`](https://www.nuget.org/packages/Inngest.OpenTelemetry/) | Traces and metrics for function calls and steps. |

Each package targets `net8.0` and `net10.0`.

## Quick start

1. **Install the SDK.** `Inngest.AspNetCore` brings in `Inngest`. The version is a preview, so add `--prerelease`:

   ```bash
   dotnet add package Inngest.AspNetCore --prerelease
   ```

   Or reference it in your project file:

   ```xml
   <ItemGroup>
     <PackageReference Include="Inngest.AspNetCore" Version="0.1.0-preview.1" />
   </ItemGroup>
   ```

   For a worker with no public endpoint, install `Inngest.Connect` instead.

2. **Register the function and map the endpoint.** `AddInngest` registers the SDK and your functions, and
   `MapInngest` maps the endpoint Inngest calls:

   ```csharp
   using Inngest;

   var builder = WebApplication.CreateBuilder(args);

   // Register IPayments, IMailer and IWarehouse as usual. CheckoutFunction gets them from DI.
   builder.Services
       .AddInngest(options => options.AppId = "shop")
       .AddFunction<CheckoutFunction>(new FunctionOptions
       {
           Id = "checkout",
           Triggers = [Trigger.Event("shop/order.paid")],
       });

   var app = builder.Build();
   app.MapInngest("/api/inngest");
   app.Run();
   ```

3. **Start the app in dev mode.** `INNGEST_DEV=1` points the SDK at the local dev server:

   ```bash
   INNGEST_DEV=1 dotnet run --urls http://localhost:5280
   ```

4. **Start the [Inngest dev server](https://www.inngest.com/docs/local-development)** and point it at the app, with
   Node.js:

   ```bash
   npx --ignore-scripts=false inngest-cli@latest dev -u http://localhost:5280/api/inngest
   ```

   Or with Docker:

   ```bash
   docker run --rm -p 8288:8288 -p 8289:8289 inngest/inngest:v1.49.0 inngest dev -u http://host.docker.internal:5280/api/inngest
   ```

   On Linux without Docker Desktop, add `--add-host=host.docker.internal:host-gateway`.

5. **Send an event** with `IInngestClient`, from an endpoint, a controller or a background service:

   ```csharp
   await inngest.SendAsync(
       new InngestEvent("shop/order.paid", new OrderPaid("o_123", "c_42", 59.90m)));
   ```

   Open http://localhost:8288 and watch the run go step by step.

[Getting started](https://damikun.github.io/inngest-dotnet/guides/getting-started/) walks through a complete app,
with the other ways to install the dev server.

## Samples

| Sample | What it shows |
|---|---|
| [`samples/Inngest.Sample`](samples/Inngest.Sample) | An ASP.NET Core app with an onboarding flow that waits for the user to activate and stops when the account is deleted, a cron job that fans out with `InvokeAsync`, and a delegate function. It comes with a Dockerfile and a Compose file with dev and cloud profiles. |
| [`samples/Inngest.AotSample`](samples/Inngest.AotSample) | An app published as a Native AOT binary, with no trimming or AOT warnings. |

The documentation has complete examples:

| Example | What it shows |
|---|---|
| [Order fulfillment](https://damikun.github.io/inngest-dotnet/examples/order-fulfillment/) | Reserve stock, charge the card, ship and confirm, release the stock when payment fails, and cancel on request. |
| [User onboarding](https://damikun.github.io/inngest-dotnet/examples/user-onboarding/) | Wait up to three days for a user to activate, and send a reminder if they do not. |
| [Nightly billing](https://damikun.github.io/inngest-dotnet/examples/nightly-billing/) | A cron function that fans out one event per account, billed with its own retries and limits. |
| [Webhook pipeline](https://damikun.github.io/inngest-dotnet/examples/webhook-pipeline/) | Accept webhooks quickly, then process each delivery with idempotency and a per-merchant throttle. |
| [Fan-out with invoke](https://damikun.github.io/inngest-dotnet/examples/fan-out-with-invoke/) | Invoke one function per region in parallel, and finish the report even when a region fails. |
| [Scheduled reminders](https://damikun.github.io/inngest-dotnet/examples/scheduled-reminders/) | Sleep until a reminder is due, and cancel or replace the pending run when the reminder changes. |
| [Tracing middleware](https://damikun.github.io/inngest-dotnet/examples/tracing-middleware/) | Wrap every call and step in an `Activity` with tags for the function, the run and the step. |
| [Connect worker](https://damikun.github.io/inngest-dotnet/examples/connect-worker/) | A worker with no HTTP endpoint, a web app that only sends events, a Dockerfile and graceful shutdown. |

## Compatibility

| | |
|---|---|
| .NET | .NET 8 and .NET 10 (`net8.0`, `net10.0`) |
| Hosting | ASP.NET Core with `MapInngest`, or any generic host with `AddConnect` |
| Native AOT and trimming | Compatible. The SDK uses source-generated JSON for its own types; you add a `JsonSerializerContext` for yours. See [Native AOT and trimming](https://damikun.github.io/inngest-dotnet/guides/native-aot/). |
| Inngest | Inngest Cloud, [self-hosted Inngest](https://www.inngest.com/docs/platform-and-operations/self-host-inngest), or the dev server. Tested against `inngest/inngest` v1.49.0. |
| Ported from | inngest-js v4.26.1 |

## Quality and testing

- **1,596 unit tests** pass on .NET 10 and on .NET 8, including concurrency stress tests with thousands of
  invocations at once.
- **196 integration and end-to-end tests** run against the real Inngest server, `inngest/inngest` v1.49.0, in three
  setups: dev mode, a self-hosted server with signing keys (cloud mode), and over Connect.
- **8,000 of 8,000 runs** completed under sustained load in dev mode, and again in cloud mode. Memory stayed flat
  over 8 million engine invocations and 1.6 million signed HTTP calls.
- **Native AOT**: the AOT sample publishes with zero warnings.
- **Strict builds**: warnings are errors, code style is enforced in the build, and the packages include Source Link
  and symbol packages.

The [readiness report](https://damikun.github.io/inngest-dotnet/internals/readiness/) has the details and the raw
data.

## Documentation

- **Start:** [Getting started](https://damikun.github.io/inngest-dotnet/guides/getting-started/) ·
  [How it works](https://damikun.github.io/inngest-dotnet/how-it-works/)
- **Guides:** [Functions and triggers](https://damikun.github.io/inngest-dotnet/guides/functions-and-triggers/) ·
  [Steps](https://damikun.github.io/inngest-dotnet/guides/steps/) ·
  [Sleep and wait for events](https://damikun.github.io/inngest-dotnet/guides/sleep-and-wait-for-events/) ·
  [Parallel steps](https://damikun.github.io/inngest-dotnet/guides/parallel-steps/) ·
  [Invoking functions](https://damikun.github.io/inngest-dotnet/guides/invoking-functions/) ·
  [Sending events](https://damikun.github.io/inngest-dotnet/guides/sending-events/) ·
  [Flow control](https://damikun.github.io/inngest-dotnet/guides/flow-control/) ·
  [Errors and retries](https://damikun.github.io/inngest-dotnet/guides/errors-and-retries/) ·
  [Middleware](https://damikun.github.io/inngest-dotnet/guides/middleware/) ·
  [Connect](https://damikun.github.io/inngest-dotnet/guides/connect/) ·
  [Configuration](https://damikun.github.io/inngest-dotnet/guides/configuration/) ·
  [Deploying](https://damikun.github.io/inngest-dotnet/guides/deploying/) ·
  [Native AOT](https://damikun.github.io/inngest-dotnet/guides/native-aot/) ·
  [Testing](https://damikun.github.io/inngest-dotnet/guides/testing/)
- **Try it in the browser:** [Function options builder](https://damikun.github.io/inngest-dotnet/explainers/function-options/) ·
  [Flow control timeline](https://damikun.github.io/inngest-dotnet/explainers/flow-control/)
- **Reference:** [API overview](https://damikun.github.io/inngest-dotnet/reference/api/) ·
  [Design and wire protocol](https://damikun.github.io/inngest-dotnet/internals/design/) ·
  [Benchmarks](https://damikun.github.io/inngest-dotnet/internals/benchmarks/)
- **Inngest itself:** the [Inngest documentation](https://www.inngest.com/docs) covers the platform, the dashboard
  and self-hosting.

## Contributing

Issues and pull requests are welcome. Build the solution and run the unit tests:

```bash
dotnet build Inngest.slnx
```

```bash
dotnet test --project tests/Inngest.Tests/Inngest.Tests.csproj
```

The integration tests start the Inngest server with Testcontainers, so they need Docker:

```bash
dotnet test --project tests/Inngest.IntegrationTests/Inngest.IntegrationTests.csproj
```

The documentation site lives in [`site/`](site) and is built with Astro and Starlight. The
[design notes](https://damikun.github.io/inngest-dotnet/internals/design/) describe the wire protocol and the
internals.

## License

Apache License 2.0. See [LICENSE](LICENSE).

inngest-dotnet is a C# port of [inngest-js](https://github.com/inngest/inngest-js), Copyright 2022 Inngest Inc., also
licensed under the Apache License 2.0. See [NOTICE](NOTICE). This project is not affiliated with or endorsed by
Inngest Inc. "Inngest" is a trademark of Inngest Inc.
