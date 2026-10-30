# Inngest.Web

An ASP.NET Core app with a durable [Inngest](https://www.inngest.com) function, made with the `Inngest.Templates` package
of [inngest-dotnet](https://damikun.github.io/inngest-dotnet/).

- `Events.cs` is the event that starts the function, a record that carries its name.
- `SayHello.cs` is the function: an id, a trigger and one step.
- `Program.cs` registers the function, serves the endpoint Inngest calls and has an endpoint that sends the event.

## Run it

1. Start the app. Its launch profile uses the Development environment, where `appsettings.Development.json` turns on
   Inngest's dev mode:

   ```bash
   dotnet run
   ```

2. Start the Inngest dev server and point it at the app. The `dotnet-inngest` tool reads the app's URL from
   `Properties/launchSettings.json`:

   ```bash
   dotnet tool install --global dotnet-inngest
   dotnet inngest dev
   ```

3. Send the event:

   ```bash
   curl -X POST http://localhost:5280/hello/Ada
   ```

   The run appears at http://localhost:8288 under Runs, with its step.

Outside the Development environment the app runs in cloud mode, which needs the keys of an Inngest app:
`INNGEST_EVENT_KEY` and `INNGEST_SIGNING_KEY`, set as environment variables or in user secrets.
