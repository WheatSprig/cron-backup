---
project: cron-backup
description: "通用备份工具 v2：多 job、分层保留、rclone 存储适配、内置调度"
target_framework: ".NET 8 / C# 12"
build_sdk: ".NET 8.0.x（global.json 固定 8.0.409）"
tools: [YamlDotNet, Cronos, xUnit, rclone]
---

# cron-backup 仓库规范

本文件面向要改这份代码的人和自动化助手，约定仓库边界、配置语义、保留策略、
测试方式与提交纪律。**若本文与实际代码不一致，以代码为准，并顺手把本文改掉。**

当前状态：v2 的配置、执行管线、rclone 后端、调度、prune/restore/health 与
Dockerfile 已落地；v1 的三个 shell 脚本已删除，只能从 `v1` tag 取回。
v2 的目录结构、配置模型与剩余工作见 `.agents/plans/` 下的计划文件（不提交）。

---

## 1. 关键不变量

这几条一旦被破坏，问题往往不会立刻编译失败，而是在真实备份或恢复时才暴露：

1. **老部署零改动**：没有 `config.yaml` 时，必须能从 v1 环境变量生成一个隐式 job。
2. **升级不自动删数据**：隐式 job 默认不启用 retention；任何删除都必须有显式配置、
   `max_delete` 上限和 dry-run 预演。
3. **密钥不出现在日志、命令行参数和提交里**：只允许通过环境变量注入，日志只打印变量名或 `***`。
4. **rclone 是内部实现细节**：配置层只使用 `kind: s3|sftp|local|rclone` 和自有字段，
   不把 rclone 的旗标、remote 名、conf 结构暴露给用户。
5. **单文件自包含发布，不启用 Native AOT / trimming**：YamlDotNet 依赖反射。
6. **恢复必须能独立工作**：备份格式、对象 key、保留规则不能依赖进程内状态或文件名时间戳解析。

---

## 2. 架构边界

### 2.1 项目

```text
src/CronBackup.Core/     # 配置、存储抽象、保留策略、归档/执行管线
src/CronBackup.Cli/      # 参数解析、组合根、命令、日志、调度
tests/CronBackup.Core.Tests/
tests/CronBackup.Cli.Tests/
```

- `CronBackup.Core` 不引用 `CronBackup.Cli`，也不引用 Hosting / 命令行框架。
- `CronBackup.Cli` 只做组合与适配，业务规则放 Core。
- 测试优先走 Core 的公开接口；不要为了测试把内部类型改成 public。

### 2.2 依赖方向

- 配置模型 → 不知道存储实现。
- 存储抽象 `IStorageBackend` → 不知道 rclone 进程。
- `RetentionPlanner` 是纯函数 → 不读文件、不读时钟、不发网络请求，时间通过 `TimeProvider` 注入。
- `RcloneBackend` / `RcloneConfRenderer` 是唯一允许拼 rclone 命令和 conf 的地方。
- 不要在 CLI 命令里直接 `Process.Start("rclone", ...)`。

### 2.3 单例边界

- 一个 job 一次运行一个锁；不要用全局静态字典保存 job 状态。
- 一个进程内可以渲染一份 rclone.conf 给多个 destination 使用；不要在每次上传时重复创建临时目录。
- 临时文件、临时归档、rclone.conf 的清理必须在 `finally` 和进程退出钩子里都覆盖。

---

## 3. 配置语义

### 3.1 优先级

```text
CLI > job(YAML) > env 默认 > 内置
```

- env 只负责凭据和全局默认：`TZ`、`LOG_TARGET`、`DRY_RUN`、`NOTIFY_URL`。
- `BACKUP_SRC`、`RCLONE_REMOTE_NAMES`、`RCLONE_PATH`、`CRON_SCHEDULE`、`INTERVAL_SECONDS`
  只在**没有 config.yaml**、生成隐式 job 时读取；存在 config.yaml 时忽略并 warning。
- 合并是逐字段合并，不是整个对象覆盖。列表字段（`destinations`、`notify`、`hooks`）
  以 job 为准，job 没写才用 defaults。

### 3.2 `${VAR}`

- 只支持 `${VAR}` 和 `${VAR:-default}`；支持 `$${...}` 转义。
- **不支持** `$VAR`，也不支持命令替换、shell 展开或递归插值。
- 只对字符串字段插值；数字/布尔字段写 `${VAR}` 应直接报配置错误。
- 缺变量 fail fast；错误信息只给字段路径和变量名，不给值。
- 建议凭据字段只写占位符。`validate` 对明显是字面密钥的字段给出 warning，但不阻断。

### 3.3 retention

- 全局 `defaults.retention` 可被 `job.retention` 逐字段覆盖。
- 正整数 = 保留最新 N 个对象；`0` = 该 tier 关闭（不提升、不清理）；
  `enabled: false` = 整个 job 关闭。
- 未配置 retention 时**不清理**。隐式 job 永远默认关闭。
- `max_delete` 是硬上限；候选超过上限时整批中止，不做部分删除。
- 不要通过解析文件名里的日期来决定保留，使用对象元数据 `RemoteObject.Modified`。

### 3.4 配置校验

- YAML 严格模式：重复 key、未知字段、重复 job 名、非法 kind、缺必填项都要报错。
- `validate` 默认离线；只有 `--online` 才允许做远端探测。
- 任何新增字段都要同时补：示例配置、校验、单测、README 表格。

---

## 4. 存储与 rclone

### 4.1 IStorageBackend

```csharp
Task<RemoteObject?> StatAsync(string key, CancellationToken ct);
Task UploadAsync(string localPath, string key, CancellationToken ct);
Task CopyWithinAsync(string sourceKey, string destinationKey, CancellationToken ct);
Task<IReadOnlyList<RemoteObject>> ListAsync(string prefix, CancellationToken ct);
Task DownloadAsync(string key, string localPath, CancellationToken ct);
Task DeleteManyAsync(IReadOnlyCollection<string> keys, CancellationToken ct);
bool SupportsServerSideCopy { get; }
```

- key 一律是相对路径，用 `/` 分隔；禁止绝对路径、`..`、空 key。
- `LocalBackend` 必须做 root 路径约束，所有操作不得逃出 root。
- 上传使用“临时文件 + 原子重命名”，避免留下半个归档。
- `SupportsServerSideCopy == false` 时，提升/复制走下载到临时目录再上传，校验后删除临时文件。

### 4.2 rclone

- 单个文件上传用 `rclone copyto`；**不要**用 `rclone copy` 传单个文件（它把目标当目录）。
- 需要读取对象元数据时用 `lsjson`，不要解析 `ls -l` 的人类可读输出。
- SFTP 的 `pass` 在 rclone.conf 中必须是 obscured 值。通过 `rclone obscure -`
  从 stdin 传入，**绝不放进 argv**。
- 临时 rclone.conf：目录 `0700`、文件 `0600`，退出即删；启动时清扫过期残留。
- 不设置全局 `RCLONE_CONFIG`，每次调用显式 `--config <tmp>` 并加 `--ask-password=false`。
- 不要记录 rclone 命令中的完整 key 之外的敏感信息；conf 内容永不进日志。

---

## 5. 保留、归档、钩子

### 5.1 分层保留

- 远端布局固定为 `<root>/daily/`、`<root>/weekly/`、`<root>/monthly/`。
- 上传永远先落 `daily/`；周日 / 每月 1 号做服务端复制提升。
- 提升要幂等：目标 key 已存在就跳过；补跑窗口用“最近 7/31 天”判断，避免同一周/月重复提升。
- 清理只按 `Modified` 倒序保留最新 N 个，当前刚上传的对象永远受保护。
- 删除前再次校验所有 key 都在目标 tier 前缀下；空前缀、root 解析失败一律拒绝。
- 删除必须支持 `DRY_RUN` / `--dry-run`；手动 `prune` 默认 dry-run，`--apply` 才真删。

### 5.2 归档

- Linux 上调用 GNU `tar`，保持 v1 的 `-C <source> .` 语义；
  纯 .NET 归档只作为跨平台测试替身，不作为默认生产路径。
- 多 job 的临时归档使用独立目录；成功、失败、取消都要清理。
- 归档名默认 `backup_<YYYYMMDD_HHMMSS>.tar.gz`，与 v1 保持可识别。

### 5.3 hooks

- pre/post 是**备份容器内部的子进程**，不是跨容器 RPC。
- 默认不经过 shell：`run` + `args` 直接执行；需要 shell 时显式写 `/bin/sh -c`。
- pre 失败中止本次 job；post 在 `finally` 中执行，即使上传失败也要跑。
- 超时杀进程树；dry-run 默认不执行 hook。
- 脚本独立文件、挂载或打进镜像；不要把大段脚本内嵌进 YAML 或 `backup.sh`。
- 跨容器动作走共享卷 + 明确时序，或 webhook/healthchecks 通知；**不要挂 docker.sock**。

---

## 6. 安全

- 不记录明文密码、令牌、S3 secret、SFTP 密码、webhook token。
- `DestinationConfig.ToString()`、`plan`、`validate`、异常消息都必须脱敏。
- 不让秘密进入 `ProcessStartInfo.Arguments`；需要 stdin 的走 stdin。
- 恢复流程解压 tar 前必须逐条校验目标路径，防止 `../` 路径穿越。
- 不把个人绝对路径写进代码默认值；容器内路径用 `/backup`、`/config`、`/tmp`。
- 任何“默认放行/默认删除/默认覆盖”的行为都要先停下来检查。

---

## 7. 测试

- 框架 xUnit；命名 `方法名_条件_期望行为`。
- 单测不依赖 rclone、Docker、真实网络。`LocalBackend` + `TimeProvider` 覆盖主要路径。
- 需要真实后端的测试打 `[Trait("Category", "Integration")]`，通过环境变量显式开启。
- 时间相关逻辑必须注入 `TimeProvider`；禁止 `Thread.Sleep` 等待时间流逝。
- 配置测试要覆盖：优先级、隐式 job、`${VAR}`、未知字段、retention 合并。
- 保留测试要覆盖：最新 N、当前 key 保护、`max_delete`、dry-run、周日/1 号、时区与 DST。
- 临时目录用完即删；测试之间不得共享可写状态。
- 共享门禁：

  ```bash
  dotnet build
  dotnet test
  ```

- 改公开配置/CLI 行为时，同步改 `samples/config.example.yaml` 与 README。

---

## 8. 不要做的事

- 不要启用 Native AOT、`PublishTrimmed` 或依赖裁剪来减小体积。
- 不要在镜像里再装 crond；调度用 Cronos。
- 不要把 rclone 的 remote 名、旗标、conf 结构暴露成用户配置。
- 不要用 `rclone copy` 传单个文件。
- 不要让隐式 job 默认开启 retention。
- 不要在日志、提交、配置示例里出现真实密钥。
- 不要在代码/提交信息里写计划代号（“阶段 3”“P0-2”之类）；计划编号只存在于 `.agents/`。
- 不要为了方便而让 `Core` 引用 `Cli`，或让 `RetentionPlanner` 直接读系统时钟。
- 不要把 `.agents/` 下的 plan/todo/scratch 提交进 Git（见 `.agents/README.md`）。
- 不要添加 `Co-authored-by`、`Generated-by` 之类署名词脚。

---

## 9. 任务分级与落盘

接手任务后的第一个动作是定级，并把结果写进 `.agents/todo.md` 对应记录。
**允许升级，禁止悄悄降级。**

- **轻量切片（Small）**：单项目单层、无配置 schema / CLI / 存储语义变更。
  例：文档、单个 bug、单个测试补齐。无需 plan 文件，但 todo 要有记录。
- **功能切片（Feature）**：跨 Core + Cli，或新增配置字段、后端、子命令；
  不包含破坏性配置变更。需要 `.agents/plans/<日期>_<slug>.md`。
- **大目标（Epic）**：多阶段演进、v2 重写、配置/保留/安全语义变更、跨会话完成。
  需要完整 plan、明确阶段验收，且在整体闭环前不得把半成品合入 `main`。

开工节奏：

1. 读 `.agents/todo.md`，确认没有同主题的在途任务。
2. 写 todo 六行块；Feature/Epic 先写 plan。
3. 从 `main` 检出 `<类型>/<slug>`；Epic 用 `feature/<slug>` 作为汇聚分支。
4. 编码、测试、原子提交；同一主题的后续修改回原分支，不要每条指令开新分支。
5. 停在分支上向用户报备；**合并到 `main` 只能由用户明确指令触发**。
6. 合并后更新 todo、归档 plan；不留下孤儿目录。

不做任务分片机制，不强制 `scratch/` 子目录；一个任务就是一个分支、一个 plan（轻量切片可无 plan）。

---

## 10. 提交与协作

- 遵循约定式提交 1.0.0：`<类型>[(范围)][!]: <描述>`。
- 类型只用：`feat`、`fix`、`refactor`、`perf`、`style`、`docs`、`test`、`build`、`ci`、`chore`、`revert`。
- 范围建议：`config`、`storage`、`retention`、`cli`、`scheduler`、`docker`、`ci`。
- 描述用中文、一句话、不以句号结尾；标题说清“改了什么”，正文写“为什么”。
- 正文用 `-` 列表写动机和取舍，不要只复述 diff。
- 一次提交只做一件事；不相关的改动拆开。
- 破坏性变更在 `:` 前加 `!`，或脚注写 `BREAKING CHANGE: ...`。
- 提交信息要能脱离对话独立读懂：不写“按计划第 2 步”“本轮讨论”等指代。
- 不添加署名类 trailer；AI 工具默认追加的要手工去掉。
- 提交签名沿用本机 Git 配置；签名失败时保持现场，不要用 `--no-gpg-sign` 绕过。
- 分支命名：`<类型>/<slug>`，不用 `dev` / `wip` / `misc`；Epic 用 `feature/<slug>`。
- 合并用 `--no-ff`；合并节点正文写“计划单元 / 实际完成 / 偏差 / 遗留”。
- 版本 tag 只在用户明确要求时打：v1 是 shell 版，v2 走语义化版本。

---

## 11. 常用命令

```bash
# 恢复 SDK
dotnet --list-sdks

# 构建与测试
dotnet build
dotnet test

# 后续 CLI 骨架落地后
dotnet run --project src/CronBackup.Cli -- validate --config samples/config.example.yaml
dotnet run --project src/CronBackup.Cli -- plan --config samples/config.example.yaml

# 查看在途任务
cat .agents/todo.md
```
