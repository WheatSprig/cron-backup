#!/bin/bash
set -e

# 归档文件名
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
ARCHIVE_NAME="backup_${TIMESTAMP}.tar.gz"
ARCHIVE_PATH="/tmp/${ARCHIVE_NAME}"

# 源目录（挂载点）
# 如果没有设置 BACKUP_SRC，则默认为 /backup/data
# 如果设置了 BACKUP_SRC，则使用该变量
# 这样可以在运行时通过环境变量覆盖默认路径
SRC_DIR="${BACKUP_SRC:-/backup/data}"

echo "📦 正在打包目录: $SRC_DIR"
tar -czf "$ARCHIVE_PATH" -C "$SRC_DIR" .

echo "✅ 本地归档完成: $ARCHIVE_PATH"

# 上传到所有 remote
RCLONE_REMOTE_NAMES="${RCLONE_REMOTE_NAMES:-backup}"
IFS=',' read -ra REMOTES <<< "$RCLONE_REMOTE_NAMES"

for NAME in "${REMOTES[@]}"; do
  # 获取 remote 类型（为了兼容不同的存储添加特定的逻辑） 
  TYPE_VAR="RCLONE_${NAME}_TYPE"
  eval "TYPE=\$$TYPE_VAR"

  # 默认路径（对所有类型都生效）
  REMOTE_PATH="${RCLONE_PATH:-/backups}"
  # 先判断是否为空或根路径，再清理
  # 如果没传 RCLONE_PATH，用 /backups，去掉斜杠后是 backups
  # 如果传空或 /，最后变成空字符串，不拼多余路径
  # 其他正常路径去除多余斜杠
  if [ -z "$REMOTE_PATH" ] || [ "$REMOTE_PATH" = "/" ]; then
    REMOTE_PATH=""
  else
    # 去除开头结尾斜杠
    REMOTE_PATH=$(echo "$REMOTE_PATH" | sed 's#^/*##; s#/*$##')
  fi

  if [ "$TYPE" = "s3" ]; then
    BUCKET_VAR="RCLONE_${NAME}_BUCKET"
    eval "BUCKET=\${$BUCKET_VAR:-backups}"
    DEST="${NAME}:${BUCKET}/${REMOTE_PATH}/${ARCHIVE_NAME}"
  else
    DEST="${NAME}:/${REMOTE_PATH}/${ARCHIVE_NAME}"
  fi

  # 清理双斜杠（//）的情况
  DEST="$(echo "$DEST" | sed 's#//*#/#g' | sed 's#:/#:#')"

  echo "🚀 上传到 :"
  echo "    🔹 Remote: $NAME"
  echo "    🔹 类型: $TYPE"
  echo "    🔹 目标路径: ${DEST}"

  rclone copy "$ARCHIVE_PATH" "$DEST" --config="/config/rclone.conf" --progress
done

# 本地清理
echo "🧹 清理本地归档: $ARCHIVE_PATH"
rm -f "$ARCHIVE_PATH"

echo "✅ 所有上传任务完成"
