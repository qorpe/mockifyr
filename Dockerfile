# Mockifyr — single image that serves the mock engine + admin API and the embedded dashboard.
# The dashboard is reachable at /__mockifyr; every other path is the mock-serving surface.
#
# Multi-arch by cross-compilation: the build stages run natively on the build platform (fast) and
# target the requested arch, so an arm64 image is produced without slow QEMU emulation of the SDK.

# ---- Stage 1: build the dashboard (static output, arch-independent — build natively) ----
FROM --platform=$BUILDPLATFORM node:22-alpine AS ui
WORKDIR /ui
RUN corepack enable
COPY ui/package.json ui/pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile
COPY ui/ ./
RUN pnpm build:embedded

# ---- Stage 2: publish the .NET host (cross-compiled to $TARGETARCH) ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/ ./src/
# Map Docker's TARGETARCH (amd64/arm64) to .NET's --arch (x64/arm64), then cross-publish.
RUN case "$TARGETARCH" in \
      amd64) ARCH=x64 ;; \
      arm64) ARCH=arm64 ;; \
      *) ARCH="$TARGETARCH" ;; \
    esac; \
    dotnet publish src/Mockifyr.Server/Mockifyr.Server.csproj -c Release -a "$ARCH" -o /app

# ---- Stage 3: runtime (pulled for the target arch automatically) ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# git backs the optional --git-remote sync (ADR 0007); hosts without the flag never invoke it.
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
COPY --from=ui /ui/dist ./dashboard
EXPOSE 8080
# The dashboard is served under /__mockifyr. --root-dir /work is baked in so no run command needs it:
# stubs persist to /work/mappings, environment configuration to /work/environments, response body
# files to /work/__files and gRPC descriptors to /work/grpc. Mount a volume at /work (bind or named)
# to keep all of it — mounting only /work/mappings loses the rest on recreate (issue #181). A
# datastore flag (--postgres/--redis/--litedb) passed at run time takes precedence over the file store.
ENTRYPOINT ["dotnet", "Mockifyr.Server.dll", "--port", "8080", "--dashboard", "/app/dashboard", "--root-dir", "/work"]
