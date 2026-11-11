# cron-backup

通用备份工具 v2：多 job、分层保留、上传校验、通知、钩子和内置调度。
存储通过 rclone 适配 S3 / SFTP / 本地目录，镜像里不需要 crond。

> v1 的 `backup.sh` / `entrypoint.sh` / `generate-config.sh` 已从 `main` 删除，
> 需要旧版请使用 git tag `v1`。

---

## 快速开始

### 1. 准备配置

```bash
cp samples/config.example.yaml config/config.yaml
```

编辑 `config/config.yaml`。凭据不要写进文件，使用 `${VAR}`：

```yaml
jobs:
  - name: photos
    schedule: "0 2 * * *"
    source:
      path: /backup/data
    destinations:
      - name: s3
        kind: s3
        provider: Minio
        endpoint: https://s3.example.com
        region: us-east-1
        bucket: backups
        root: photos
        access_key_id: ${S3_ACCESS_KEY_ID}
        secret_access_key: ${S3_SECRET_ACCESS_KEY}
```

### 2. 启动容器

```yaml
services:
  backup:
    image: ghcr.io/wheatsprig/cron-backup:2
    restart: unless-stopped
    volumes:
      - ./data:/backup/data:ro
      - ./config:/config:ro
    environment:
      TZ: Asia/Shanghai
      S3_ACCESS_KEY_ID: ${S3_ACCESS_KEY_ID}
      S3_SECRET_ACCESS_KEY: ${S3_SECRET_ACCESS_KEY}
```

```bash
docker compose up -d
```

容器默认执行 `cron-backup run`：先立即执行一轮，再按 `schedule` 常驻。
只跑一次用：

```bash
docker compose run --rm backup run --once
```

---

## 运行模式

### config.yaml

完整配置见 [`samples/config.example.yaml`](samples/config.example.yaml)。

配置发现顺序：

```text
--config PATH
  > CRONBACKUP_CONFIG（文件路径）
  > CRONBACKUP_CONFIG_YAML（内联内容）
  > /config/config.yaml
  > ./config.yaml
  > v1 环境变量隐式 job
```

`CRONBACKUP_CONFIG` 和 `CRONBACKUP_CONFIG_YAML` 不能同时设置。

### 不挂配置文件：v1 环境变量

没有 `config.yaml` 时，程序会从 v1 变量生成一个隐式 job：

```yaml
environment:
  BACKUP_SRC: /backup/data
  CRON_SCHEDULE: "0 2 * * *"
  RCLONE_REMOTE_NAMES: backup
  RCLONE_backup_TYPE: s3
  RCLONE_backup_PROVIDER: Minio
  RCLONE_backup_ENDPOINT: https://s3.example.com
  RCLONE_backup_BUCKET: backups
  RCLONE_backup_ACCESS_KEY_ID: ${S3_ACCESS_KEY_ID}
  RCLONE_backup_SECRET_ACCESS_KEY: ${S3_SECRET_ACCESS_KEY}
```

隐式 job 默认 **不启用 retention**，升级不会自动删除旧备份。

### 不挂配置文件：内联 YAML

一两个 job 可以把完整配置放进环境变量：

```yaml
environment:
  CRONBACKUP_CONFIG_YAML: |
    version: 1
    jobs:
      - name: photos
        schedule: "0 2 * * *"
        source:
          path: /backup/data
        destinations:
          - name: s3
            kind: s3
            provider: Minio
            bucket: backups
            root: photos
            access_key_id: $${S3_ACCESS_KEY_ID}
            secret_access_key: $${S3_SECRET_ACCESS_KEY}
```

Docker Compose 会先做自己的 `$` 插值，所以配置里的占位符要写成 `$${VAR}`。

---

## 配置语义

优先级：

```text
CLI > job(YAML) > env 默认 > 内置
```

env 只负责凭据和全局默认：

| 变量 | 含义 |
|---|---|
| `TZ` | 时区 |
| `LOG_TARGET` | `console` 或 `file` |
| `LOG_FILE` | 文件日志路径，默认 `/var/log/cron-backup/backup.log` |
| `DRY_RUN` | `true` 时不产生任何远端变更 |
| `NOTIFY_URL` | 默认 webhook |

支持 `${VAR}`、`${VAR:-default}` 和 `$${...}` 转义。不支持 `$VAR`、命令替换或递归插值。
缺失变量会直接报配置错误。

### destination

```yaml
- name: s3-main
  kind: s3              # s3 | sftp | local | rclone
  provider: Minio
  endpoint: https://s3.example.com
  region: us-east-1
  bucket: backups
  root: photos
  access_key_id: ${S3_ACCESS_KEY_ID}
  secret_access_key: ${S3_SECRET_ACCESS_KEY}
  acl: private
  no_check_bucket: true
```

`kind: rclone` 是逃生口，用于程序没有专门建模的 rclone 后端：

```yaml
- name: gdrive
  kind: rclone
  type: drive
  root: backups/photos
  options:
    client_id: ${GDRIVE_CLIENT_ID}
    client_secret: ${GDRIVE_CLIENT_SECRET}
    token: ${GDRIVE_TOKEN}
  obscure: [client_secret]
```

### retention

```yaml
defaults:
  retention:
    daily: 7
    weekly: 4
    monthly: 6
    max_delete: 20

jobs:
  - name: photos
    retention:
      daily: 14
    destinations:
      - name: s3
        kind: s3
        # ...
        retention:
          daily: 30
          max_delete: 50
```

解析顺序：`defaults < job < destination`，逐字段覆盖。

- 正整数：保留最新 N 个对象；
- `0`：关闭该 tier，不提升也不清理；
- `enabled: false`：当前作用域关闭；job 上关闭是硬关闭；
- 不配置 retention：不提升、不清理；
- 清理按对象 `Modified` 倒序，不解析文件名；
- 候选数超过 `max_delete` 时整批中止；
- `prune` 手动执行默认 dry-run，加 `--apply` 才删除。

远端布局：

```text
<root>/daily/backup_20261111_020000.tar.gz
<root>/weekly/backup_20261108_020000.tar.gz
<root>/monthly/backup_20261101_020000.tar.gz
```

周日和每月 1 号做服务端复制提升；错过时按最近 7/31 天补跑，目标 key 已存在则跳过。

### hooks

```yaml
hooks:
  timeout_seconds: 300
  pre:
    - run: /backup/hooks/db-dump.sh
      args: ["--full"]
      timeout_seconds: 600
      env:
        PGPASSWORD: ${PGPASSWORD}
  post:
    - run: /backup/hooks/cleanup-local.sh
```

pre/post 是备份容器内部的子进程，默认不经过 shell。需要 shell 时显式：

```yaml
- run: /bin/sh
  args: ["-c", "pg_dump ... | gzip > /backup/data/db.sql.gz"]
```

pre 失败会中止本次 job；post 始终执行。dry-run 默认不执行 hook。
需要调用其他容器时，优先使用共享卷或 webhook，不要挂 `docker.sock`。

### notify

```yaml
notify:
  - kind: webhook
    url: ${WEBHOOK_URL}
  - kind: ntfy
    url: ${NTFY_URL}
    topic: backups
  - kind: healthchecks
    url: ${HEALTHCHECKS_URL}
```

支持 webhook / ntfy / healthchecks。通知失败不会让备份 job 失败。

---

## 命令

```text
cron-backup run      [--job NAME] [--once] [--dry-run]
cron-backup plan     [--job NAME] [--json]
cron-backup validate [--online] [--json]
cron-backup list     [--job NAME] [--remote] [--tier daily|weekly|monthly] [--json]
cron-backup prune    [--job NAME] [--dry-run] [--apply]
cron-backup restore  --job NAME (--latest | --key KEY) --to DIR [--extract]
cron-backup health   [--strict] [--json]
cron-backup version  [--json]
```

常用：

```bash
# 只验证配置和 ${VAR}
cron-backup validate --config /config/config.yaml

# 查看生效配置和 retention，不访问远端
cron-backup plan --config /config/config.yaml

# 查看远端已有对象
cron-backup list --remote --config /config/config.yaml

# 预演清理
cron-backup prune --config /config/config.yaml

# 实际清理
cron-backup prune --config /config/config.yaml --apply

# 恢复最新 daily 归档并解压
cron-backup restore --job photos --latest --to /restore --extract
```

退出码：`0` 成功，`1` 参数错误，`2` 配置错误，`3` job 失败，`4` 已被锁占用。

---

## healthcheck

`run` 常驻模式会写 `/tmp/cron-backup/status.json`：

- 配置是否有效
- 调度器是否运行
- 心跳时间
- 最近一次 job 结果
- 连续失败次数

```bash
cron-backup health
```

默认只在配置无效、调度器停止或心跳超过 90 秒时失败。
`--strict` 会额外把最近一次失败或连续失败 3 次判为 unhealthy。

Dockerfile 已内置：

```dockerfile
HEALTHCHECK CMD ["/app/cron-backup", "health"]
```

备份失败默认不会让 Docker 反复重启容器；请用 ntfy / webhook / healthchecks.io 做业务告警。

---

## Docker 与架构

- 多阶段构建，self-contained 单文件发布
- 运行时包含 rclone、GNU tar、bash、tzdata、ICU
- 没有 crond
- 默认支持 `linux/amd64`、`linux/arm64`；`linux/arm/v7` 可在 buildx 中验证后发布

```bash
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t ghcr.io/wheatsprig/cron-backup:2 .
```

---

## 从 v1 迁移

- 旧脚本已删除，v1 在 git tag `v1`。
- v1 的 `rclone copy <file> <file-path>` 会把目标当目录，实际多套一层；v2 使用 `copyto`，归档直接落在 `<root>/<archive>`。
- compose 里的 `RCLONE_backup_PATH` 在 v1 没有生效；v2 同时支持全局 `RCLONE_PATH` 和 per-remote `RCLONE_<NAME>_PATH`。
- 没有 config.yaml 时仍按 v1 env 生成隐式 job，但 **不会自动启用 retention**。
- `rclone.conf` 不再手工维护，由程序运行时渲染到临时目录并在退出时删除。

---

## 开发

```bash
dotnet build
dotnet test
dotnet run --project src/CronBackup.Cli -- validate --config samples/config.example.yaml
dotnet run --project src/CronBackup.Cli -- plan --config samples/config.example.yaml
```

仓库约定见 [`AGENTS.md`](AGENTS.md)。

---

## License

MIT
