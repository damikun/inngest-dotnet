# syntax=docker/dockerfile:1

# The sample app as a container image. The build context is the repository root:
#
#   docker build -f samples/Inngest.Sample/Dockerfile -t inngest-dotnet-sample .
#
# The SDK stage runs on the build machine's platform and publishes for the target one, so an arm64 machine builds
# an amd64 image (docker build --platform linux/amd64) without emulating the SDK.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Project files first: the restore layer is reused until a project or package version changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Directory.Build.props src/.editorconfig src/
COPY src/Inngest/Inngest.csproj src/Inngest/
COPY src/Inngest.AspNetCore/Inngest.AspNetCore.csproj src/Inngest.AspNetCore/
COPY samples/Inngest.Sample/Inngest.Sample.csproj samples/Inngest.Sample/
RUN --mount=type=cache,id=inngest-dotnet-nuget,target=/root/.nuget/packages \
    dotnet restore samples/Inngest.Sample/Inngest.Sample.csproj -a $TARGETARCH

COPY README.md LICENSE NOTICE ./
COPY src/Inngest/ src/Inngest/
COPY src/Inngest.AspNetCore/ src/Inngest.AspNetCore/
COPY samples/Inngest.Sample/ samples/Inngest.Sample/
RUN --mount=type=cache,id=inngest-dotnet-nuget,target=/root/.nuget/packages \
    dotnet publish samples/Inngest.Sample/Inngest.Sample.csproj -c Release -a $TARGETARCH --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# The image's built-in non-root user. Kestrel listens on 8080 (ASPNETCORE_HTTP_PORTS), which needs no privileges.
USER $APP_UID
EXPOSE 8080

# The image has no curl, so the probe speaks HTTP over bash's /dev/tcp. It asks for the root path, which the sample
# does not serve: any answer below 500 shows that Kestrel and the request pipeline work. It stays away from
# /api/inngest because in cloud mode the SDK refuses unsigned requests there and logs a warning for each one.
HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --start-interval=1s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET / HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3 && timeout 4 head -n 1 <&3 | grep -Eq '^HTTP/1\\.1 [234][0-9][0-9] '"]

ENTRYPOINT ["dotnet", "Inngest.Sample.dll"]
