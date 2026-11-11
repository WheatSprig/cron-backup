# Changelog

All notable changes to this project are documented here.

## [2.1.0] - 2026-11-11

### Added

- `defaults.max_parallel` 限制调度器同时运行的 job 数，默认 1。
- `archive.timeout_seconds` 配置 tar 超时，默认 21600 秒。
- `LOG_MAX_SIZE_MB` / `LOG_MAX_FILES` 文件日志轮转。
- `known_hosts_file` SFTP 主机密钥校验配置。
- `linux/arm/v7` 多架构发布。
- Security workflow：NuGet 漏洞、Dockerfile lint、Trivy 配置扫描。
- Release workflow：tag 时自动创建 GitHub Release。

### Changed

- 归档文件名加入毫秒：`backup_yyyyMMdd_HHmmss_fff.tar.gz`，避免同秒覆盖。
- CLI 支持 `--config=path`、`--job=name` 等等号写法。
- checksum 在远端不提供 hash 时下载回读计算。
- prune 与 run 共用 job 锁，支持 `CRONBACKUP_LOCK_DIR`。
- 启动时清理 24 小时前的临时归档与 rclone.conf。

### Fixed

- SFTP chroot 场景下推荐使用相对 root。
- 集成测试恢复目录权限。
- 健康状态版本号改为从程序集读取。

## [2.0.0] - 2026-11-11

### Added

- 多 job YAML 配置、`${VAR}` 插值、v1 环境变量隐式 job。
- S3 / SFTP / local / rclone 存储后端。
- daily / weekly / monthly 分层保留与服务端复制提升。
- 上传 size / checksum 校验。
- pre/post hooks、webhook/ntfy/healthchecks 通知。
- flock 防重入、重试退避、内置 Cronos 调度。
- `run` / `list` / `validate` / `plan` / `prune` / `restore` / `health` / `version`。
- MinIO + SFTP 集成测试 harness。
- self-contained Docker 镜像，支持 amd64/arm64。
