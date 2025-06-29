#!/bin/bash
set -e

CONFIG_PATH="/config/rclone.conf"
mkdir -p "$(dirname "$CONFIG_PATH")"

# 默认 remote 名为 backup
RCLONE_REMOTE_NAMES="${RCLONE_REMOTE_NAMES:-backup}"

IFS=',' read -ra REMOTES <<< "$RCLONE_REMOTE_NAMES"

> "$CONFIG_PATH"  # 清空配置

for NAME in "${REMOTES[@]}"; do
  TYPE_VAR="RCLONE_${NAME}_TYPE"
  eval "TYPE=\$$TYPE_VAR"

  echo "[$NAME]" >> "$CONFIG_PATH"
  echo "type=$TYPE" >> "$CONFIG_PATH"

  case "$TYPE" in
    s3)
      eval "provider=\$RCLONE_${NAME}_PROVIDER"
      eval "env_auth=\${RCLONE_${NAME}_ENV_AUTH:-false}"
      eval "access_key_id=\$RCLONE_${NAME}_ACCESS_KEY_ID"
      eval "secret_access_key=\$RCLONE_${NAME}_SECRET_ACCESS_KEY"
      eval "region=\${RCLONE_${NAME}_REGION:-us-east-1}"
      eval "endpoint=\$RCLONE_${NAME}_ENDPOINT"
      eval "acl=\${RCLONE_${NAME}_ACL:-private}"
      eval "no_check_bucket=\${RCLONE_${NAME}_NO_CHECK_BUCKET:-true}"

      echo "provider=$provider" >> "$CONFIG_PATH"
      echo "env_auth=$env_auth" >> "$CONFIG_PATH"
      echo "access_key_id=$access_key_id" >> "$CONFIG_PATH"
      echo "secret_access_key=$secret_access_key" >> "$CONFIG_PATH"
      echo "region=$region" >> "$CONFIG_PATH"
      echo "endpoint=$endpoint" >> "$CONFIG_PATH"
      echo "acl=$acl" >> "$CONFIG_PATH"
      echo "no_check_bucket=$no_check_bucket" >> "$CONFIG_PATH"
      ;;
    sftp)
      eval "host=\$RCLONE_${NAME}_HOST"
      eval "user=\$RCLONE_${NAME}_USER"
      eval "pass=\$RCLONE_${NAME}_PASS"
      eval "port=\${RCLONE_${NAME}_PORT:-22}"

      echo "host=$host" >> "$CONFIG_PATH"
      echo "user=$user" >> "$CONFIG_PATH"
      echo "pass=$pass" >> "$CONFIG_PATH"
      echo "port=$port" >> "$CONFIG_PATH"
      ;;
    drive)
      eval "client_id=\$RCLONE_${NAME}_CLIENT_ID"
      eval "client_secret=\$RCLONE_${NAME}_CLIENT_SECRET"
      eval "token=\$RCLONE_${NAME}_TOKEN"

      echo "client_id=$client_id" >> "$CONFIG_PATH"
      echo "client_secret=$client_secret" >> "$CONFIG_PATH"
      echo "token=$token" >> "$CONFIG_PATH"
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
