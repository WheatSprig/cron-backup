#!/bin/sh
set -e

# 归档文件名
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
ARCHIVE_NAME="backup_${TIMESTAMP}.tar.gz"
ARCHIVE_PATH="/tmp/${ARCHIVE_NAME}"

# 源目录（挂载点）
SRC_DIR="${BACKUP_SRC:-/data}"

echo "📦 正在打包目录: $SRC_DIR"
tar -czf "$ARCHIVE_PATH" -C "$SRC_DIR" .

echo "✅ 本地归档完成: $ARCHIVE_PATH"

# 上传到所有 remote
RCLONE_REMOTE_NAMES="${RCLONE_REMOTE_NAMES:-backup}"
IFS=',' read -ra REMOTES <<< "$RCLONE_REMOTE_NAMES"

for NAME in "${REMOTES[@]}"; do
  DEST="${NAME}:${RCLONE_PATH:-/backups}/${ARCHIVE_NAME}"
  echo "🚀 上传到 $DEST"
  rclone copy "$ARCHIVE_PATH" "$DEST" --config="/config/rclone.conf" --progress
done

# 本地清理
echo "🧹 清理本地归档: $ARCHIVE_PATH"
rm -f "$ARCHIVE_PATH"

echo "✅ 所有上传任务完成"
