FROM debian:12-slim

ENV DEBIAN_FRONTEND=noninteractive

# Install Packages
RUN \
    apt-get update && \
    apt install rclone -y && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# RClone Config file
ENV RCLONE_CONFIG=/rclone.conf

COPY entrypoint.sh /
COPY generate-config.sh /
COPY backup.sh /

RUN chmod +x /entrypoint.sh /generate-config.sh /backup.sh

ENTRYPOINT /entrypoint.sh
