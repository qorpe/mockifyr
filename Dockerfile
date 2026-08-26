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

# Apache-2.0 §4(a) and §4(d): a redistributor must pass on the License and the NOTICE attributions,
# and shipping this image IS redistribution — so the artefact has to carry them rather than leaving
# every consumer to find them in the repository. /licenses is the conventional location: it is where
# the OCI/Red Hat container guidelines put them and where OpenShift tooling looks (#395).
COPY LICENSE NOTICE /licenses/

# Unprivileged by default (#241). The engine never needs to write outside its work directory, so it
# runs as a dedicated non-root user. /work is owned by that user AND group-writable with the group
# set to root (GID 0): OpenShift's restricted SCC assigns an arbitrary UID with GID 0, so this is
# what lets the file store work under both `docker run` and OpenShift without a chown at startup.
RUN groupadd --system --gid 1001 mockifyr \
    && useradd --system --uid 1001 --gid 0 --home-dir /app --shell /usr/sbin/nologin mockifyr \
    && mkdir -p /work \
    && chown -R 1001:0 /app /work \
    && chmod -R g=u /app /work
USER 1001:0

EXPOSE 8080

# Static OCI metadata, so `docker inspect` answers "what is this and where did it come from" even for
# a local build. The release workflow's metadata-action overrides these with the tag's real values.
#
# `version` is set explicitly rather than left alone: the aspnet base image carries its own
# org.opencontainers.image.version (the Ubuntu release, "24.04"), and an inherited label is still an
# inherited label — `docker inspect` on a local build reported the distro version as if it were the
# product's. A dev default is honest; the release run replaces it.
ARG VERSION=0.0.0-dev
LABEL org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.title="Mockifyr" \
      org.opencontainers.image.description="API mock engine, admin API and dashboard in one image." \
      org.opencontainers.image.licenses="Apache-2.0" \
      org.opencontainers.image.source="https://github.com/qorpe/mockifyr" \
      org.opencontainers.image.documentation="https://mockifyr.qorpe.com"

# Container-level health (#241): the probe path stays reachable without credentials even when admin
# auth is on (#218), so this works on a secured host too.
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD ["dotnet", "Mockifyr.Server.dll", "--healthcheck"]
# The dashboard is served under /__mockifyr. --root-dir /work is baked in so no run command needs it:
# stubs persist to /work/mappings, environment configuration to /work/environments, response body
# files to /work/__files and gRPC descriptors to /work/grpc. Mount a volume at /work (bind or named)
# to keep all of it — mounting only /work/mappings loses the rest on recreate (issue #181). A
# datastore flag (--postgres/--redis/--litedb) passed at run time takes precedence over the file store.
ENTRYPOINT ["dotnet", "Mockifyr.Server.dll", "--port", "8080", "--dashboard", "/app/dashboard", "--root-dir", "/work"]
