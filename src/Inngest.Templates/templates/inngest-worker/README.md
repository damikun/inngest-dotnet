# Inngest.Worker

A worker that runs a durable [Inngest](https://www.inngest.com) function over Connect, made with the `Inngest.Templates`
package of [inngest-dotnet](https://damikun.github.io/inngest-dotnet/). It dials out to Inngest and has no endpoint to
expose.

- `Events.cs` is the event that starts the function, a record that carries its name.
- `SayHello.cs` is the function: an id, a trigger and one step.
- `Program.cs` registers the function and `AddConnect`, which opens the connection when the host starts.

## Run it

1. Start the Inngest dev server. It runs a Connect gateway for the worker, and `--no-discovery` stops it from looking
   for apps that serve HTTP, which this one does not:

   ```bash
   dotnet tool install --global dotnet-inngest
   dotnet inngest dev --no-discovery
   ```

2. Start the worker. Its launch profile uses the Development environment, where `appsettings.Development.json` turns on
   Inngest's dev mode:

   ```bash
   dotnet run
   ```

3. Send the event that starts `SayHello`:

   ```bash
   curl -X POST http://localhost:8288/e/dev -d '{"name": "app/hello.requested", "data": {"name": "Ada"}}'
   ```

   The run appears at http://localhost:8288 under Runs, with its step.

Outside the Development environment the worker runs in cloud mode, which needs the keys of an Inngest app:
`INNGEST_EVENT_KEY` and `INNGEST_SIGNING_KEY`, set as environment variables or in user secrets.
