#!/bin/sh
set -e

echo "🔧 正在生成 rclone 配置..."
sh /generate-config.sh

# 默认路径，方便可替换
BACKUP_SCRIPT="/backup.sh"

# 日志目标，默认为控制台
LOG_FILE="/var/log/backup.log"

if [ "$LOG_TARGET" = "file" ]; then
  exec > >(tee -a "$LOG_FILE") 2>&1
  echo "📂 日志写入: $LOG_FILE"
else
  echo "🖥️ 日志输出到控制台"
fi


# 判断是否设置了 CRON_SCHEDULE 或 INTERVAL_SECONDS
if [ -n "$CRON_SCHEDULE" ]; then
  echo "🕒 启用 cron 调度: $CRON_SCHEDULE"

  echo "$CRON_SCHEDULE root $BACKUP_SCRIPT >> /var/log/cron.log 2>&1" > /etc/crontabs/root

  echo "📦 立即执行一次备份..."
  sh "$BACKUP_SCRIPT"

  echo "🚀 启动 crond"
  crond -f
elif [ -n "$INTERVAL_SECONDS" ]; then
  echo "🔁 启用循环间隔模式，每 $INTERVAL_SECONDS 秒执行一次备份"

  while true; do
    echo "📦 执行备份 at $(date)"
    sh "$BACKUP_SCRIPT"
    sleep "$INTERVAL_SECONDS"
  done
else
  echo "⚠️ 未设置 CRON_SCHEDULE 或 INTERVAL_SECONDS，默认执行一次备份"
  sh "$BACKUP_SCRIPT"
fi
