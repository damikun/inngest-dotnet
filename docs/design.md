# Design

This document describes how the .NET SDK is put together and the wire protocol it speaks. It is written for
people working on the SDK; the README covers usage.

The SDK is a port of the official TypeScript SDK, [inngest-js](https://github.com/inngest/inngest-js) (v4.26.1
at the time of the port, v4.26.2 for the features added after it), checked against the server's
[SDK spec](https://github.com/inngest/inngest/blob/main/docs/SDK_SPEC.md) and the server's Go code. Where the
spec and the TypeScript SDK disagree, the SDK does what the server actually relies on. Those cases are listed in
[Spec deviations](#spec-deviations).

## Protocol

Inngest talks to an app through one HTTP endpoint (by default `/api/inngest`). The method decides the action:

| Method | Caller | Purpose |
|---|---|---|
| `GET` | Inngest, dev server discovery, anyone | Introspection and health check |
| `PUT` | Inngest, the dev server, a deploy script | Sync: register the app's functions |
| `POST` | Inngest executor | Call: run a function or one of its steps |

Every response to a verified request (and every response in dev mode) carries
`X-Inngest-SDK: inngest-dotnet:v<version>`. The server treats a 2xx without it as "not an SDK" and retries, which
matters behind proxies. Answers to unverified requests in cloud mode leave out the SDK headers on purpose, so they
tell an anonymous caller nothing.

### Sync (PUT)

The `X-Inngest-Sync-Kind` request header picks the kind of sync.

**Out-of-band** (header absent or `out_of_band`): the SDK posts its configuration to
`POST {api}/fn/register?deployId=<from the PUT query>` with `Authorization: Bearer <hashed signing key>` and
`X-Inngest-Expected-Server-Kind` copied from the PUT's `X-Inngest-Server-Kind`. It then answers the PUT with
`200 {"message":"Successfully registered","modified":<from Inngest>}` or `500 {"message":"<error>","modified":false}`,
plus `X-Inngest-Sync-Kind: out_of_band`. Out-of-band PUTs are accepted unsigned, as in the TypeScript SDK, because
the SDK only calls back the configured Inngest.

**In-band** (header `in_band`): the PUT body is `{"url":"<serve url>"}` and must be signed in cloud mode. The SDK
answers `200` with `X-Inngest-Sync-Kind: in_band` and the configuration in the body, so Inngest does not need to
reach the app twice. `InngestOptions.AllowInBandSync = false` forces out-of-band syncs, and
`AllowUnsignedSync = false` (`INNGEST_ENABLE_UNAUTHED_SYNC=false`) makes cloud mode refuse unsigned ones.

The configuration (the register body, and the `functions` of an in-band reply):

```json
{
  "url": "https://example.com/api/inngest",
  "deployType": "ping",
  "appName": "shop",
  "appVersion": "1.4.0",
  "sdk": "dotnet:v0.1.0",
  "v": "0.1",
  "framework": "aspnetcore",
  "capabilities": { "trust_probe": "v1" },
  "functions": [{
    "id": "shop-send-welcome-email",
    "name": "Send welcome email",
    "triggers": [
      { "event": "user/signed.up", "expression": "event.data.plan == 'pro'" },
      { "cron": "TZ=Europe/Prague 0 9 * * 1-5" }
    ],
    "steps": { "step": {
      "id": "step", "name": "step",
      "runtime": { "type": "http", "url": "https://example.com/api/inngest?fnId=shop-send-welcome-email&stepId=step" },
      "retries": { "attempts": 3 }
    }},
    "concurrency": [{ "limit": 10, "key": "event.data.accountId", "scope": "fn" }],
    "throttle": { "limit": 5, "period": "1m", "burst": 2, "key": "event.data.accountId" },
    "rateLimit": { "limit": 1, "period": "4h", "key": "event.data.accountId" },
    "idempotency": "event.data.orderId",
    "debounce": { "period": "5s", "key": "event.data.accountId", "timeout": "1m" },
    "batchEvents": { "maxSize": 50, "timeout": "5s", "key": "event.data.tenant" },
    "priority": { "run": "event.data.vip ? 120 : 0" },
    "cancel": [{ "event": "user/deleted", "if": "event.data.userId == async.data.userId", "timeout": "1d" }],
    "timeouts": { "start": "10m", "finish": "1h" },
    "singleton": { "mode": "skip", "key": "event.data.userId" }
  }]
}
```

Notes on the fields:

- A function's id on the wire is `<app id>-<function id>`, not slugified. `name` is required by the server and
  defaults to the function id.
- `retries.attempts` holds the number of retries, not attempts. When omitted the server uses 4 retries (5
  attempts); the maximum is 20.
- Durations are time strings (`"1h30m"`), formatted from `TimeSpan` with whole-second resolution. Batch timeouts
  are parsed with Go's `time.ParseDuration`, so they never use `d` or `w` units.
- `idempotency` is turned into a 24h rate limit by the server and replaces any `rateLimit`.
- A function with a failure handler registers a second function `<id>-failure`, triggered by
  `inngest/function.failed` with `event.data.function_id == '<app id>-<id>'` and one retry.

### Introspection (GET)

Returns `schema_version: "2024-05-24"`. In cloud mode with a valid signature the body is the authenticated form
(app id, origins, hashed keys, function count, mode, SDK language and version); in cloud mode without one the SDK
answers `401`, as the TypeScript SDK does. Dev mode verifies nothing, so it always gets the unauthenticated form
(function count, mode, which keys are set).

### Call (POST)

Query parameters: `fnId` is the composite function id; `stepId` is `step` for a normal call or the hashed id of a
planned step to run. The body (keys arrive sorted, because Inngest canonicalizes it before signing):

```json
{
  "event": { "name": "user/signed.up", "data": { "userId": 1 }, "user": {}, "id": "01J...", "ts": 1705586504000 },
  "events": [ "...all events of a batch, or just event..." ],
  "steps": {
    "e7d8a2f140845095749d60246ff1110c9d01d76a": { "data": "A" },
    "2c4ac3...": { "error": { "name": "Error", "message": "boom", "stack": "..." } },
    "8f1a2b...": null,
    "93bd77...": { "name": "order/paid", "data": {}, "ts": 1705586505000 },
    "51c0de...": { "input": [] }
  },
  "ctx": {
    "run_id": "01J...", "attempt": 0, "max_attempts": 5,
    "stack": { "stack": ["e7d8a2f1...", "2c4ac3..."], "current": 2 },
    "disable_immediate_execution": false, "use_api": false, "fn_id": "<uuid>"
  },
  "version": 2
}
```

A memoized step is read as: an object with only `data` is a result, only `error` is a final failure, only `input`
is not a result (input of a step still in progress), and anything else, including `null` and a raw event, is the
result itself. Sleeps memoize `null`; `waitForEvent` memoizes the matched event or `null` on timeout.

When `ctx.use_api` is true the server trimmed a large payload, and the SDK fetches the events from
`GET {api}/v0/runs/{run id}/batch` and the steps from `GET {api}/v0/runs/{run id}/actions`, authenticated with the
hashed signing key. Any failure there answers `500`.

### Responses to calls

| Status | Meaning | Headers |
|---|---|---|
| `200` | The function returned; the body is its output as JSON | |
| `206` | The body is an array of step operations | `X-Inngest-No-Retry` when a step errored |
| `500` | Retriable function error; body `{"name","message","stack"}` | `X-Inngest-No-Retry: false`, optional `Retry-After` |
| `400` | Final function error (non-retriable, uncaught step failure, or last attempt) | `X-Inngest-No-Retry: true` |
| `401` | Signature missing or invalid in cloud mode | |

Every call response also carries `X-Inngest-Req-Version: 2`. In cloud mode it is signed with
`X-Inngest-Signature` using the key that verified the request.

Step operations (`id` is the hashed step id):

| `op` | Sent when | Shape |
|---|---|---|
| `StepPlanned` | A `run` or `sendEvent` step is found but not executed | `{id, op, name, displayName, opts: {type}}` |
| `StepRun` | A step ran and succeeded | `{id, op, name, displayName, data, timing}` |
| `StepError` | A step threw and has attempts left | `{id, op, name, displayName, error}` |
| `StepFailed` | A step threw on its last attempt or threw `NonRetriableException` | `{id, op, name, displayName, error}` |
| `Sleep` | `SleepAsync`, `SleepUntilAsync` | `{id, op, name: "<duration or RFC 3339 time>", opts: {duration}}` |
| `WaitForEvent` | `WaitForEventAsync` | `{id, op, name: "<event>", opts: {event, timeout, if}}` |
| `InvokeFunction` | `InvokeAsync` | `{id, op, opts: {function_id, payload: {data, user}, timeout}}` |

Step ids are the lowercase hex SHA-1 of the id the code passed. A repeated id gets `:1`, `:2`, ... appended before
hashing, in call order (`my-step` = `e7d8a2f1...` is the spec's example for `my-step-id`).

### Deciding what to answer

For `stepId=step`, the SDK replays the function, feeding it memoized results, until the function returns, throws,
or waits on steps that are not memoized. Then:

- If exactly one new step was found, it is a `run` step and `ctx.disable_immediate_execution` is false, the SDK
  runs it right away and answers `206 [StepRun]` (or `StepError`/`StepFailed`).
- Otherwise it answers `206` with all new steps. The server runs each `StepPlanned` step in its own call with
  `stepId=<hash>` and sets `disable_immediate_execution` for the rest of the parallel section.
- If the function returns while new steps are pending (fired but not awaited), the steps are reported, not the
  return value.

For `stepId=<hash>`, the SDK replays until it reaches that step, runs it and answers with one operation. If the step
does not show up within 10 seconds, it answers `500` with `X-Inngest-No-Retry: false` (the server has no
`StepNotFound` op).

Memoized results are handed back in the order recorded in `ctx.stack.stack`, so races between parallel steps
replay the way they first resolved. If the code asks for steps in an order the stack cannot satisfy, the SDK logs
that the function changed between calls and falls back to the earliest memoized step that was asked for.

### Retries

Every queue item (the first call, each planned step, each discovery call) gets `retries + 1` attempts, counted by
`ctx.attempt` from zero. A function error is final when it is a `NonRetriableException`, an uncaught
`StepFailedException`, or happens on the last attempt; the SDK answers `400` then. A `RetryAfterException` adds
`Retry-After` (seconds, or an RFC 3339 time), which the server clamps to between 1 second and 24 hours.

After the final failure Inngest emits `inngest/function.failed` with `{function_id, run_id, error, event}`, which
is what failure handlers run on.

### Signing

Requests from Inngest carry `X-Inngest-Signature: t=<unix seconds>&s=<hex>`. `s` is HMAC-SHA256 over the raw body
followed by `t`, keyed with the signing key minus its `signkey-<env>-` prefix, used as UTF-8 text (not decoded
from hex). Requests older or newer than 5 minutes are rejected. With a fallback key configured
(`INNGEST_SIGNING_KEY_FALLBACK`), a signature that fails with the current key is checked against the fallback, so
a key can be rotated without downtime.

Calls to the Inngest API authenticate with `Authorization: Bearer signkey-<env>-<sha256 of the hex-decoded key>`
and are retried once with the fallback key on `401` or `403`.

In dev mode nothing is verified or signed.

### Events

`POST {event api}/e/{event key}` with a JSON array of `{name, data, user, id, ts, v}`. The response is
`{"ids": [...], "status": 200}`; the body's `status` decides success. The SDK sends
`X-Inngest-Event-Id-Seed: <unix ms>,<base64 of 10 random bytes>` once per send and keeps it across retries, so a
retried send creates the same event ids. Sends are retried up to 5 attempts with exponential backoff.

### Modes and configuration

| Setting | Environment variable | Default |
|---|---|---|
| `IsDev` | `INNGEST_DEV` (`1`/`true`, or the dev server URL) | cloud mode |
| `EventKey` | `INNGEST_EVENT_KEY` | none; any key works against the dev server |
| `SigningKey`, `SigningKeyFallback` | `INNGEST_SIGNING_KEY`, `INNGEST_SIGNING_KEY_FALLBACK` | required in cloud mode |
| `Environment` | `INNGEST_ENV` | the key's environment |
| `BaseUrl`, `ApiBaseUrl`, `EventApiBaseUrl` | `INNGEST_BASE_URL`, `INNGEST_API_BASE_URL`, `INNGEST_EVENT_API_BASE_URL` | `https://api.inngest.com`, `https://inn.gs`, or `http://localhost:8288` in dev mode |
| `ServeOrigin`, `ServePath` | `INNGEST_SERVE_ORIGIN`, `INNGEST_SERVE_PATH` | the URL the request arrived on |
| `AllowInBandSync` | `INNGEST_ALLOW_IN_BAND_SYNC` | `true` |
| `AllowUnsignedSync` | `INNGEST_ENABLE_UNAUTHED_SYNC` | `true` |

Options set in code win over environment variables. Variables are read through `IConfiguration`, so they can
also come from `appsettings.json` or any other configuration source. `AddInngest(IConfiguration)` reads the options
from a section such as `Inngest` as well, as the lowest layer: code wins over the `INNGEST_*` variables, which win
over the section, which wins over the defaults. The section is read key by key rather than through the configuration
binder, so it works trimmed and under Native AOT.

## .NET architecture

### Packages

| Package | Contents |
|---|---|
| `Inngest` | Client, function definitions, step tools, execution engine, middleware, signing, the framework-agnostic serve handler. Depends only on `Microsoft.Extensions.*` abstractions. It also carries a source generator, analyzers and a code fix (see [Source generator and analyzers](#source-generator-and-analyzers)), which are not packages of their own. |
| `Inngest.AspNetCore` | `MapInngest`, which adapts ASP.NET Core requests to the serve handler, and `AddInngest` on `IHealthChecksBuilder`, a readiness check for apps that serve functions. |
| `Inngest.AwsLambda` | `InngestLambdaHandler`, which adapts the events of API Gateway REST and HTTP APIs and Lambda function URLs to the serve handler, in a managed runtime function or a Native AOT executable. Depends on `Amazon.Lambda.APIGatewayEvents` and `Amazon.Lambda.Core` only. |
| `Inngest.AzureFunctions` | `ServeInngestAsync`, which adapts the request of an HTTP-triggered function of the Azure Functions isolated worker to the serve handler. Depends on `Inngest.AspNetCore` and, of the worker, only `Microsoft.Azure.Functions.Worker.Core`. |
| `Inngest.Connect` | `AddConnect`, a hosted worker that receives calls over an outbound WebSocket instead of an HTTP endpoint. |
| `Inngest.Aspire.Hosting` | `AddInngest` and `AddInngestSelfHosted`, which run the Inngest dev server or a self-hosted server as a container of an Aspire AppHost, `WithApp`, which tells the server where an app that serves functions is, and `WithReference`, which gives a project the `INNGEST_*` settings the SDK reads. Depends on `Aspire.Hosting` only, not on `Inngest`. |
| `Inngest.Testing` | `InngestTestHost`, which runs functions in memory on the real execution engine for unit tests, with mocked steps, retries and replays. |
| `Inngest.OpenTelemetry` | `AddInstrumentation`, a middleware with spans for calls and executed steps and metrics for runs, steps, retries and durations. Depends on `OpenTelemetry.Api` only. |
| `Inngest.Encryption` | `AddEncryption`, a middleware that encrypts event data, step results and function output in the format of `@inngest/middleware-encryption`. XSalsa20, Poly1305 and BLAKE2b come from `BouncyCastle.Cryptography`, which is managed code. |
| `Inngest.Sentry` | `AddSentryReporting`, a middleware that reports the errors of functions and steps to Sentry, with tags for the call and the run's trace. Depends on `Sentry` only. |
| `Inngest.Extensions.AI` | `UseInngestSteps` and `UseInngestFunctionInvocation`, stages for the `ChatClientBuilder` of Microsoft.Extensions.AI that make each model call and each tool call of a run an AI wrap step, and `InngestInferChatClient`, a chat client of inference steps in the OpenAI chat format. Depends on `Microsoft.Extensions.AI`. |
| `Inngest.EntityFrameworkCore` | `AddEntityFrameworkOutbox`, a transactional outbox: events are rows saved with the app's data through `DbContext.AddInngestEvent`, and a hosted dispatcher sends them. Depends on `Microsoft.EntityFrameworkCore.Relational`, version 8 for net8.0 and 10 for net10.0. See [Transactional outbox](#transactional-outbox). |
| `Inngest.Testing.Containers` | A Testcontainers module (`InngestServerBuilder`, `InngestServerContainer`, `InngestServerConfiguration`) that runs the dev server or the self-hosted server (`inngest start`) in Docker for tests, with the settings that point an app at it and a call that has it sync the app. Depends on `Testcontainers` and `Inngest`'s public options only. |
| `Inngest.Testing.Containers.AspNetCore` | `InngestWebApplicationFactory`, a `WebApplicationFactory` that runs the app on Kestrel against the container. Depends on `Microsoft.AspNetCore.Mvc.Testing`, so it is a package of its own and targets net10.0 only. |
| `Inngest.Templates` | The `inngest-web` and `inngest-worker` templates for `dotnet new`. A template package: files, no assembly. |
| `dotnet-inngest` | A .NET tool, `dotnet inngest dev`, that downloads the Inngest CLI, checks it and runs the dev server. Depends on nothing but the framework. |
| `Inngest.SignalR` | `AddInngestSignalR`, `MapInngestRealtimeHub` and `InngestRealtimeBridge<THub>`, which forward realtime messages to SignalR clients through `IHubContext<THub>`: clients join through a hub method guarded by a callback, share one subscription per channel and topics, and get each message on a queue of their own, by connection id and never through a group, so that instances on a backplane deliver once. Reads the realtime subscriber of `Inngest` through `InternalsVisibleTo`, takes `RealtimeSubscription` from `Inngest.AspNetCore`, and uses the SignalR of the ASP.NET Core shared framework. No release includes it yet, so its project file clears the package validation baseline. |
| `Inngest.Blazor` | `RealtimeView`, a headless Blazor component, and `IRealtimeSource`, the realtime messages as an async stream, with a source for the server (`AddInngestRealtimeServerSource`, over `IInngestRealtime`) and one for WebAssembly (`AddInngestRealtimeRelaySource`, which reads a `MapInngestRealtime` endpoint). A Razor class library that references `Microsoft.AspNetCore.Components.Web` as a package, not the shared framework, so it runs in the browser. |
| `Inngest.UserContext` | `AddUserContext`, a middleware that attaches selected claims of the current user to the events an app sends, protected with ASP.NET Core Data Protection, and restores them in runs as `ICurrentUser` and `ClaimsPrincipal`. Depends on `Microsoft.AspNetCore.DataProtection`, which has no framework reference, so workers without ASP.NET Core use it. |
| `Inngest.UserContext.AspNetCore` | `AddUserContextFromHttpContext`, which takes the user of an event sent while serving a request from `HttpContext.User`. Uses the ASP.NET Core shared framework. |
| `Inngest.Webhooks` | Verifiers for Standard Webhooks (Svix, Clerk, Resend), Stripe, GitHub, Shopify and any HMAC, the providers that turn a verified delivery into an event, and the pipeline behind `MapInngestWebhook` (which is in `Inngest.AspNetCore`). Depends on `Inngest` only, and uses `System.Security.Cryptography` for every signature. |

`Inngest.AspNetCore`, `Inngest.AwsLambda`, `Inngest.Blazor`, `Inngest.Connect`, `Inngest.SignalR`, `Inngest.Testing`,
`Inngest.Encryption`, `Inngest.EntityFrameworkCore`, `Inngest.Extensions.AI` and `Inngest.UserContext` use internals of
`Inngest` through `InternalsVisibleTo`: the serve handler, the execution engine, the registry and the resolved settings,
of which `Inngest.Encryption` only reads the serializer options, `Inngest.EntityFrameworkCore` those and the checks the
client makes on an event it sends, `Inngest.Extensions.AI` only asks which invocation the calling code runs in, and so
whether it can make a step, and `Inngest.UserContext` reads the settings, the registry and the run a piece of code
belongs to. `Inngest.Blazor` and `Inngest.SignalR` use the realtime subscriber, and `Inngest.Blazor`
also its message reader and server-sent events parser. Internals can change in any release, so the packages ship
together and each depends on exactly the `Inngest` of its own version (a target in `src/Directory.Build.targets` writes
the exact range). `Inngest.OpenTelemetry` and `Inngest.Sentry` use only the public middleware API, and
`Inngest.Testing.Containers` only `InngestOptions`; `Inngest.Testing.Containers.AspNetCore` reaches one internal helper
of `Inngest.Testing.Containers` through `InternalsVisibleTo`, and depends on exactly its version too.
`Inngest.AzureFunctions` uses the serve handler of `Inngest` and the `InngestEndpoint` of `Inngest.AspNetCore` in the
same way, so it depends on exactly the `Inngest.AspNetCore` of its own version too, as `Inngest.AspNetCore` does on the
`Inngest.Webhooks` whose internal receiver `MapInngestWebhook` uses. Package validation compares every
package with the last release on nuget.org, so `dotnet pack` fails on a change that would break code built against it;
`PackageValidationBaselineVersion` in `src/Directory.Build.props` is raised after each release. `Inngest.Encryption`,
`Inngest.Sentry`, `Inngest.EntityFrameworkCore`, `Inngest.Extensions.AI`, the two `Inngest.Testing.Containers` packages,
`Inngest.Templates`, `dotnet-inngest`, `Inngest.AwsLambda`, `Inngest.AzureFunctions`, `Inngest.Blazor`, `Inngest.SignalR`,
`Inngest.UserContext`, `Inngest.UserContext.AspNetCore` and `Inngest.Webhooks` have no release to compare with yet, so
their project files clear the baseline until their first one.
Every package with an assembly is `IsAotCompatible` except four. The two container packages: Testcontainers and
`Microsoft.AspNetCore.Mvc.Testing` are built on reflection, and the packages are for test projects, which are neither
trimmed nor compiled ahead of time. `Inngest.EntityFrameworkCore`: the constructors of EF Core's `DbContext` require
unreferenced and dynamic code. And `Inngest.Aspire.Hosting`, below.

`Inngest.Aspire.Hosting` is a different kind of package: it runs in the AppHost, not in the app, so it does not
reference `Inngest` and is not pinned to its version. It is not `IsAotCompatible`, because `Aspire.Hosting` declares
neither trimming nor Native AOT support and an AppHost is never published as a native binary. Its project file clears
the baseline too. Aspire reserves the `Aspire.` prefix on nuget.org, hence the name.

### Code layout

- **Namespaces.** Public types are in `Inngest`, whatever folder they live in, so one `using Inngest;` brings in the
  whole API. Internal types are in the namespace of their folder, such as `Inngest.Execution` or
  `Inngest.Registration`. Extension methods on a framework type are in that type's namespace
  (`Microsoft.Extensions.DependencyInjection`, `Microsoft.AspNetCore.Builder`, `OpenTelemetry.Trace`), and extension
  methods on Inngest types are in `Inngest`. `Inngest.Aspire.Hosting` follows Aspire instead: its resource type is in
  `Aspire.Hosting.ApplicationModel` and its extension methods are in `Aspire.Hosting`, the two namespaces an AppHost
  imports.
- **Files.** Every public type has a file named after it; the generic forms of a type share it or use `OfT`, as in
  `InngestEventOfT.cs`. Internal helper types may share the file of the type they serve. A class too large to read in
  one file is split into partial files named after their concern, such as `Invocation.Steps.cs`,
  `Invocation.Execution.cs` and `Invocation.Answers.cs`.
- **Comments.** Public types and members carry a `/// <summary>`, which IDEs show. Everything else has `//` comments
  that say why, not what.
- **Tests.** The folders of `tests/Inngest.Tests` follow the areas of the source (`Steps`, `Functions`, `Middleware`,
  `Registration` and so on), and the integration tests are grouped by the Inngest server fixture they run against.

### Public API

The API follows .NET conventions rather than the shape of the TypeScript SDK:

- `services.AddInngest(options => ...)`, or `services.AddInngest(configuration.GetSection("Inngest"))`, registers
  everything and returns an `IInngestBuilder` for `AddFunction<TFunction>()`, `AddFunction<TFunction>(FunctionOptions)`,
  `AddFunction<TEventData>(FunctionOptions, handler)` and `AddMiddleware<TMiddleware>()`.
  `app.MapInngest("/api/inngest")` serves the endpoint.
- Functions are classes deriving from `InngestFunction<TEventData>`, or `InngestFunction<TEventData, TResult>` for a
  typed result, resolved from a DI scope per invocation, or delegates. `FunctionOptions` and the flow control records
  (`ConcurrencyLimit`, `Throttle`, `RateLimit`, `Debounce`, `BatchEvents`, `Priority`, `CancelOn`,
  `FunctionTimeouts`, `Singleton`) are immutable records with `TimeSpan` durations.
- A function class can carry its options as attributes (`[InngestFunction]`, `[EventTrigger<T>]`, `[CronTrigger]`,
  `[Concurrency]`, `[Throttle]`, `[CancelOn<T>]` and so on), one per `FunctionOptions` member, with durations as
  time strings. All but `[InngestFunction]` derive from `FunctionOptionsAttribute` and map themselves onto the
  options in `Configure`, so users can write their own. `AddFunction<TFunction>()` reads them once at registration, with `GetCustomAttributes` on the type it
  names, which stays trim and AOT safe: it starts from `[InngestFunction]` and applies the others in `Order`, then
  in declaration order. The result goes through the same startup validator. Explicit `FunctionOptions` replace the
  attributes, and `AddFunction<TFunction>(configure)` adjusts them. There is no assembly scanning: the source generator writes
  `AddInngestFunctions()`, which calls `AddFunction<TFunction>()` for each class with `[InngestFunction]`, in a project
  that turns it on. The generated code does not call `Apply` itself, so the attributes are still read when the app starts
  and not at compile time.
- Event data records implement `IInngestEventData`, whose static abstract `EventName` (and static virtual
  `EventVersion`) let `SendAsync<T>`, `Trigger.Event<T>`, `WaitForEventAsync<T>`, `CancelOn.For<T>` and
  `SendEventAsync<T>` take the name from the type without reflection. Every string form stays.
- `step.InvokeAsync<TFunction, TResult>` finds the function's id in the registry by its class; `IReturns<TResult>`,
  which `InngestFunction<TEventData, TResult>` implements, lets the compiler check the result type.
- `FunctionContext<TEventData>` gives the typed event and batch, run id, attempt, step tools, a replay-safe
  `ILogger` and the invocation's `IServiceProvider`.
- Step tools are an interface, `IStepTools`, so user code can be unit tested with a fake: `RunAsync`, `SleepAsync`,
  `SleepUntilAsync`, `WaitForEventAsync`, `InvokeAsync`, `SendEventAsync`. Members added later have default
  implementations, and convenience overloads are extension methods over those six. Parallel steps are plain
  `Task.WhenAll`/`Task.WhenAny`.
- Fan-out over a list is extension methods over those six: `MapAsync`, `ForEachAsync` and `MapOutcomesAsync` run a step per
  item with a window of items in flight, and `MapInChunksAsync` and `MapOutcomesInChunksAsync` send long lists to child
  runs of a `FanOutFunction<TItem, TResult>` class. See [Fan-out](#fan-out).
- `RunScopedAsync` is a run step whose handler runs in a dependency injection scope of its own. The invocation makes
  the scope from its services (a sibling of the invocation's scope, as `IServiceScopeFactory` makes them) when the
  handler starts and disposes it, asynchronously, when the handler ends, so a `DbContext` is new for every attempt and
  step, and a memoized, planned or mocked step makes none. It is a `RunStep<T>` with a wrapped handler, so Inngest,
  middleware (`StepType.Run`) and the test host see an ordinary run step, and middleware keeps seeing the invocation's
  scope in `FunctionContext.Services`. The typed overloads are extension methods that resolve one service with
  `GetRequiredService<T>()`. A scoped service that throws when it is disposed is logged and does not fail the step, so
  disposal must not do work that can fail.
- Errors are exceptions: `NonRetriableException`, `RetryAfterException`, and `StepFailedException` thrown at a
  step's call site when the step failed for good. `InngestException` covers failures talking to Inngest.
- Failure handlers implement `IFailureHandler` on the function class, or pass `onFailure` with a delegate.
- Middleware derives from `InngestMiddleware` and overrides only the hooks it needs: observers (`OnRunStartAsync`,
  `OnMemoizationEndAsync`, `OnRunCompleteAsync`, `OnRunErrorAsync`, `OnStepStartAsync`, `OnStepCompleteAsync`,
  `OnStepErrorAsync`) and wrappers (`WrapFunctionAsync`, `WrapStepAsync`, `WrapStepHandlerAsync`,
  `WrapSendEventAsync`). Instances are resolved per call, so they can hold state for the call, as in v4. A run's calls
  can reach any instance of the app, so nothing in them lasts the run.

### Request path

```
Kestrel -> MapInngest -> InngestEndpoint (pooled body) -> InngestCommHandler
  GET  -> introspection
  PUT  -> FunctionRegistry.WriteRegistration -> in-band body, or IInngestApi.RegisterAsync
  POST -> signature check -> FunctionRegistry.TryGet(fnId) -> ExecutionEngine.ExecuteAsync -> ServeResponse
```

Azure Functions reaches the same handler through a function of the app that calls `request.ServeInngestAsync()`. With
the isolated worker's ASP.NET Core integration, the function runs inside a request of the worker's Kestrel, whose
`HttpContext` is in `FunctionContext.Items`, and `InngestEndpoint.HandleAsync` answers through it, streaming included.
With the built-in HTTP model, the body is a `MemoryStream` that already holds the request and is used in place, and the
response is copied into an `HttpResponseData`, which the worker sends to the host when the function returns, so a call
cannot be streamed.

`InngestCommHandler` knows nothing about ASP.NET Core; it works on `IServeRequest` and returns `ServeResponse`, so
other hosts, and the Connect worker, reuse it. Startup data (resolved settings, the function registry) is immutable and
read without locks. Request and response bodies live in pooled buffers; the body's raw bytes are what the
signature is computed over and what `JsonDocument` parses, without copies.

### Execution engine

Each call replays the function from the top. The interesting part is knowing when the user's code has gone as far
as it can in this call. The TypeScript SDK approximates that by waiting out a fixed number of microtask turns.
The .NET engine runs the user's code on a private `SynchronizationContext` for the invocation
(`InvocationScheduler`), so every continuation of the function is queued there. When that queue is empty and the
function has not finished, the function is waiting on something: a step that will not complete in this call, or
real I/O whose continuation will arrive later. That is a precise signal, not a timeout, and it keeps
`Task.WhenAll` batching and `Task.WhenAny` races working with no special API. One limit: a function that keeps
queueing work without ever waiting (a tight `Task.Yield` loop) is cut off after 100,000 continuations per round so
that a call always ends; memoized results may then be handed out before the function idles.

From there the engine follows the TypeScript SDK:

1. Step tools hash the id and return a task the engine controls.
2. Memoized results are handed out one at a time, in `ctx.stack.stack` order, each followed by running the
   function until it idles again.
3. When nothing memoized is left to hand out, the steps found since the last report decide the answer, as
   described in [Deciding what to answer](#deciding-what-to-answer).
4. Steps that were found but not run are left pending. Their tasks are never faulted or cancelled, so `catch` and
   `finally` blocks in user code do not run for them in this call, matching the TypeScript SDK.

User code that leaves the scheduler (`ConfigureAwait(false)`, `Task.Run`) still works: step calls made from other
threads are posted back to the scheduler, so engine state is only touched by one logical thread and needs no locks.
Such code can only make the SDK report fewer steps at once, never wrong ones. Blocking on a step task (`.Result`)
deadlocks the invocation, as it would on any single-threaded context.

Two rules keep tasks the function starts from hanging, as they would not on the JavaScript event loop:

- While a step handler or a middleware hook runs, the engine keeps running what is queued on the scheduler, without
  deciding anything, so either can await a task the function started before the step.
- Once the call has answered, work posted to the scheduler, and work still queued, runs on the thread pool instead
  of being dropped. A task the function started may outlive the call, such as one a singleton caches, or a
  `SemaphoreSlim` wait whose `finally` releases it. The abandoned function's own steps still never complete.

### Fan-out

`MapAsync` and its siblings are extension methods over `IStepTools`, written against nothing but `RunAsync` and
`InvokeAsync<TFunction, TResult>`, so a fake of the step tools runs them too.

- **Ids.** An item is the step `<id>[<index>]` and a chunk is `<id>[<first>-<last>]`. The engine appends `:<n>` to a
  repeated id, so `<id>:<index>` could be taken by the second use of `<id>` in a run. The ids here end in `]`, which
  that suffix never does, and the index is the run of digits after the last `[`, so two items or an item and a chunk
  never share an id. What can still repeat is an id the code passes twice, and the engine numbers that as ever. A chunk
  has both of its ends in its id so that a run replayed with another chunk size does not hand one chunk the result of
  another; it invokes every chunk again instead.
- **The window.** `FanOutRun` starts one lane per unit in flight, as the workers of `Parallel.ForEachAsync` do: a lane
  starts a unit, waits for it, and takes the next. The engine hands memoized results back in the order the run recorded
  them, one at a time, and each hand-back lets its lane start the next unit, so a replay starts the units in the order
  the run first did, whichever order the results arrived in. Under the default mode Inngest calls the function again
  only when every step of a group has finished, so the window moves on in groups; under race mode after each step, so it
  slides. The engine tells that the function is idle on its scheduler, so the lanes run there: they await with
  `ConfigureAwait(true)` on purpose, since `false` would move their continuations to the thread pool, and the next unit
  would start while the engine decides what to report. A caller that left the scheduler, after `ConfigureAwait(false)`
  or inside `Task.Run`, has `StepTools.RunOnScheduler` queue the start of the lanes there with its execution context, as
  one piece of work, so that the whole first window is found at once and a fan-out behaves as it does called from the
  scheduler. The alternative, lanes that continue on the thread that completes a step (`ContinueWith` with
  `ExecuteSynchronously`), fixes the awaits but leaves the first window to be registered step by step from the pool
  thread, which the engine can interleave with hand-backs: in 60,000 replays of calls off the scheduler it gave one
  window in another order and, once, another first failure, both from `Task.Run`; queuing the start gave none in as
  many. A continuation that is not inlined would also run on the pool. A fake of the step tools has no scheduler, and
  the lanes then start where the caller is. The answer is taken where the run is decided, not when the awaiting code
  resumes.
- **Calls.** A list of `n` items in windows of `w` takes one call per window of two or more items that finds its items,
  one call per item, one call for a last window of one item that runs in the call that finds it, and one that returns:
  seven items in windows of three are 10 calls. In race mode it is `2n + 1`. Every call replays the function and carries
  the results so far, which is why lists beyond a few hundred items go to chunks.
- **Failures.** A fail-fast run starts nothing after the first failure it sees and throws it without waiting for the
  units in flight, since a planned step cannot be recalled. The failure it sees first is the one Inngest recorded first,
  because hand-backs follow the record. A step that failed for good is the only thing that counts as an item's failure;
  anything else a unit throws ends the run with that exception.
- **Chunks.** The function of a chunk is a class, because the code of an item cannot travel to another run. The parent
  invokes it by class with a `FanOutChunk<TItem>` as the event data and reads `FanOutOutcome<TResult>[]` back; the
  function never fails because of an item, it reports it, so the parent can name the item that failed. The outcomes are
  an array because the engine writes a function's output by its runtime type, so a source-generated context has to list
  the type that is written. Sizes follow the limits of Inngest, 32 MiB for a run's state among them, and what the dev
  server enforces: a run fails once 1,000 steps have completed, so the guards refuse 1,000 items, chunks or chunk size
  and more, without retries, and an SDK response is capped at 8 MiB, which carries all the chunks planned in one call and
  the outcomes a chunk's run ends with. See the guide for what was tried.

### Serialization

The SDK's own wire types use System.Text.Json source generation, one `JsonSerializerContext` per area. User types
(event data, step results, function output) go through `InngestOptions.SerializerOptions`, which defaults to
`JsonSerializerDefaults.Web` with reflection when reflection is enabled. Apps that trim or use Native AOT add their
own source-generated context to its `TypeInfoResolverChain`, and an analyzer of the `Inngest` package finds the types
that context lacks. Step results are round-tripped through JSON even on the call that ran them, the same as every later
replay sees them. Packages that save types the app does not own, such as `Inngest.Extensions.AI` with `ChatResponse`,
write them to a `JsonElement` with the options of the library the types come from and hand the SDK only the element, so
the app's options need no contracts for them.

### Source generator and analyzers

`src/Inngest.SourceGeneration` holds an incremental source generator and two analyzers, and
`src/Inngest.SourceGeneration.CodeFixes` the code fix. Neither is a package. `src/Inngest/Inngest.csproj` builds both
without referencing them and packs the two assemblies in `analyzers/dotnet/cs`, with `buildTransitive/Inngest.props`
and `Inngest.targets`. They ship in the package of the SDK because the generated code calls `AddFunction<T>()` of the
same release and every project that defines a function references `Inngest`, which is where System.Text.Json and
`Microsoft.Extensions.Logging` put their generators too, next to the attributes they read. `martinothamar/Mediator`
ships its generator as a package of its own, because only the app that hosts the mediator needs it and libraries use
the abstractions alone; that is not the shape here, where a library with functions needs the generator as much as the
app does. `buildTransitive` and not `build`, because an app mostly reaches `Inngest` through `Inngest.AspNetCore` or
`Inngest.Connect`, and the assets of `build` only reach the project that references the package itself.

Everything in the package reaches every existing user on upgrade, so nothing it does may change a build that compiles
today. The registration is **opt-in**: `InngestGenerateFunctionRegistration` is false until a project sets it, and the
generator then writes nothing and the class diagnostics are not reported. Without that, a project that registers a
generic class closed by hand, or a private class from the class that holds it, would have warnings (errors, under
warnings as errors) about code that works, and a helper of its own called `AddInngestFunctions` would become ambiguous the
moment it had a class with `[InngestFunction]`. The JSON analyzer is limited by what it checks: it is silent unless the
project turns reflection-based JSON off, and then it reports types that would fail at run time.
`DisableInngestAnalyzers` removes everything the package runs in the compiler, for the build that one of them breaks.

- **Roslyn.** Both assemblies target `netstandard2.0` (RS1041) and `Microsoft.CodeAnalysis.CSharp` 4.8.0, the compiler of
  the first .NET 8 SDK and Visual Studio 17.8, the oldest that can build for `net8.0`. A component loads into its own Roslyn
  version or a newer one, so one build serves every compiler the packages can be used with, where System.Text.Json's
  generator ships builds for Roslyn 3.11, 4.0 and 4.4 under `analyzers/dotnet/roslyn*` because its package also
  targets older runtimes. `EnforceExtendedAnalyzerRules` is on, so the banned-API rules (RS1035) apply.
- **The code fix is an assembly of its own.** It needs `Microsoft.CodeAnalysis.Workspaces`, which the command-line
  compiler does not have, and a generator or analyzer must not reference it (RS1038): the extension would fail to load
  there. The compiler never loads a type of the code fix assembly, so the assembly beside the generator is harmless to a
  build, and the IDE uses it.
- **The generator** asks for the classes with `ForAttributeWithMetadataName`, which the compiler answers from an index
  of attribute names, and describes each one as a string: no symbol, compilation or location enters the pipeline. One
  `Registration` value, which compares by value (`EquatableArray<string>`), comes out of `Collect()`, so an edit that
  leaves the registered classes as they were does not write the file again. `IncrementalityTests` check that with
  `trackIncrementalGeneratorSteps`: every step's outputs are `Cached` or `Unchanged` after an unrelated edit.
- **Output.** A static class in the namespace `Inngest`, named after the assembly, with a hash of the name added when
  an identifier cannot hold all of it: two assemblies of one app each get a class, and a public class of the same name
  in both would hide the extension methods of the one that is referenced (the compiler only warns, CS0436), which
  `My-Lib` and `My.Lib` would do without the hash. One statement for each class, because a long chain of calls is one
  expression and the compiler has a limit on those. The file disables CS0612 and CS0618, because a function class may be
  `[Obsolete]` and the warning would be in a file nobody can edit. The method is `internal` unless the project says
  `public`, and its name is a project property too. With public methods of the same name in two assemblies, or an internal
  one seen through `InternalsVisibleTo`, the call is ambiguous (CS0121); `InngestFunctionsMethodName` is the way out.
- **Diagnostics.** The generator reports nothing about classes. `FunctionClassification` answers whether a class can
  be registered, for the generator, which leaves out the classes that cannot, and for `FunctionClassAnalyzer`, which
  says why, when the project has turned generation on. The two cannot disagree, and the messages are analyzer
  diagnostics, which `#pragma` and `.editorconfig` control. A property value the generator cannot use is reported by the
  generator.
- **JSON analyzer.** It registers nothing unless `PublishAot`, `PublishTrimmed` or
  `JsonSerializerIsReflectionEnabledByDefault` (read through `CompilerVisibleProperty`) say that reflection-based JSON is
  off; the SDK only flips the switch while publishing, so the properties it flips it from are read, and they are those of
  the project it compiles, so a library that sets none is not checked. It checks the type arguments of the function base
  class, `AddFunction<TEventData>(options, handler)` and the typed steps (`RunAsync<T>`, `AIWrapAsync`,
  `WaitForEventAsync<T>`, `WaitForSignalAsync<T>`, `InvokeAsync`, `InferAsync<T>` and `PublishAsync<T>`, which read their
  type back through the app's serializer options).
- **What counts as listed** is what the generator of System.Text.Json writes metadata for, and the analyzer follows its
  rules: the types of public properties and fields, and of non-public ones with `[JsonInclude]`, minus `[JsonIgnore]`;
  the parameters of the one constructor it calls (`[JsonConstructor]`, else the only public one when there is no public
  parameterless one); the element type of a collection, whose own members and constructors it ignores; the type arguments
  of framework generics; `[JsonDerivedType]`. A type with a `[JsonConverter]` has nothing reached through it. The roots
  are the types a context lists, in the project and in referenced assemblies that use System.Text.Json and are not part
  of the framework, plus the types `PublicTypesJsonContext` lists, the context the SDK appends to every app's options, so
  that `string` or `IReadOnlyList<string>` is not asked for when the app's context reaches none. `JsonCoverageTests` hold
  the analyzer to this: they compile a set of probe types, one for each rule, and compare the analyzer with the generator
  of every .NET version they can find in the targeting packs (8 and 10 agree), and compare the types taken from the SDK's
  context with the ones that context has. The set is computed when the first type needs it.
- **The generator cannot fill the context.** Generators run on the same input and the output of one is not input to
  another, so `[JsonSerializable]` attributes written by Inngest's generator would never reach System.Text.Json's. The
  code fix edits the user's own file instead, with `ImportAdder` and the simplifier for the using directives. Its equivalence
  key names the context and not the type, and its own `FixAllProvider` adds every type of the scope to that context in one
  edit: the batch fixer merges text changes, which collide when several diagnostics edit the same class.
- **The package path is tested.** Everything above runs from the build output in the unit tests, which is not how a user
  gets it. `PackagedConsumerTests` pack `Inngest` and `Inngest.AspNetCore` into a temporary feed, restore them into a small
  app that references only `Inngest.AspNetCore`, and build it in each state: nothing set (no diagnostic, under warnings as
  errors), generation on, reflection off, and `DisableInngestAnalyzers`. A CI job runs them with the .NET 8 SDK too.

### Logging

`ILogger` with source-generated `LoggerMessage` methods throughout. `FunctionContext.Logger` drops messages while
the function replays memoized steps, so a message written between two steps appears once per run rather than once
per call.

### Webhooks

`MapInngestWebhook` turns a provider's webhooks into events. The work is split so that nothing but the last step knows
about ASP.NET Core:

```
Kestrel -> MapInngestWebhook -> WebhookEndpoint (bounded, pooled body) -> WebhookReceiver
  size check -> IWebhookVerifier.Verify -> JsonDocument.Parse -> WebhookProvider.CreateEvent
  -> WebhookOptions.Transform -> IInngestClient.SendAsync (within SendTimeout) -> status code
```

- **Package.** `Inngest.Webhooks` holds the verifiers, `WebhookProvider`, `WebhookOptions` and `WebhookReceiver`, and
  depends on `Inngest` alone. `Inngest.AspNetCore` references it and adds the endpoint, as `InngestCommHandler` in
  `Inngest` is reached by `MapInngest` and by the Connect worker. The verifiers work on `IWebhookRequest` (the raw bytes
  and a header lookup), so an Azure Functions or a Lambda host can reuse them. `WebhookReceiver` is internal for now;
  such a host gets `InternalsVisibleTo` or the type becomes public when it needs it. `Inngest` itself gained no API.
- **Verification** is in front of everything: the body is not parsed, and no header is used for anything but the
  signature's own inputs, until a signature matches. Signatures are decoded without exceptions, compared with
  `CryptographicOperations.FixedTimeEquals` against every candidate and every secret (no early exit), and the timestamp
  window is checked after the signature, so the log can tell a replay from a forgery. A secret never appears in a
  message, and neither does a body, a signature or a header.
- **Schemes.** Standard Webhooks signs `{id}.{timestamp}.{body}` with HMAC-SHA256 and the base64 key behind `whsec_`, and
  carries several `v1,<base64>` signatures for rotation; `v1a` (Ed25519) is ignored, because .NET has no Ed25519 in its
  libraries. Stripe signs `{t}.{body}` with the text of the secret and carries one `v1` per active secret. GitHub and
  Shopify sign the body only, so they have no replay window and their headers are not covered. `HmacWebhookVerifier`
  expresses all of them, and Slack's `v0:{timestamp}:{body}`, as options; GitHub and Shopify are two presets of it.
- **Events.** The default mappings follow Inngest's own transforms where its guide has one (`stripe/<type>` with the
  event as data and its `id` as the id; `<prefix>/<type>` with the `data` of the payload for Clerk and Resend). The data
  is a `JsonElement` of the payload, which the client writes through the serializer options with `JsonElement`
  metadata that `PublicTypesJsonContext` always adds, so it needs no reflection, and nothing from a header is put in it:
  only the body is signed by GitHub and Shopify, so their event names and ids, which come from headers, are checked for
  their shape (short visible ASCII, the whole name at most 256 characters) but remain whatever a holder of one captured
  delivery sends. A provider that has delivery ids needs one: a delivery without is a 400, since an event without an id
  would never be deduplicated.
- **Lifetime.** The parsed payload reads the pooled body in place. Before the send, a `JsonElement` as the event's `Data`
  or `User` is cloned, so that the event can be kept (the test host's `SentEvents` and middleware do), and the pooled
  body is cleared and returned when the request ends.
- **Answers** have no body: 200, 204 (the transform dropped it), 401, 400, 413, 503 (Inngest did not accept the event
  within `SendTimeout`, which is short because providers wait seconds, or failed in a way that may pass) and 500 (a
  verifier or a transform threw). A send that may pass is a 5xx on purpose: a provider that retries delivers the webhook
  again and the delivery id makes a second attempt a duplicate that Inngest drops. A send that cannot pass is not: an
  `InngestException` with a 4xx other than 408, 425 and 429 (the statuses the client itself retries) is answered 413 for
  413 and 400 otherwise, a client refusal (`ArgumentException`) is a 400, and a payload that parses but cannot be written
  (an unpaired surrogate escape) is found by writing it once before the send and is a 400; each is logged as an error
  that says a redelivery fails the same way.

### What is supported

| Area | Status |
|---|---|
| Serve endpoint: introspection, in-band and out-of-band sync, calls | Supported |
| Dev mode and cloud mode, signing, signing key rotation, response signing | Supported |
| Steps: run, sleep, sleepUntil, waitForEvent, invoke, sendEvent, parallel steps | Supported |
| A dependency injection scope per step handler, for a `DbContext` and other scoped services | Supported (.NET only): `RunScopedAsync` and typed overloads on the step tools |
| Flow control and function config: concurrency, throttle, rate limit, debounce, batching, priority, idempotency, retries, cancelOn, timeouts, singleton | Supported |
| Function config as attributes on the class, typed events, typed results and invoke by class | Supported (.NET only) |
| Settings from a configuration section (`appsettings.json`), below `INNGEST_*` variables | Supported (.NET only) |
| Failure handlers (`inngest/function.failed`) | Supported |
| Errors: non-retriable, retry-after, step failures | Supported |
| Middleware: observer and wrap hooks, for the whole app or for one function | Supported |
| `use_api` for large runs | Supported |
| Checkpointing (running several steps per call and saving each to Inngest as it completes) | Supported, opt-in with `CheckpointingOptions`; off by default |
| Connect (WebSocket workers): start, handshake, heartbeats, leases, reply buffering and flush, draining, reconnects, graceful shutdown | Supported (`Inngest.Connect`) |
| Connect connection state and a readiness health check | Supported (`IInngestConnection`, `AddInngestConnect`) |
| Serve health check: keys configured, the API answers a request that carries the signing key, optionally the app is synced | Supported (`AddInngest` on `IHealthChecksBuilder`, `InngestHealthCheckOptions`) |
| Aspire AppHost: the dev server and a self-hosted server as containers, apps synced at the address the server's container reaches, `INNGEST_*` settings for projects and containers | Supported (`Inngest.Aspire.Hosting`), for local development. The servers keep no state outside their container. |
| `step.waitForSignal` and sending signals, `step.fetch`, `step.ai.infer` | Supported (`WaitForSignalAsync`, `SendSignalAsync`, `FetchAsync` and `AddInngestStepFetch`, `InferAsync`) |
| `step.ai.wrap` | Supported (`AIWrapAsync`), with its input shown in the Inngest UI and an input edited there passed to the handler; an input edited for `step.ai.infer` is spread over its body, as in inngest-js |
| Realtime: publishing data and streams, subscription tokens, subscribing | Supported: `PublishAsync` and `PublishStreamAsync` (`text/stream`) on the step tools and `IInngestRealtime`, typed topics, tokens, `SubscribeAsync` over Inngest's server-sent events with a new token on every reconnect, and `MapInngestRealtime`, a relay to browsers that shares one subscription per channel and topics |
| Realtime messages to SignalR clients: joining and leaving through a hub, authorization of each join, one shared subscription per channel and topics, a bounded queue for each connection, delivery by connection id | Supported (`Inngest.SignalR`) over the JSON and MessagePack protocols; under Native AOT only where SignalR supports it (JSON, .NET 9 and later) |
| Blazor components for realtime: Blazor Server, WebAssembly and interactive Auto | Supported (`Inngest.Blazor`): `RealtimeView` subscribes after its first interactive render and never while prerendering, through a source each side registers; the WebAssembly source reads the server-sent events of `MapInngestRealtime` with `HttpClient` response streaming and opens the connection again with a growing delay. It does not interpret run messages or pause in hidden tabs. |
| AWS Lambda: the serve endpoint behind API Gateway REST and HTTP APIs and function URLs, in a managed runtime function or a Native AOT executable | Supported (`Inngest.AwsLambda`, `InngestLambdaHandler`), with buffered responses only: `Streaming` has no effect, and the call's cancellation token is never cancelled |
| Streaming responses (`INNGEST_STREAMING`) | Supported on the ASP.NET Core endpoint (`InngestOptions.Streaming`): a call longer than three seconds gets a 201, a space every three seconds and the wrapped result. Azure Functions streams the same way with the worker's ASP.NET Core integration, and not with its built-in HTTP model. |
| Azure Functions (isolated worker): built-in HTTP model and ASP.NET Core integration | Supported (`Inngest.AzureFunctions`): tested against fakes of the worker and against the Inngest server behind a stand-in for the Functions host, not in a deployed app. A call has to be answered within the platform's 230 seconds. |
| Receiving webhooks: Stripe, GitHub, Shopify, Standard Webhooks (Svix, Clerk, Resend) and HMAC signatures, event ids from the delivery ids | Supported (`Inngest.Webhooks`, `MapInngestWebhook`). Inngest's hosted webhooks and their transforms are the counterpart. `v1a` (Ed25519) signatures are not checked. |
| Event sessions and their propagation (`meta.sessions`, `sessionPropagation`) | Supported (`EventMeta`, `FunctionContext.Sessions`, `InngestOptions.SessionPropagation`) |
| The user behind an event: selected claims of the current user in the event's `user`, protected, restored in the run, passed on to events, invocations and failure handlers | Supported (.NET only): `Inngest.UserContext`, `Inngest.UserContext.AspNetCore`. See [User context](#user-context) |
| `defer` | Not yet |
| Durable endpoints (beta in Inngest) | Supported on ASP.NET Core and marked experimental (`INNGEST001`): `UseInngestDurableEndpoints`, `WithDurableEndpoint` and `[DurableEndpoint]` in `Inngest.AspNetCore`. The steps of a request run inline, and the run goes to Inngest through the checkpoint API when a step has to wait or fails, or steps run in parallel. Not yet: the output proxy on the app's domain, streaming responses from an endpoint, resuming an endpoint that requires authorization, and `InngestTestHost`. Flow control, `defer` and `group.experiment` are not supported for endpoints upstream |
| Middleware transform hooks (`transformFunctionInput`, `transformStepInput`) | Supported as `IPayloadTransform`, on raw JSON: events, memoized step results, step output and function output. The data an invoke sends is not transformed. |
| `wrapRequest` | Not implemented; ASP.NET Core middleware or an endpoint filter on `MapInngest` wraps a request |
| `group.parallel` race mode | Supported (`ParallelAsync(ParallelMode.Race, ...)`, or `StepOptions.ParallelMode` for one step) |
| Fan-out over a list: a step per item with stable ids, a window of parallel items, fail-fast or collected outcomes, chunks in child runs for lists beyond the 1,000 steps of a run | Supported (.NET only): `MapAsync`, `ForEachAsync`, `MapOutcomesAsync`, `MapInChunksAsync` and `MapOutcomesInChunksAsync` over `IStepTools`, and `FanOutFunction` for the function of a chunk |
| REST API for runs: get a run, list the runs of an event, cancel a run | Supported (`IInngestRunsClient`) |
| OpenTelemetry spans and metrics | Supported (`Inngest.OpenTelemetry`) |
| Encryption middleware (`@inngest/middleware-encryption`): default strategy, fallback keys, decrypt-only mode, the event field, custom services | Supported (`Inngest.Encryption`), readable by the TypeScript package and the reverse. It reads the AES envelopes of the package's first versions and never writes them, and it does not encrypt the data of `step.invoke`, which has no payload hook, nor the input of AI wrap steps and the request of inference steps, which no payload hook covers. |
| Sentry error reporting | Supported (`Inngest.Sentry`), without the TypeScript middleware's Sentry spans |
| Chat clients of Microsoft.Extensions.AI as steps | Supported (.NET only, `Inngest.Extensions.AI`): `UseInngestSteps` makes model calls and streaming calls steps, `UseInngestFunctionInvocation` makes tool calls steps, with the input shown in the Inngest UI and an edited input passed on. Responses are saved as JSON written with `AIJsonUtilities.DefaultOptions`, so the app registers no contracts for them, and `RawRepresentation` does not survive a replay. A streaming call publishes its text to a realtime topic while the model runs, when asked to. `InngestInferChatClient` has Inngest call an OpenAI-compatible provider instead (`step.ai.infer`). |
| Transactional outbox: events saved with the app's data in one transaction and sent afterwards under one id each | Supported (`Inngest.EntityFrameworkCore`) on Entity Framework Core 8 (net8.0) and 10 (net10.0), not 9; the tests run SQLite on both and PostgreSQL with EF Core 10 and Npgsql 10 on net10.0 only; not Native AOT |
| Unit tests on the real execution engine, with mocked steps | Supported (`Inngest.Testing`) |
| End-to-end tests against the dev server or a self-hosted server in Docker, including the Connect gateway, and against an app under `WebApplicationFactory` | Supported (`Inngest.Testing.Containers`, `Inngest.Testing.Containers.AspNetCore`), with the image you choose; tested with `inngest/inngest:v1.49.0`. The factory needs .NET 10. |
| `dotnet new` templates for a web app and a Connect worker, and a tool that runs the Inngest dev server | Supported (`Inngest.Templates`, `dotnet-inngest`) |
| Registering function classes without listing them | Supported (.NET only), when a project sets `InngestGenerateFunctionRegistration`: the source generator in `Inngest` writes `AddInngestFunctions()`. It does not compute the options at compile time |
| Finding the types a Native AOT app has not given its `JsonSerializerContext` | Supported (.NET only): an analyzer and a code fix in `Inngest`, for function classes, delegate functions and the typed steps; values whose type is known only at run time, such as the data of an invoke or an event the app sends, are not checked |


## Spec deviations

| Topic | Spec | This SDK, following the server and inngest-js |
|---|---|---|
| Step not found | `StepNotFound` op | `500` with `X-Inngest-No-Retry: false`; the server has no such op |
| Bad signature | `500` | `401` |
| Sleep and wait payloads | `opts.duration`, `opts.event` | sent both in `opts` and in `name`, which the server falls back to |
| Sync kind values | `in-band` | `in_band` / `out_of_band` |
| Default retries | "4 attempts" | the server's default of 4 retries (5 attempts) |
| `sdk` field | equal to `X-Inngest-SDK` | `dotnet:v<version>` in the body, `inngest-dotnet:v<version>` in the header, as the server only splits the body value on `:` |

## Differences from the TypeScript SDK

Behaviour that differs on purpose, beyond the API shape:

| Area | TypeScript SDK | This SDK |
|---|---|---|
| Function options | Invalid options produce a warning at sync | Rejected at startup with one exception that lists every problem, each naming its function |
| Sync URL | Keeps `deployId` in the registered URLs | Strips it, as the spec requires |
| Failure handlers | Always registered with runtime type `http` | Same runtime type as the function they belong to |
| Event sends | Retries every failure | Retries transport errors, 5xx, 408, 425 and 429; other 4xx fail at once |
| `INNGEST_DEV` URL | Redirects the APIs even when `isDev` is false | Only used in dev mode |
| Flags in variables | `1`/`true` and `0`/`false`; any other `INNGEST_DEV` is a dev server address | Also `on`/`yes` and `off`/`no`, so `INNGEST_DEV=off` is cloud mode, not a dev server at `http://off` that turns off signature checks |
| Error names | JavaScript error names, with a `__serialized` marker | .NET exception type names, no marker |
| Planned run steps | `opts` is `{}` | `opts.type` is `"step.run"` |
| Targeted call whose step was started but not awaited | Answers 200 and drops the step | Runs the step |
| Uncaught `StepFailedException` | Final only for the most recent step failure | Always final |
| Memoized `{}` | Read as an error | Read as data |
| Steps called inside other steps or observer hooks | Hang | Throw `InvalidOperationException` |
| Invalid in-band sync body | 500 | 400 `invalid_request` |
| Introspection | Omits `has_signing_key_fallback` and `authentication_succeeded` when unauthenticated | Includes both, as the spec describes |
| In-band `sdk_author` | `inngest` | `inngest-dotnet` |
| `Match` and `If` on a wait or cancellation | Match replaces If (the types forbid setting both) | Both apply, joined with `&&` |
| Connect shutdown | Closes the socket without a code | Closes with 1000 and `WORKER_SHUTDOWN`, which the gateway recognizes |
| Connect replies not acknowledged | Flushed over HTTP only on reconnect and close | Flushed as soon as the 5 second deadline passes, and at shutdown |
| Unsigned out-of-band syncs | Can be refused with `INNGEST_ENABLE_UNAUTHED_SYNC=false` | Same, plus `AllowUnsignedSync`; unsigned callers are never told why a sync failed |

## AWS Lambda

`InngestLambdaHandler` serves the endpoint from a Lambda function. It reduces the two payload formats of API Gateway
(`APIGatewayProxyRequest` for REST APIs, `APIGatewayHttpApiV2ProxyRequest` for HTTP APIs and function URLs) to an
`IServeRequest`, hands it to `InngestCommHandler` with no service scope, so that the handler creates one per invocation,
and maps the `ServeResponse` back to the response type of the payload format. The framework name is `aws-lambda`, as
in inngest-js.

- **Bodies.** The signature covers the bytes Inngest sent: the UTF-8 of the body text, or what a base64 body decodes to
  (`isBase64Encoded`, which API Gateway sets when a binary media type matches). Responses are JSON, so they leave as
  UTF-8 text with `isBase64Encoded` false, and their signature, made over those bytes, holds.
- **Headers.** Names are matched without regard to case. REST events merge `headers` and `multiValueHeaders`, with the
  multi-value form winning; HTTP API events have lowercase names and values joined by commas.
- **Query.** HTTP APIs decode `queryStringParameters` twice, so a `%2B` arrives as a space. `rawQueryString` is the
  source for HTTP APIs, and REST events, which only have decoded dictionaries, are encoded into a query string. One
  parser reads both, so a parameter has the same value from either.
- **URL.** The scheme comes from the last value of `X-Forwarded-Proto`, which the closest proxy appends and ASP.NET
  Core's forwarded headers middleware trusts (`https` unless it says `http`), and the host from `Host`, or the request
  context's domain name. The path is `rawPath` for HTTP APIs, which already starts with a named stage, and
  `requestContext.path` for REST APIs when it extends `path`, which is when it holds the stage. API Gateway fills it with
  the path the client called; on a custom domain both fields hold the base path mapping, as a real event in
  aws-lambda-dotnet issue 127 shows. inngest-js uses `path`, which leaves the stage out. The URL is built on first use,
  and only out-of-band syncs use it: an in-band sync names the URL in its signed body. Anything the event cannot show,
  such as the API mapping of an HTTP API or a CloudFront host, is left to `ServeOrigin` and `ServePath`.
- **Time.** The call's cancellation token is `CancellationToken.None`. Lambda gives no signal before it ends an
  invocation, and cancelling shortly before `RemainingTime` runs out could only turn the 5xx that Inngest retries anyway
  into an error of the handler's own: the serve handler rethrows a cancellation, so an error response would have to be
  built outside it, where the headers and the signature are made, calls that would have finished lose their last
  moments, and the abandoned work stays alive in an environment that a timeout would have reset. Checkpointing's
  `MaxRuntime` limits the time steps may start within.
- **Streaming.** The answer is a single buffered response, which cannot write the 201 and the spaces of
  `InngestOptions.Streaming` while a call runs, so `Streaming` is not consulted, and the handler logs a warning (event
  id 380) when it is built with the setting on. Lambda can stream responses to .NET functions
  (`Amazon.Lambda.Core.ResponseStreaming` with function URLs in `RESPONSE_STREAM` mode, or REST APIs with a streaming
  integration), which would be a second response path with its own prelude; the handler does not use it.
- **Native AOT.** Nothing reflects, and CI publishes `samples/Inngest.LambdaAotSample`, the guide's function, where a
  warning fails the publish. The event types come from `Amazon.Lambda.APIGatewayEvents`, which the app lists in its own
  `JsonSerializerContext` for `SourceGeneratorLambdaJsonSerializer`. The package ships none: a context fixes the shape
  of types it does not own, cannot hold the REST and HTTP API event types together (their nested types share names,
  `SYSLIB1031`) and could not be combined with the app's other event types.

## Connect

With `AddConnect`, the app opens an outbound WebSocket to Inngest instead of exposing an endpoint, which suits
workers behind a firewall. The worker calls `POST {api}/v0/connect/start` with the hashed signing key, dials the
returned gateway with the `v0.connect.inngest.com` subprotocol and exchanges protobuf `ConnectMessage` frames
(`connect.v1`, copied from inngest-js):

1. Handshake: `GATEWAY_HELLO`, then `WORKER_CONNECT` with the same app configuration a sync sends (runtime type
   `ws`), then `GATEWAY_CONNECTION_READY` with the heartbeat and lease intervals, then `WORKER_READY`.
2. Each `GATEWAY_EXECUTOR_REQUEST` is acknowledged, run through the same execution engine as an HTTP call, kept
   alive with `WORKER_REQUEST_EXTEND_LEASE` and answered with `WORKER_REPLY`. A reply that is not acknowledged within
   5 seconds is sent again over `POST {api}/v0/connect/flush`.
3. `GATEWAY_CLOSING` makes the worker open a new connection before closing the old one; a lost connection is
   retried with backoff (1 s up to 5 minutes); a 401 switches to the fallback signing key.
4. On shutdown the worker sends `WORKER_PAUSE`, waits for running calls up to the host's shutdown timeout and
   closes with 1000 `WORKER_SHUTDOWN`.

Each socket has a single writer fed by a channel and a reader that never runs user code, so a slow function cannot
stall heartbeats or lease extensions.

## Health check

`AddInngest` on `IHealthChecksBuilder` (`Inngest.AspNetCore`) tells a readiness probe whether an app that serves
functions can work with Inngest; `AddInngestConnect` does the same for a Connect worker. It lives in
`Inngest.AspNetCore` because only apps that call `MapInngest` use it and that package already references ASP.NET
Core, where the health checks are, so `Inngest` gets no new dependency. It sends its requests through
`InngestApiTransport`, reached by `InternalsVisibleTo`, so they carry the SDK headers and the hashed signing key and are
repeated with the fallback key on 401 or 403, like every call to the API. `SendKeyedAsync` also says which key was
answered, which the result shows.

**The request.** In cloud mode, and against a self-hosted server, `GET {api}/v2/apps?limit=1`: it needs the key, lists
at most one app and changes nothing. `GET /v2/health` would be lighter, but Inngest Cloud answers it to anyone (an
unauthenticated request gets a 200), so it would pass with a key Inngest has never seen. A self-hosted server does check
the key there. In dev mode, `GET {dev server}/health`, the server's own health endpoint, which does not depend on the
v2 REST API: `/v2/apps` came with Inngest v1.40.0 and `/v2/apps/{id}` with v1.27.1. A 2xx is healthy, 401 and 403 are a refused key, 429 is Degraded and
anything else is a failure. The body of that request is not read, and no error text of Inngest's goes into a
description, so a gateway's error page cannot reach one.

**Sync.** With `CheckSync`, after the first request succeeded, `GET {api}/v2/apps/{app id}`, which Inngest marks Beta:
`data.functionCount`, `data.isArchived` and `data.latestSync` (`status`, `syncedAt`, `error`, `appVersion`). The dev
server leaves out zero and false values and reports no status or time. The order of the verdicts: 404 is not
registered; archived; a status of `error`, or an error without a status; another app version than `AppVersion`, when
both are set; another number of functions than the registry holds; else synced. The registry's count is the number of
definitions, which includes failure handlers, as Inngest counts them. The status, time and version that reach the result
are Inngest's own text for the app, cut to 64 characters. Nothing is kept in the process about syncs: Inngest syncs
through whichever instance the load balancer picks.

**Cost.** The answer is kept per registration: `CacheDuration` for a healthy or rate limited one, and the smaller of
`FailureCacheDuration` and `CacheDuration` for a failure, so that an instance is not unready for long after a blip.
Probes wait on one gate, and each takes the answer when it gets through, so overlapping probes send one request.
The health check service builds the check for every run, in a scope of its own, so the answer lives in the registration,
not in the check.

**Cancellation.** The request ends with the probe's token and at 90 percent of the registration's timeout, so that the
check can say Inngest was too slow with the registration's failure status before the health check service gives up on it
with its own words and `Unhealthy`. A registration has no timeout unless it is given one, so then the request ends after
10 seconds; HttpClient would wait for 100. A cancelled probe stores nothing, and a timeout is stored as a failure. One
deadline covers both requests of a check.

**What the result holds.** `signingKeyFallback`, `signingKey` and `eventKey` are `set`, `not set` or `missing`, never
values. The address is shown as scheme, host and port. The tests look for the keys, the hashed token and the event
key in the description, the data and the log of every kind of answer.

## Test containers

`Inngest.Testing.Containers` follows the module pattern of testcontainers-dotnet 4.16: `InngestServerBuilder` derives
from `ContainerBuilder`, `InngestServerConfiguration` from `ContainerConfiguration`, `InngestServerContainer` from
`DockerContainer`, and the builder takes the image, as every module has since 4.10, so there is no default image to go
stale. The types are named after the server because `InngestBuilder` is one letter from the SDK's `IInngestBuilder`,
which test code uses beside them. The builder keeps what the caller chose (the mode, the keys, the app URLs, the
gateway) in the configuration and turns it into the command line in `Build`, so the order of its methods never matters
and one builder can build several containers, each with keys, a gateway port and a connection string provider of its
own: a provider remembers the container it was started with, so a shared one answers for the last. Arguments added with
`WithCommand` come last, and a wait strategy with conditions of its own, which `WithWaitStrategy` replaces the default
with, is kept.

- **Command.** `inngest dev --no-discovery --no-poll`, or `inngest start --signing-key K --event-key E`, then the timing
  flags (`--poll-interval 1 --retry-interval 1 --tick 50`), `--connect-gateway-port` and a `-u` for each app. The
  `inngest start` signing key must be plain hexadecimal digits in pairs. With `-u` the server retries a failed sync
  at the poll interval and stops once it succeeds; it does not poll the app afterwards, and nothing tells the test when
  the app is registered.
- **Reaching the host.** Inside the container localhost is the container, so a loopback app URL becomes
  `host.docker.internal`, which the builder maps to `host-gateway` for Linux. Any other address is left as it is. The
  app must listen on all interfaces, since on Linux the container comes in over the Docker bridge, not loopback.
- **Ports.** The API is published on a random host port. The Connect gateway is published on the same port inside and
  outside, because the start API hands workers the port as the container knows it, so the port has to be chosen before
  the container exists. A port the builder picks is held by a listening socket from `Build` until the container starts,
  because pulling the image can take long and the port may be taken in the meantime. The container lets it go just
  before Docker starts the container, or when it is disposed. Starting again with a fresh port when Docker still finds
  the port taken is not possible on the same container, as `DockerContainer` keeps its configuration to itself and a
  created container cannot be created again; the caller builds another. The server binds its gRPC ports (50052 and
  50053) and the gateway only after it has opened connections, and the kernel may give such a port to one of them as a
  source port, so the container reserves them with `net.ipv4.ip_local_reserved_ports`.
- **Syncing after the start.** A `PUT` to the app's endpoint makes an SDK register itself with the server, under the URL
  the request arrived on. `SyncAsync` sends it with the container's name for the app as the `Host` header, so neither
  the app nor the server needs the other's address before it starts and no port is picked up front. It also sends
  `X-Inngest-Server-Kind` (`dev` or `cloud`), which the SDK hands to the server it registers with, as with the server's
  own sync. For the self-hosted server the request carries `X-Inngest-Signature`, a MAC of the (empty) body and the
  timestamp, which keeps the app from logging an unsigned sync. The GraphQL `createApp` mutation of the dev UI also
  has the server sync an app, and was not used: against `inngest start` the sync it makes arrives unsigned, a failed
  one stays in the app list, and the error is a bare code such as `unreachable` or `url_not_found`.
- **Variables.** `GetSdkEnvironment` is for processes that inherit their parent's environment, so it sets every variable
  that could outrank the others. The SDK reads the API address from `INNGEST_API_BASE_URL`, then `INNGEST_BASE_URL`, then
  the dev server's address, the event API address from `INNGEST_EVENT_API_BASE_URL` first, and `INNGEST_DEV` picks the
  mode, where only an explicit `0` keeps an inherited `INNGEST_DEV=1` from switching the signature checks off. Variables
  the list leaves out, such as `INNGEST_SERVE_ORIGIN`, still apply, and the guide says so.
- **WebApplicationFactory.** The server needs a real socket, which `TestServer` does not have. .NET 10 added
  `UseKestrel`; earlier versions can only get one with a second host built beside the in-memory one, which runs hosted
  services twice and splits the app's state, so `InngestWebApplicationFactory` targets net10.0 and lives in a package of
  its own: `Microsoft.AspNetCore.Mvc.Testing` brings build targets and a version that must not be forced on users of
  the container module. The factory calls `UseKestrel` with `ListenAnyIP(0)`. It has to be called on the factory that
  starts, because a factory made by `WithWebHostBuilder` does not inherit it. The options are applied with
  `PostConfigure` from `CreateHost`, so the container wins whatever the app or an override of `ConfigureWebHost` set.
  Kestrel reports an app on all interfaces as `[::]`, which the factory takes for its clients' base address and which a
  client cannot connect to on Windows. `CreateDefaultClient` is fixed in `ConfigureClient`, but `CreateClient` and
  `CreateDefaultClient(Uri, ...)` set the base address after it, from options that default to `http://localhost`, port
  80, so the factory declares `CreateClient` again. The addresses collection of a started Kestrel cannot be changed, so
  there is no earlier place to fix it.

## Templates and the dotnet-inngest tool

**The template package.** `src/Inngest.Templates` has `PackageType` Template and no build output. Each template under
`templates/` is a real project with `!!INNGEST_VERSION!!` where the version of an Inngest package goes. At pack time the
target `StageTemplates`, which runs before the package files are collected and so also for `dotnet pack --no-build`,
copies the folder to `obj`, replaces the tokens with `$(PackageVersion)` in a small inline MSBuild task and packs the
copy. It fails when a token is left, and it copies on every pack, because a new version changes the result while the
files stay the same. The worker's `Microsoft.Extensions.Hosting` takes its version from `Directory.Packages.props` the
same way. This follows the Aspire templates, which replace `!!REPLACE_WITH_LATEST_VERSION!!` in a copy at pack time. The
ASP.NET Core templates generate their project files with a task of the Arcade SDK, which a project outside that repository
does not have, and Aspire runs a C# script with `dotnet`. An inline MSBuild task needs neither.

The `template.json` files use what `dotnet new` offers instead of tokens of their own: a `Framework` choice that replaces
`net10.0`, the `port` generator with `coalesce` for `--http-port`, the `kebabCase` form of the project name for the app
id, a fresh `UserSecretsId`, and the restore post action. The generated code has no namespace and no folders, as in the
getting started guide. `Microsoft.TemplateEngine.Authoring.Tasks` only localizes templates, and
`Microsoft.TemplateEngine.Authoring.TemplateVerifier` snapshots a template folder, so neither is used.

**Their tests.** `tests/Inngest.Templates.Tests` packs `Inngest`, `Inngest.AspNetCore`, `Inngest.Connect` and
`Inngest.Templates` into a temporary feed with a version of its own (a version restored from a feed stays in the global
packages folder, so a fixed one would hide changes), installs the template package into a throwaway hive
(`--debug:custom-hive`), makes both templates and builds them with a `nuget.config` that maps `Inngest*` to the feed and
the rest to nuget.org. It also starts the web app and reads its introspection, which proves the launch profile, the
Development settings and dev mode work together. The commands run in a folder outside the repository, where the newest SDK
builds everything, so the net8.0 worker gets a `global.json` that selects the .NET 8 SDK; the test is skipped, with a
reason, on a machine that has none, and CI installs both. That build turns off `CheckEolTargetFramework`, since warnings
are errors in it and the SDK warns about a target framework out of support. A run packs four projects and builds two, so it
is a job of its own in CI.

Each of the two packages has a README of its own, `src/Inngest.Templates/README.md` and `src/dotnet-inngest/README.md`,
packed in place of the shared `src/README.md`, which describes the SDK. The templates project also leaves `bin` and `obj`
out of what it packs, for a template that was built in place.

**The tool.** `src/dotnet-inngest` is a `PackAsTool` project named `dotnet-inngest`, so that `dotnet inngest` finds it, as
`dotnet ef` finds `dotnet-ef`. It targets `net8.0` and `net10.0`, each in its own folder of the package, with
`RollForward=Major` for a machine that has neither. The arguments are parsed by hand: the tool owns three options and
passes the rest to `inngest dev`, whose options change with the release, which is the opposite of what a command line
library is for, and the tool stays free of dependencies. Its types are internal, tested through `InternalsVisibleTo`.

- **Download.** The asset is named `inngest_<version>_<os>_<arch>.<tar.gz|zip>` in the release of the tag, as the
  install script and the npm package expect it, chosen by the architecture of the machine. The pinned version is checked
  against `PinnedChecksums`, a copy of the release's `checksums.txt` in the source, and never against the release, so a
  file replaced on GitHub together with its checksums is caught. Another version is checked against its `checksums.txt`,
  which catches damage only. The pin, the `inngest/inngest` image in the docs and the integration tests are raised
  together.
- **Cache.** `<local app data>/inngest-dotnet/cli/<version>/<os>_<arch>/inngest`. The download and the unpacked binary
  are made in a staging folder beside it, which is moved into place in one step, so an interrupted install leaves nothing
  that looks installed and two installs at once end with one. Only the binary is unpacked, to a path the tool chooses,
  whatever the archive's entries are called, and it gets the execute bit. It is flushed to disk before the rename, so a
  crash cannot leave a file without content that the next run finds. The checks happen at install time only, and a binary
  in the cache is run as it is, so the folder must not be writable by other users; the docs say so.
- **Running.** The CLI inherits the console, so its output and input are the user's. Ctrl+C reaches the CLI as it does
  any foreground program, and the tool waits for it and returns its exit code. On Unix `SIGINT` and `SIGTERM` that reach
  the tool alone are passed on with `kill`; the CLI takes the second `SIGINT` of a terminal as one. `SIGKILL` and a crash
  cannot be passed on, and on Windows nothing is. There is no job object, which could not be tested, so a tool that is
  ended alone leaves the CLI running.
- **Downloads.** A download is limited by how long the server may send nothing, 60 seconds for the headers and between two
  reads of the body, and not by its total time. With `ResponseHeadersRead` the `Timeout` of an `HttpClient` ends at the
  headers, so it would leave a body that stalls waiting forever. The tool sets it to infinite and limits the reads itself.
  A limit that fires, which the user did not ask for, is an error with the Docker command. Only Ctrl+C ends with 130.
- **Address.** Without `-u` or `--no-discovery`, the first launch profile that runs the project and has an `http` address
  gives the app's URL, plus `/api/inngest`.
- **Docker.** The failures that mean the CLI cannot be had print the `docker run` command for the same address. Nothing is
  run for the user.

## Transactional outbox

`Inngest.EntityFrameworkCore` makes sending an event part of a database transaction. The app adds events to its
`DbContext` with `AddInngestEvent`, `SaveChanges` stores them with its data, and a hosted dispatcher per context type sends them
afterwards. Delivery is at least once. What is meant to make a resend harmless is that every attempt carries the row's event id,
which Inngest is documented to drop when it repeats within 24 hours; see Event ids below for what was checked.

- **Table.** `InngestOutboxMessage`, mapped by `ModelBuilder.AddInngestOutbox()` as `InngestOutbox`. The key is the event
  id, a ULID unless the code gives one, so a second row for the same fact fails the save. The id, the name and the version
  are limited to 255 characters, and a case-insensitive collation (the default of SQL Server and MySQL) makes ids that differ
  only in case the same key. `Data`, `User` and `Sessions` are the event's JSON as text, written when the event is added;
  times are UTC `DateTime`s with a converter that makes the kind UTC, because providers return them unspecified. `Timestamp` is
  the event's `ts`, which the caller may set to any time, so `CreatedAt` records when the row was added. One index on
  (`SentAt`, `NextAttemptAt`) serves the claim and the cleanup.
- **Adding.** `AddInngestEvent` makes the checks the client makes when it sends (name, data and user as JSON objects, sessions)
  and serializes through the `JsonTypeInfo` of `InngestSettings.SerializerOptions`, so a failure is found while the code that
  added the event is on the stack and trimmed apps use the metadata they already registered for `SendAsync`. It finds the
  writer in the application service provider that `AddDbContext`, `AddDbContextPool` and `AddDbContextFactory` leave in the
  context's options. The event keeps the time it was added as its `ts`, not the time the dispatcher sends it.
  `PropagatedSessions` is refused, because it only exists for events sent from a run, and an event added inside a function does
  not inherit the run's sessions, because the dispatcher sends it outside the run.
- **Waking.** One interceptor instance serves every context and needs no service provider at configuration time: it finds the
  signal through the application services of the context that saved, so `UseInngestOutbox()` works in the callbacks that have
  no provider and in `OnConfiguring`. When the dispatcher starts it says once if the context's options lack the interceptor.
  `AddInngestEvent` marks the context; `SavedChanges` wakes the dispatcher at once when no transaction of the code's own is
  open, and otherwise leaves the wake-up to `TransactionCommitted`, so the dispatcher is not woken to find nothing. A rollback
  clears the mark. The signal is a one-slot channel per context type, so a wake-up that comes while the dispatcher is busy is
  kept, and many count as one. With a `TransactionScope` EF sees no transaction of its own, so the wake comes at the save, before
  the scope completes, and the events leave at the next poll (checked on PostgreSQL; SQLite has no ambient transactions). The poll
  interval is the fallback for everything else, including saves by other processes.
- **Claiming.** Dispatchers take no lock. A claim selects the ids of the due rows (`SentAt` null, `NextAttemptAt` passed, no
  live lease), then `ExecuteUpdate`s those ids with a token and an expiry, repeating the test for "due" in the `WHERE` of the
  update, and reads back the rows that hold the token, by key as well, because the token has no index and the table holds a
  day of sent rows. The two statements matter: on PostgreSQL, an `UPDATE ... WHERE id IN
  (SELECT ... LIMIT n)` that waited for a row another one was updating took the row as well and overwrote the token (checked
  with two sessions), where the repeated test in the outer `WHERE` made the loser update nothing. A dispatcher that died holds its
  rows until the lease ends. Marking sent or failed names the token, so a dispatcher that lost its lease changes nothing.
- **Sending.** One request per claimed batch, each event with its own `id`, through `IInngestClient`, so the client's retries and
  the middleware apply. A request Inngest refuses because of its events (400, 409, 412, 413, 422, or an event the client
  refuses to write) is repeated event by event, so one bad event does not hold back the others; other failures fail the batch
  together. The first of those requests is sent whatever is left of the lease, and the others only while the lease covers
  another request, so every pass decides at least one event; the options require a lease of at least twice `SendTimeout` for
  the same reason. A failed event is retried after `RetryDelay`, doubling per failure up to `MaxRetryDelay`, and is never
  given up; each refused one is logged at error level on every attempt, with its id and name. When the host stops, claimed
  events are released without an attempt.
- **Clocks.** Leases, retries and the due test compare an instance's clock with times other instances wrote, because the
  clock is the `TimeProvider`'s and not the database's. A dispatcher takes rows another is still sending only if its clock is
  ahead by more than `LeaseDuration` minus `SendTimeout`; a row saved by an instance whose clock is ahead waits for the
  others' clocks, unless that instance dispatches it itself.
- **Cleanup.** Sent rows older than `Retention` (a day, as long as Inngest remembers ids) are deleted with `ExecuteDelete` in
  batches, on a loop of its own so that a long cleanup never delays a send.
- **SQL Server.** Under the locking `READ COMMITTED` that is its on-premises default, the due query waits for rows that open
  transactions inserted, so one long unit of work that adds an event stalls every dispatcher until it commits; the remedy is
  `READ_COMMITTED_SNAPSHOT`, on by default in Azure SQL Database. This follows from how SQL Server locks and was not run.
  PostgreSQL does not wait (a test holds an uncommitted event while a dispatcher runs).
- **Event ids.** Inngest takes any string as an event id. The dev server (v1.49.0) starts a function's run once per id: a
  second send under the same id is answered 200 with a new internal event id, and no run starts. The server's source, read at the
  v1.49.0 tag (28cb533), says that a repeated event can exist in its event store, because the id is enforced when scheduling
  runs, and keeps the run's idempotency key for 24 hours (`FunctionIdempotencyPeriod`), which is what Inngest's documentation
  gives for Inngest Cloud. Only a repeat within seconds was tested, and only against the dev server.

How the established outboxes differ, where it shaped this one (read at CAP ad19c50, MassTransit d9f61f7 and Wolverine e9110b7,
all of October 2026). CAP stores messages in the app's transaction through its own transaction wrapper and, after the commit,
pushes them into an in-process queue; its retry processor polls for failed rows and serializes instances with a lock row in a
table that expires (`UseStorageLock`), and its cleanup deletes expired rows in batches of 1000. MassTransit locks one outbox
state row per unit of work with provider-specific statements (`FOR UPDATE SKIP LOCKED`, `UPDLOCK, READPAST`), sends in
sequence order inside that transaction and deletes the rows it sent; its commit signal is a cancelled delay when the scope ends
after the entity left the `Added` state. Wolverine stores envelopes with an owner node id, sends from memory after the commit,
and lets a durability agent reassign the rows of nodes that left the cluster. This package takes no cluster membership and no
provider-specific SQL: a lease column set by `ExecuteUpdate` is the only coordination, which suits Inngest because it does not
order events and drops a repeated id.

**EF Core versions.** `ExecuteUpdate` and `ExecuteDelete` are in `Microsoft.EntityFrameworkCore.Relational` in EF Core 8 and in
`Microsoft.EntityFrameworkCore` from EF Core 9 on, so code compiled against one does not run on the other (a
`MissingMethodException`). The net8.0 build uses EF Core 8 and the net10.0 build EF Core 10, selected in
`Directory.Packages.props`; an app on .NET 9 gets the net8.0 build, so EF Core 9 is not supported, and the dispatcher refuses to
start when it finds EF Core 9 loaded, naming the reason. There is no net9.0 target, because the SDK has none. EF Core is not
trim or Native AOT compatible (its `DbContext` constructors are marked `RequiresUnreferencedCode` and `RequiresDynamicCode`), so
the project sets `IsAotCompatible` to false while keeping the analyzers on; its own code has no warning.

**Tests.** `tests/Inngest.EntityFrameworkCore.Tests` runs against SQLite on both runtimes (the net8.0 build on EF Core 8, in a
container when the machine has no .NET 8 runtime). `tests/Inngest.IntegrationTests/Outbox` runs a dispatcher against PostgreSQL
with Testcontainers, on net10.0 only, so with EF Core 10 and Npgsql 10: four dispatchers at once, the order of claims that would
overwrite a token if the update did not repeat the test for "due", commit visibility and a `TransactionScope`. Against the dev
server, an event saved through the outbox starts its function, and a resend under the same id starts nothing.

## Aspire

`Inngest.Aspire.Hosting` puts the Inngest server into an Aspire AppHost. The shape follows the container integrations
of Aspire (`Seq`, `RabbitMQ`) and of the Community Toolkit (`MailPit`, `Ollama`): a `ContainerResource`, an `Add*`
extension, an internal class that holds the image tag, health checks and a link for the UI. These decisions are
specific to Inngest:

- **The container.** The image is `inngest/inngest`, at the tag the integration tests of this repository use, and has
  no entrypoint, so the resource sets `inngest` as the entrypoint. The dev server runs `dev --no-discovery`: discovery
  scans ports inside the container, where the apps are not. Polling stays on, at the server's default of 5 seconds, so
  that an app that starts after the server, or restarts with other functions, is found; the self-hosted server polls
  only with `--poll-interval`, so it gets one.
- **Addresses.** Nothing is written as text. `WithApp` and `WithReference` put `ReferenceExpression`s over endpoint
  references into the arguments (`-u <address>`, once per app) and the environment, and Aspire resolves them for the
  resource that receives them. For a server's container, an app that is a project on the host is `aspire.dev.internal`
  with the port of the container tunnel (or `host.docker.internal` where the tunnel is off); for a project on the host
  the server is `localhost` with the proxy's port; for a container it is the name on the container network. The SDK
  registers the address it was synced through, so the step calls go to the same address.
- **The Connect gateway.** `POST /v0/connect/start` answers a worker with the host it called and the port the gateway
  listens on in the container (8289), which is not the port Aspire publishes it on, so a worker that used the answer
  would log `Could not open a WebSocket to the gateway at ws://localhost:8289/v0/connect`. Publishing the gateway
  on the same port is not an option: Aspire picks only the host port of a container endpoint and wants the container's
  port in the model. So the gateway is an ordinary proxied endpoint, and `WithReference` gives the worker the address
  of that endpoint as `INNGEST_CONNECT_GATEWAY_URL`, which the Connect worker prefers to the server's answer
  (`ConnectOptions.GatewayUrl`). A worker that is started outside `WithReference` gets the server's answer. The tests
  start a Connect worker against both servers.
- **Dev and self-hosted.** The dev server's reference is `INNGEST_DEV=<address>`, as
  `samples/Inngest.Sample/compose.yaml` has it for dev mode. The self-hosted server's reference is `INNGEST_DEV=0` and
  the two keys, as the `cloud` profile of that file has it; `0` is set because projects inherit the environment of the
  AppHost, and an inherited `INNGEST_DEV=1` would turn off signature checks. Both also get the address in
  `INNGEST_BASE_URL`, `INNGEST_API_BASE_URL` and `INNGEST_EVENT_API_BASE_URL`: `InngestSettings` reads the last two
  before the base URL, so an inherited one would win over it. Other `INNGEST_*` variables of the AppHost's environment
  still reach the projects, and an `INNGEST_SERVE_ORIGIN` makes runs undeliverable without a word, which the guide says.
  The keys reach the server through `INNGEST_SIGNING_KEY` and `INNGEST_EVENT_KEY`, which its CLI reads (flag, then
  variable, then file), so they are not in the command line. `inngest start` takes a signing key only as hex of even
  length, so the generated one is 64 digits, and a key that is not is reported when the server starts.
- **The manifest.** The dev server is excluded (`ExcludeFromManifest`), and `WithReference` to it sets nothing in
  publish mode: a reference would dangle, and `INNGEST_DEV` would put a deployed app into dev mode, which checks no
  signatures. The self-hosted server is a `container.v0` with its keys as parameters, and what refers to it has settings
  for cloud mode. `ManifestTests` writes the manifest through the publish mode of an AppHost and checks that every
  `{resource.…}` expression names a resource in it.
- **Tests.** `tests/Inngest.Aspire.Hosting.Tests` is itself an AppHost project: `Aspire.Hosting.Testing` finds the
  orchestrator and the dashboard through assembly attributes that `Aspire.AppHost.Sdk` writes, and has no other way to
  find them. The `AppModel` tests resolve arguments and environments for endpoints that the test allocates.
  `PinnedVersionsTests` fails when the Aspire version of `global.json` (the AppHost SDK) differs from the packages in
  `Directory.Packages.props`, or when the default image differs from the one that the harness of the integration tests
  runs. The `Containers` tests run both servers in Docker next to a project and a Connect worker, through the real
  orchestrator, with an inherited `INNGEST_*` environment that the settings of `WithReference` must win over.

## User context

`Inngest.UserContext` carries the user who caused a run into it. It is a format and a middleware; `Inngest` itself only
lets it see its internals. It has no counterpart in the TypeScript SDK.

- **Wire.** The event's `user` object, which Inngest documents as user data and stores encrypted at rest, gets one
  reserved key, `_inngest_dotnet_user: {"v":1,"t":"<text>"}`; every other key is the app's. `t` is what
  `IUserContextProtector.Protect` made of this JSON: `{"v":1,"iat":<unix s>,"exp":<unix s>,"ev":"<event name>","nt":..,"rt":..,"c":{"<claim type>":["<value>"]}}`.
  `ev` ties the text to the event it was made for, and `fn` for an invocation to the function that is invoked
  (`<app id>-<function id>`), because `inngest/function.invoked` is one name for every function and Inngest builds it
  from a body its caller supplies. `nt` and `rt` are the name and role claim types when they are not the .NET defaults, and `c` holds only the claim types of `UserContextOptions.Claims`, up to `MaxSize` bytes. The expiry
  and the event name are inside the protected text, so every protector has them and a custom one only has to detect
  changes and, ideally, encrypt. The default protector is Data Protection with the purposes `Inngest.UserContext` and `v1`.
- **Capture.** `WrapSendEventAsync` runs for every send. Outside a run it asks the `IUserContextSource`; the ASP.NET Core
  package registers one over `IHttpContextAccessor`, which flows with the async context into the scope the client makes
  for middleware. Inside a run (`context`, or `Invocation.AmbientContext` for a client used in a function) it attaches the
  run's own snapshot and never asks the source, because the request that reached the app is Inngest's. Each event gets a
  text made for its own name, and the snapshot keeps its original `iat` and `exp`, so passing it on never extends it.
  Invocations have no middleware payload hook, so `InvokeOptions.WithUser` puts the envelope in `InvokeOptions.User`.
- **Restore.** `WrapFunctionAsync` reads `FunctionContext.RawEvent`, or for the failure handler of a function
  (`FunctionDefinition.FailureHandlerFor`) the event inside `inngest/function.failed`, and keeps the snapshot in a
  `ConditionalWeakTable` keyed by the run's `FunctionContext`. `ICurrentUser` is a singleton that finds the running run
  through `Invocation.AmbientContext` on every read, so it is the same in every DI scope of the call, including the
  sibling scope a scoped step makes, and in singletons. A function that batches events gets no user: a batch has no
  single one.
- **Validity.** A function replays from its top on every call, so a user that expired halfway through a run would change
  under it. The expiry is compared with the creation time of the run, which its id holds (a ULID that Inngest makes and
  every call shares), not with the clock of the call. An id that is not a ULID falls back to the clock of the call. Against
  the dev server, an event with a future timestamp gets a run created when the event is received.
- **Failure.** A payload that cannot be used gives an anonymous principal and one warning, written once per run through
  the replay-safe logger, that names the reason and the event and never the payload. `OnInvalid = FailRun` fails the run
  instead: without retries, except for text that could not be unprotected, which may mean a key store that is down. Data
  Protection reports every failure as a `CryptographicException`, so it cannot tell the two apart.
