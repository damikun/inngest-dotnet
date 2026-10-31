# Inngest.Templates

`dotnet new` templates for the [Inngest .NET SDK](https://damikun.github.io/inngest-dotnet/): a project with a typed
event, a durable function, and dev mode in its Development settings.

| Template | Makes |
|---|---|
| `inngest-web` | An ASP.NET Core app that serves the function with `MapInngest` and has an endpoint that sends the event that starts it. |
| `inngest-worker` | A worker that runs the same function over Connect with `AddConnect`, with no endpoint to expose. |

## Use

```bash
dotnet new install Inngest.Templates
dotnet new inngest-web -n Shop
cd Shop
dotnet run
```

The options of both templates are `-n` for the name, which also gives the app id, `-f` for the framework, `net10.0`
(the default) or `net8.0`, and `--no-restore`. `inngest-web` also has `--http-port`.

The new project references the Inngest packages at the version of this template package. To run the Inngest dev server
for it, install the [`dotnet-inngest`](https://www.nuget.org/packages/dotnet-inngest/) tool and run
`dotnet inngest dev`.

## Documentation

[Templates and the dotnet inngest command](https://damikun.github.io/inngest-dotnet/guides/templates-and-tools/) describes
the files the templates make and their options. The SDK itself is documented at
https://damikun.github.io/inngest-dotnet/.

## License

Apache License 2.0; the package includes the NOTICE file. inngest-dotnet is a community project and is not affiliated
with or endorsed by Inngest Inc. "Inngest" is a trademark of Inngest Inc.
