# Deployment readiness

Before recommending the SDK for containers and the cloud, we ran it the way production runs it: in Docker against
Inngest's own server image, in dev mode and in signed cloud mode, under sustained load and for millions of
invocations. It passed every check, and the two bugs the checks turned up are fixed.

## At a glance

| Check | Result |
|---|---|
| Docker, dev mode | The sample app syncs over the Docker network and all 9 flow checks pass |
| Docker, cloud mode | All 11 checks pass with signed requests, including the refusal of unsigned and forged calls |
| Load | 8,000 of 8,000 runs completed with the right output in dev mode, and 8,000 of 8,000 in cloud mode. The app stayed under 60 MiB of memory; the server, not the app, set the pace |
| Memory | Flat over 8 million engine invocations and 1.6 million signed HTTP invocations; work abandoned on a pending step is collected |
| Concurrency | 11 stress tests with up to 4,000 concurrent invocations each get their own answers |
| Tests | 3,144 unit tests and 327 end-to-end tests against the Inngest server in Docker |

The checks ran on 2026-10-29 on an Apple M3 Max with Docker Desktop, the `mcr.microsoft.com/dotnet/aspnet:10.0`
image and `inngest/inngest:v1.49.0`; the test counts are from 2026-10-31. The raw data of every check, from the flow results to the load and memory
samples and the benchmark reports, is in [`docs/results`](results).

## Two bugs found and fixed

- **Connect kept abandoned executions alive.** Every execution got the worker's own long-lived token, so a function
  left waiting on a step stayed reachable until the worker stopped. Each execution now gets a token of its own that
  is disposed when it ends.
- **The HTTP endpoint reserved memory on the caller's word.** It sized the first body buffer from an unauthenticated
  `Content-Length`, up to 4 MB per request. It now reserves at most 64 KB and grows the buffer as the body arrives.

Both fixes come with tests that fail without them.

One race is left in the Connect worker, in theory only: if the worker stalled for 5 seconds at one exact point, a
reply would wait for its acknowledgement instead of being sent over HTTP.

## Run the sample in Docker

[`samples/Inngest.Sample`](../samples/Inngest.Sample) has a Dockerfile and a compose file that run the app with
`inngest/inngest:v1.49.0`. From that folder:

```bash
# Dev mode: the dev server signs nothing and accepts any event key.
docker compose --profile dev up --build -d
```

```bash
# Cloud mode: inngest start signs every request; the server and the app read the same two keys.
export INNGEST_SIGNING_KEY=$(openssl rand -hex 32)
export INNGEST_EVENT_KEY=$(openssl rand -hex 16)
docker compose --profile cloud up --build -d
```

The server's API and UI are published on `127.0.0.1:18288`. For your own app, the
[deploying guide](https://damikun.github.io/inngest-dotnet/guides/deploying/) covers keys, health checks and
shutdown.

## Benchmarks

The SDK's own work per call is measured in microseconds; [benchmarks.md](benchmarks.md) has the indicative figures.
A full benchmark run on dedicated hardware is coming.
