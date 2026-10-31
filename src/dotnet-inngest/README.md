# dotnet-inngest

A .NET tool for the [Inngest .NET SDK](https://damikun.github.io/inngest-dotnet/). `dotnet inngest dev` runs the Inngest
dev server for the app in the current folder, without you installing the Inngest CLI first.

## Install

```bash
dotnet tool install --global dotnet-inngest
```

Or as a local tool, recorded in the tool manifest of a repository:

```bash
dotnet new tool-manifest
dotnet tool install dotnet-inngest
```

## Use

Start your app, then the dev server, each in its own terminal:

```bash
dotnet run
dotnet inngest dev
```

The first run downloads the release of the Inngest CLI for your system from GitHub, checks its SHA-256 and keeps it for
the later runs. It then runs `inngest dev` and passes on its output, its exit code and Ctrl+C.

- Without `-u`, the app's address is read from the first launch profile in `Properties/launchSettings.json` that runs
  the project and has an `http` address, plus `/api/inngest`. `-u <url>` names another, and `--no-discovery` (for a
  Connect worker) asks for none. `--project <path>` reads the launch settings of another project.
- Every other option goes to `inngest dev` as it is, as in `dotnet inngest dev --port 9000`.
  `dotnet inngest dev -- --help` shows the options of the CLI.
- The tool is pinned to one release of the CLI, whose SHA-256 checksums are built into the tool.
  `--cli-version <version>` runs another, checked against the `checksums.txt` of its release.
- When the CLI cannot be downloaded, the error ends with the `docker run` command for the same address.

## Documentation

[Templates and the dotnet inngest command](https://damikun.github.io/inngest-dotnet/guides/templates-and-tools/) has the
options, the folder the CLI is kept in, and what the tool does and does not do. The SDK itself is documented at
https://damikun.github.io/inngest-dotnet/.

## License

Apache License 2.0; the package includes the NOTICE file. The package holds no part of the Inngest CLI: it is Inngest's
software, which the tool downloads, under the license in the `LICENSE.md` of its release. inngest-dotnet is a community
project and is not affiliated with or endorsed by Inngest Inc. "Inngest" is a trademark of Inngest Inc.
