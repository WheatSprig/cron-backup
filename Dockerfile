# syntax=docker/dockerfile:1

ARG RCLONE_VERSION=1.76.0

FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
ARG TARGETARCH
ARG TARGETVARIANT
WORKDIR /src

COPY Directory.Build.props global.json CronBackup.sln ./
COPY src/CronBackup.Core/CronBackup.Core.csproj src/CronBackup.Core/
COPY src/CronBackup.Cli/CronBackup.Cli.csproj src/CronBackup.Cli/
RUN dotnet restore src/CronBackup.Cli/CronBackup.Cli.csproj

COPY src ./src
RUN case "${TARGETARCH}/${TARGETVARIANT}" in \
        amd64/) RID=linux-x64 ;; \
        arm64/) RID=linux-arm64 ;; \
        arm/v7) RID=linux-arm ;; \
        *) echo "unsupported platform: ${TARGETARCH}/${TARGETVARIANT}" >&2; exit 1 ;; \
    esac && \
    dotnet publish src/CronBackup.Cli/CronBackup.Cli.csproj \
        -c Release \
        -r "$RID" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:PublishTrimmed=false \
        -p:InvariantGlobalization=false \
        -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-bookworm-slim AS runtime
ARG TARGETARCH
ARG TARGETVARIANT
ARG RCLONE_VERSION
ARG UID=1000
ARG GID=1000

ENV DEBIAN_FRONTEND=noninteractive \
    HOME=/tmp \
    XDG_CACHE_HOME=/tmp/.cache \
    CRONBACKUP_STATUS_FILE=/tmp/cron-backup/status.json \
    LOG_FILE=/var/log/cron-backup/backup.log

RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        bash \
        ca-certificates \
        curl \
        gzip \
        libicu72 \
        passwd \
        tar \
        tzdata \
        unzip && \
    case "${TARGETARCH}/${TARGETVARIANT}" in \
        amd64/) RCLONE_ARCH=amd64 ;; \
        arm64/) RCLONE_ARCH=arm64 ;; \
        arm/v7) RCLONE_ARCH=arm-v7 ;; \
        *) echo "unsupported platform: ${TARGETARCH}/${TARGETVARIANT}" >&2; exit 1 ;; \
    esac && \
    curl -fsSL \
        "https://downloads.rclone.org/v${RCLONE_VERSION}/rclone-v${RCLONE_VERSION}-linux-${RCLONE_ARCH}.zip" \
        -o /tmp/rclone.zip && \
    unzip -q /tmp/rclone.zip -d /tmp && \
    install -m 0755 "/tmp/rclone-v${RCLONE_VERSION}-linux-${RCLONE_ARCH}/rclone" /usr/local/bin/rclone && \
    rm -rf /tmp/rclone.zip "/tmp/rclone-v${RCLONE_VERSION}-linux-${RCLONE_ARCH}" && \
    apt-get purge -y curl unzip && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/* && \
    groupadd --gid "$GID" cronbackup && \
    useradd --uid "$UID" --gid "$GID" --create-home --home-dir /backup --shell /bin/bash cronbackup && \
    install -d -o cronbackup -g cronbackup /backup /config /var/log /var/log/cron-backup

COPY --from=build /app/publish /app

WORKDIR /backup
USER cronbackup

HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD ["/app/cron-backup", "health"]

ENTRYPOINT ["/app/cron-backup"]
CMD ["run"]
