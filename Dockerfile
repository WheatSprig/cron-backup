FROM debian:12-slim

ENV DEBIAN_FRONTEND=noninteractive

# Useing chinese mirror
RUN sed -i "s@http://deb.debian.org@http://mirrors.aliyun.com@g" /etc/apt/sources.list && rm -Rf /var/lib/apt/lists/* && apt-get update

# Install Packages
RUN \
    apt-get update && \
    apt install -y --no-install-recommends curl && \
    curl -fsSL https://rclone.org/install.sh | bash - && \
    apt-get -y --auto-remove purge curl && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /backup

# RClone Config file
ENV RCLONE_CONFIG=/backup/rclone/rclone.conf

# Create the default user
ARG UID=1000
ARG GID=1000
SHELL ["/bin/bash", "-o", "pipefail", "-c"]
RUN \
    apt-get update && \
    echo "Etc/UTC" > /etc/localtime && \
    apt-get -y --no-install-recommends install whois wget && \
    addgroup --gid $GID maisui && \
    useradd -m -u $UID -g $GID -d /backup maisui && \
    echo "maisui:$(head /dev/urandom | tr -dc A-Za-z0-9 | head -c 24 | mkpasswd -s -m sha-256)" | chpasswd && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

COPY --chown=maisui:maisui entrypoint.sh /
COPY --chown=maisui:maisui generate-config.sh /backup/
COPY --chown=maisui:maisui backup.sh /backup/

RUN chmod +x /entrypoint.sh /backup/generate-config.sh /backup/backup.sh

# Set the run user
USER maisui

ENTRYPOINT /entrypoint.sh
