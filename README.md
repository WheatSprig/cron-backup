
# 📦 cron-backup

使用 Docker 构建的通用备份工具，支持定时任务（Cron）、固定间隔（Loop）和一次性执行（Once）。内置 `rclone`，可将数据上传至多种远程存储（S3、SFTP、Google Drive 等）。

---

## 🚀 功能特性

* ✅ 支持三种运行模式：定时、循环、一次性
* ✅ 支持多种 `rclone` 远程类型（S3 / SFTP / Google Drive 等）
* ✅ 支持多 remote 同时上传
* ✅ 仅使用 shell + rclone，体积小，行为透明
* ✅ 支持日志输出至控制台或文件
* ✅ 支持 UID/GID 控制容器内权限

---

## 🧱 使用前提

* 已安装 [Docker](https://docs.docker.com/)
* 熟悉基本的 `rclone` 概念（remote / 配置）

---

## 📁 项目结构

```plaintext
.
├── backup.sh                       # 主备份逻辑脚本
├── entrypoint.sh                   # 容器启动入口，负责调度与日志
├── generate-config.sh              # 自动生成 rclone.conf
├── Dockerfile
├── docker-compose.examples.yml     # 推荐部署方式
└── config/                         # 持久化 rclone 配置目录
```

---

## ⚙️ 配置方式

### ✅ 主要环境变量

| 变量名                   | 含义                 | 示例              |
| --------------------- | ------------------ | --------------- |
| `CRON_SCHEDULE`       | 使用 Cron 表达式执行备份    | `0 2 * * *`     |
| `INTERVAL_SECONDS`    | 每隔 N 秒执行一次备份       | `3600`          |
| `BACKUP_SRC`          | 要备份的本地目录（容器内）      | `/backup/data`  |
| `RCLONE_REMOTE_NAMES` | 多 remote 名称，用逗号分隔  | `backup1,sftp1` |
| `RCLONE_PATH`         | 上传至远程的子目录          | `/daily`        |
| `LOG_TARGET`          | `console` 或 `file` | `console`       |
| `UID`、`GID`           | 容器运行的用户            | `1000`          |

### 🔐 按 remote 类型配置（以 backup 为例）

#### S3

```env
RCLONE_backup_TYPE=s3
RCLONE_backup_ACCESS_KEY_ID=your_key
RCLONE_backup_SECRET_ACCESS_KEY=your_secret
RCLONE_backup_REGION=us-east-1
RCLONE_backup_BUCKET=your_bucket
RCLONE_backup_ENDPOINT=https://s3.example.com
RCLONE_backup_PROVIDER=AWS
RCLONE_backup_ACL=private
```

#### SFTP

```env
RCLONE_backup_TYPE=sftp
RCLONE_backup_HOST=sftp.example.com
RCLONE_backup_PORT=22
RCLONE_backup_USER=username
RCLONE_backup_PASS=password
```

#### Google Drive

```env
RCLONE_backup_TYPE=drive
RCLONE_backup_CLIENT_ID=xxx
RCLONE_backup_CLIENT_SECRET=yyy
RCLONE_backup_TOKEN=zzz
```

---

## 🐳 示例：docker-compose.yml

```yaml
version: "3.8"
services:
  cron-backup:
    image: cron-backup
    build:
      context: .
    volumes:
      - ./data:/backup/data
      - ./config:/config
    environment:
      - CRON_SCHEDULE=0 2 * * *
      - RCLONE_REMOTE_NAMES=backup
      - RCLONE_backup_TYPE=s3
      - RCLONE_backup_ACCESS_KEY_ID=xxx
      - RCLONE_backup_SECRET_ACCESS_KEY=yyy
      - RCLONE_backup_REGION=us-east-1
      - LOG_TARGET=console
      - UID=1000
      - GID=1000
```

---

## 🔁 三种运行模式说明

| 模式    | 描述             | 启动方式                                |
| ----- | -------------- | ----------------------------------- |
| 定时模式  | 按 Cron 表达式定时执行 | 设置 `CRON_SCHEDULE`                  |
| 间隔模式  | 每隔一段时间执行一次     | 设置 `INTERVAL_SECONDS`               |
| 一次性模式 | 容器启动后执行一次立即退出  | 配置 `command: ["/backup/backup.sh"]` |

---

## 📝 日志说明

* 默认输出到控制台
* 设置 `LOG_TARGET=file` 时，输出路径为 `/var/log/backup.log`

---

## 🧪 快速测试（一次性备份）

```bash
docker compose -f docker-compose.yml run --rm cron-backup
```

---

## 🧩 许可证

MIT License
