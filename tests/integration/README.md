# Integration tests

真实容器集成测试：MinIO（S3）+ SFTP + cron-backup 镜像。

## 运行

```bash
bash tests/integration/run.sh
```

需要本机有 Docker 和 Docker Compose v2。脚本会：

- 构建 `cron-backup:integration-test`
- 启动 MinIO 和 SFTP
- 连续执行三次 `run --once`
- 校验 daily/weekly/monthly 数量
- 校验 S3 / SFTP 的 checksum（SFTP 走下载回读）
- 校验 `list --remote`
- 校验 `restore --latest --extract`
- 校验 `prune` dry-run / `--apply`
- 启动常驻容器，执行 `health`，再 `docker stop` 校验退出码

默认使用临时目录，结束后清理。保留现场：

```bash
KEEP_TEST_ROOT=1 TEST_ROOT=/tmp/cron-backup-it bash tests/integration/run.sh
```

## 镜像

默认：

- `minio/minio:latest`
- `atmoz/sftp:alpine`

如果 Docker Hub 拉取官方 MinIO 失败，脚本会自动回退到 `elestio/minio:latest`。
也可以显式覆盖：

```bash
MINIO_IMAGE=elestio/minio:latest \
SFTP_IMAGE=atmoz/sftp:alpine \
bash tests/integration/run.sh
```

测试用凭据全部是 `testuser/testpassword`，只用于本地或 CI 临时容器。
