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
| `Inngest` | Client, function definitions, step tools, execution engine, middleware, signing, the framework-agnostic serve handler. Depends only on `Microsoft.Extensions.*` abstractions. |
| `Inngest.AspNetCore` | `MapInngest`, which adapts ASP.NET Core requests to the serve handler, and `AddInngest` on `IHealthChecksBuilder`, a readiness check for apps that serve functions. |
| `Inngest.Connect` | `AddConnect`, a hosted worker that receives calls over an outbound WebSocket instead of an HTTP endpoint. |
| `Inngest.Testing` | `InngestTestHost`, which runs functions in memory on the real execution engine for unit tests, with mocked steps, retries and replays. |
| `Inngest.OpenTelemetry` | `AddInstrumentation`, a middleware with spans for calls and executed steps and metrics for runs, steps, retries and durations. Depends on `OpenTelemetry.Api` only. |
| `Inngest.Encryption` | `AddEncryption`, a middleware that encrypts event data, step results and function output in the format of `@inngest/middleware-encryption`. XSalsa20, Poly1305 and BLAKE2b come from `BouncyCastle.Cryptography`, which is managed code. |
| `Inngest.Sentry` | `AddSentryReporting`, a middleware that reports the errors of functions and steps to Sentry, with tags for the call and the run's trace. Depends on `Sentry` only. |
| `Inngest.Extensions.AI` | `UseInngestSteps` and `UseInngestFunctionInvocation`, stages for the `ChatClientBuilder` of Microsoft.Extensions.AI that make each model call and each tool call of a run an AI wrap step, and `InngestInferChatClient`, a chat client of inference steps in the OpenAI chat format. Depends on `Microsoft.Extensions.AI`. |
| `Inngest.Testing.Containers` | A Testcontainers module (`InngestServerBuilder`, `InngestServerContainer`, `InngestServerConfiguration`) that runs the dev server or the self-hosted server (`inngest start`) in Docker for tests, with the settings that point an app at it and a call that has it sync the app. Depends on `Testcontainers` and `Inngest`'s public options only. |
| `Inngest.Testing.Containers.AspNetCore` | `InngestWebApplicationFactory`, a `WebApplicationFactory` that runs the app on Kestrel against the container. Depends on `Microsoft.AspNetCore.Mvc.Testing`, so it is a package of its own and targets net10.0 only. |

`Inngest.AspNetCore`, `Inngest.Connect`, `Inngest.Testing`, `Inngest.Encryption` and `Inngest.Extensions.AI` use internals of
`Inngest` through `InternalsVisibleTo`: the serve handler, the execution engine, the registry and the resolved settings, of
which `Inngest.Encryption` only reads the serializer options and `Inngest.Extensions.AI` only asks which invocation the
calling code runs in, and so whether it can make a step. Internals can change in any release, so the packages ship
together and each depends on exactly the `Inngest` of its own version (a target in `src/Directory.Build.targets` writes
the exact range). `Inngest.OpenTelemetry` and `Inngest.Sentry` use only the public middleware API, and
`Inngest.Testing.Containers` only `InngestOptions`; `Inngest.Testing.Containers.AspNetCore` reaches one internal helper
of `Inngest.Testing.Containers` through `InternalsVisibleTo`, and depends on exactly its version too.
Package validation compares every package with the last release on nuget.org, so `dotnet pack` fails on a change that
would break code built against it; `PackageValidationBaselineVersion` in `src/Directory.Build.props` is raised after
each release. `Inngest.Encryption`, `Inngest.Sentry`, `Inngest.Extensions.AI` and the two `Inngest.Testing.Containers`
packages have no release to compare with yet, so their project files clear the baseline until their first one. The
container packages are not `IsAotCompatible`, which every other package is: Testcontainers and
`Microsoft.AspNetCore.Mvc.Testing` are built on reflection, and the packages are for test projects, which are neither
trimmed nor compiled ahead of time.

### Code layout

- **Namespaces.** Public types are in `Inngest`, whatever folder they live in, so one `using Inngest;` brings in the
  whole API. Internal types are in the namespace of their folder, such as `Inngest.Execution` or
  `Inngest.Registration`. Extension methods on a framework type are in that type's namespace
  (`Microsoft.Extensions.DependencyInjection`, `Microsoft.AspNetCore.Builder`, `OpenTelemetry.Trace`), and extension
  methods on Inngest types are in `Inngest`.
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
  options in `Configure`, so users can write their own and a source generator could call `Apply` without
  reflection. `AddFunction<TFunction>()` reads them once at registration, with `GetCustomAttributes` on the type it
  names, which stays trim and AOT safe: it starts from `[InngestFunction]` and applies the others in `Order`, then
  in declaration order. The result goes through the same startup validator. Explicit `FunctionOptions` replace the
  attributes, and `AddFunction<TFunction>(configure)` adjusts them. There is no assembly scanning.
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

### Serialization

The SDK's own wire types use System.Text.Json source generation, one `JsonSerializerContext` per area. User types
(event data, step results, function output) go through `InngestOptions.SerializerOptions`, which defaults to
`JsonSerializerDefaults.Web` with reflection when reflection is enabled. Apps that trim or use Native AOT add their
own source-generated context to its `TypeInfoResolverChain`. Step results are round-tripped through JSON even on
the call that ran them, the same as every later replay sees them. Packages that save types the app does not own, such as
`Inngest.Extensions.AI` with `ChatResponse`, write them to a `JsonElement` with the options of the library the types come
from and hand the SDK only the element, so the app's options need no contracts for them.

### Logging

`ILogger` with source-generated `LoggerMessage` methods throughout. `FunctionContext.Logger` drops messages while
the function replays memoized steps, so a message written between two steps appears once per run rather than once
per call.

### What is supported

| Area | Status |
|---|---|
| Serve endpoint: introspection, in-band and out-of-band sync, calls | Supported |
| Dev mode and cloud mode, signing, signing key rotation, response signing | Supported |
| Steps: run, sleep, sleepUntil, waitForEvent, invoke, sendEvent, parallel steps | Supported |
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
| `step.waitForSignal` and sending signals, `step.fetch`, `step.ai.infer` | Supported (`WaitForSignalAsync`, `SendSignalAsync`, `FetchAsync` and `AddInngestStepFetch`, `InferAsync`) |
| `step.ai.wrap` | Supported (`AIWrapAsync`), with its input shown in the Inngest UI and an input edited there passed to the handler; an input edited for `step.ai.infer` is spread over its body, as in inngest-js |
| Realtime: publishing data and streams, subscription tokens, subscribing | Supported: `PublishAsync` and `PublishStreamAsync` (`text/stream`) on the step tools and `IInngestRealtime`, typed topics, tokens, `SubscribeAsync` over Inngest's server-sent events with a new token on every reconnect, and `MapInngestRealtime`, a relay to browsers that shares one subscription per channel and topics |
| Streaming responses (`INNGEST_STREAMING`) | Supported on the ASP.NET Core endpoint (`InngestOptions.Streaming`): a call longer than three seconds gets a 201, a space every three seconds and the wrapped result |
| Event sessions and their propagation (`meta.sessions`, `sessionPropagation`) | Supported (`EventMeta`, `FunctionContext.Sessions`, `InngestOptions.SessionPropagation`) |
| `defer` | Not yet |
| Durable endpoints (beta in Inngest) | Supported on ASP.NET Core and marked experimental (`INNGEST001`): `UseInngestDurableEndpoints`, `WithDurableEndpoint` and `[DurableEndpoint]` in `Inngest.AspNetCore`. The steps of a request run inline, and the run goes to Inngest through the checkpoint API when a step has to wait or fails, or steps run in parallel. Not yet: the output proxy on the app's domain, streaming responses from an endpoint, resuming an endpoint that requires authorization, and `InngestTestHost`. Flow control, `defer` and `group.experiment` are not supported for endpoints upstream |
| Middleware transform hooks (`transformFunctionInput`, `transformStepInput`) | Supported as `IPayloadTransform`, on raw JSON: events, memoized step results, step output and function output. The data an invoke sends is not transformed. |
| `wrapRequest` | Not implemented; ASP.NET Core middleware or an endpoint filter on `MapInngest` wraps a request |
| `group.parallel` race mode | Supported (`ParallelAsync(ParallelMode.Race, ...)`, or `StepOptions.ParallelMode` for one step) |
| REST API for runs: get a run, list the runs of an event, cancel a run | Supported (`IInngestRunsClient`) |
| OpenTelemetry spans and metrics | Supported (`Inngest.OpenTelemetry`) |
| Encryption middleware (`@inngest/middleware-encryption`): default strategy, fallback keys, decrypt-only mode, the event field, custom services | Supported (`Inngest.Encryption`), readable by the TypeScript package and the reverse. It reads the AES envelopes of the package's first versions and never writes them, and it does not encrypt the data of `step.invoke`, which has no payload hook, nor the input of AI wrap steps and the request of inference steps, which no payload hook covers. |
| Sentry error reporting | Supported (`Inngest.Sentry`), without the TypeScript middleware's Sentry spans |
| Chat clients of Microsoft.Extensions.AI as steps | Supported (.NET only, `Inngest.Extensions.AI`): `UseInngestSteps` makes model calls and streaming calls steps, `UseInngestFunctionInvocation` makes tool calls steps, with the input shown in the Inngest UI and an edited input passed on. Responses are saved as JSON written with `AIJsonUtilities.DefaultOptions`, so the app registers no contracts for them, and `RawRepresentation` does not survive a replay. A streaming call publishes its text to a realtime topic while the model runs, when asked to. `InngestInferChatClient` has Inngest call an OpenAI-compatible provider instead (`step.ai.infer`). |
| Unit tests on the real execution engine, with mocked steps | Supported (`Inngest.Testing`) |
| End-to-end tests against the dev server or a self-hosted server in Docker, including the Connect gateway, and against an app under `WebApplicationFactory` | Supported (`Inngest.Testing.Containers`, `Inngest.Testing.Containers.AspNetCore`), with the image you choose; tested with `inngest/inngest:v1.49.0`. The factory needs .NET 10. |


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
