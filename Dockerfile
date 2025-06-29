FROM debian:12-slim

ENV DEBIAN_FRONTEND=noninteractive

# 安装所需组件并安装 rclone
RUN apt-get update && \
    apt install -y --no-install-recommends curl ca-certificates gnupg cron unzip && \
    curl -fsSL https://rclone.org/install.sh | bash - && \
    apt-get purge -y curl unzip && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /backup

# RClone 配置文件路径
ENV RCLONE_CONFIG=/config/rclone.conf

# 创建默认用户
ARG UID=1000
ARG GID=1000
SHELL ["/bin/bash", "-o", "pipefail", "-c"]
RUN apt-get update && \
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

USER maisui

HEALTHCHECK CMD [ ! -f /tmp/last_backup_failed ]

ENTRYPOINT ["/entrypoint.sh"]
