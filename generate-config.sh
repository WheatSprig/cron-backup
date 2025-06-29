#!/bin/sh
set -e

CONFIG_PATH="/config/rclone.conf"
mkdir -p "$(dirname "$CONFIG_PATH")"

# 默认 remote 名为 backup
RCLONE_REMOTE_NAMES="${RCLONE_REMOTE_NAMES:-backup}"

IFS=',' read -ra REMOTES <<< "$RCLONE_REMOTE_NAMES"

> "$CONFIG_PATH"  # 清空配置

for NAME in "${REMOTES[@]}"; do
  TYPE_VAR="RCLONE_${NAME}_TYPE"
  TYPE="${!TYPE_VAR}"

  echo "[$NAME]" >> "$CONFIG_PATH"
  echo "type=$TYPE" >> "$CONFIG_PATH"

  case "$TYPE" in
    s3)
      echo "provider=${!RCLONE_${NAME}_PROVIDER}" >> "$CONFIG_PATH"
      echo "env_auth=${!RCLONE_${NAME}_ENV_AUTH:-false}" >> "$CONFIG_PATH"
      echo "access_key_id=${!RCLONE_${NAME}_ACCESS_KEY_ID}" >> "$CONFIG_PATH"
      echo "secret_access_key=${!RCLONE_${NAME}_SECRET_ACCESS_KEY}" >> "$CONFIG_PATH"
      echo "region=${!RCLONE_${NAME}_REGION:-us-east-1}" >> "$CONFIG_PATH"
      echo "endpoint=${!RCLONE_${NAME}_ENDPOINT}" >> "$CONFIG_PATH"
      ;;
    sftp)
      echo "host=${!RCLONE_${NAME}_HOST}" >> "$CONFIG_PATH"
      echo "user=${!RCLONE_${NAME}_USER}" >> "$CONFIG_PATH"
      echo "pass=${!RCLONE_${NAME}_PASS}" >> "$CONFIG_PATH"
      echo "port=${!RCLONE_${NAME}_PORT:-22}" >> "$CONFIG_PATH"
      ;;
    drive)
      echo "client_id=${!RCLONE_${NAME}_CLIENT_ID}" >> "$CONFIG_PATH"
      echo "client_secret=${!RCLONE_${NAME}_CLIENT_SECRET}" >> "$CONFIG_PATH"
      echo "token=${!RCLONE_${NAME}_TOKEN}" >> "$CONFIG_PATH"
      ;;
    *)
      echo "❌ 不支持的 RCLONE 类型: $TYPE"
      exit 1
      ;;
  esac

  echo "" >> "$CONFIG_PATH"
done

echo "✅ rclone 配置生成完成："
cat "$CONFIG_PATH"
