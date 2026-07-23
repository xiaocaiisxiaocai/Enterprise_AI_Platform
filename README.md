# Enterprise AI Platform

本仓库包含企业内部 AI 平台的目标设计，以及一个可运行的 Gate F 权限感知检索 PoC。

## 当前可运行范围

PoC 只验证最小安全假设：单一企业 Tenant 内，不同部门用户只能检索其源 ACL 允许的文档；检索结果包含固定版本与位置引用，无证据时统一拒答。`src\EnterpriseAI.Poc\Data\approved-source.json` 是仓库内的合成批准快照，逐项声明 Owner、分类、ACL、版本和 SHA-256；加载器拒绝内容篡改、路径越界、规范化冲突、重解析点、缺失/重复 ACL、超大文件与非法哈希。本地 Trace 以 JSONL 哈希链记录问题哈希、权限输入、快照、决策和授权引用，不保存问题原文或答案。`evaluation\gate-f-golden-v1.json` 提供 12 个版本化合成问题，确定性评测要求越权引用为 0、引用精确匹配率和拒答一致率均为 100%。

它不接入真实 IdP、SharePoint、向量数据库或大模型，不代表业务试点、生产容量或概率性 AI 质量已经通过。PostgreSQL、Kafka、Kubernetes 等为规划技术，不得描述为已落地依赖。

测试身份：

- `alice-finance`：`employees`、`finance`；
- `bob-hr`：`employees`、`hr`。

## 操作手册：证据生成与离线验证

生成完整 Gate F/F.1、本地状态与摄取证据包（Release 构建、85 条回归、12 Golden、文档门禁；原子发布至 `artifacts\`）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Export-GateFEvidence.ps1
```

离线只读校验证据包（不依赖网络、GitHub Artifact 或未声明工作区文件）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-GateFEvidence.ps1 -EvidencePath .\artifacts\gate-f-evidence.json
```

两条命令成功时均输出 `GATE_F_SUMMARY`（commit、回归计数、Golden 计数、越权引用数、数据集哈希、Trace 最终哈希与限制声明）。日志中预期的 4xx 是断言边界，不是未处理失败。

## 本地运行与分项检查

```powershell
dotnet build .\tests\EnterpriseAI.Poc.Regression\EnterpriseAI.Poc.Regression.csproj --configuration Release
dotnet run --project .\tests\EnterpriseAI.Poc.Regression\EnterpriseAI.Poc.Regression.csproj --configuration Release --no-build
dotnet run --project .\tests\EnterpriseAI.Poc.Evaluation\EnterpriseAI.Poc.Evaluation.csproj --configuration Release -- --self-test
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Validate-Docs.ps1 -SelfTest
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Export-GateFEvidence.ps1 -SelfTest
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-GateFEvidence.ps1 -SelfTest
dotnet run --project .\src\EnterpriseAI.Poc\EnterpriseAI.Poc.csproj
```

可选的本地 Markdown/TXT 启动同步必须显式提供根目录、来源、Owner、分类和 ACL；未配置时不扫描任何额外目录：

```powershell
dotnet run --project .\src\EnterpriseAI.Poc\EnterpriseAI.Poc.csproj -- `
  --GateF:LocalState:Path=C:\enterprise-ai-local-state `
  --GateF:LocalIngestion:RootPath=C:\approved-local-docs `
  --GateF:LocalIngestion:SourceId=local-approved `
  --GateF:LocalIngestion:Owner=knowledge-owner `
  --GateF:LocalIngestion:Classification=internal `
  --GateF:LocalIngestion:AllowedGroups:0=employees
```

同步以规范化相对路径生成稳定 ID，以内容 SHA-256 生成版本；重复内容不提升 repository revision。`GateF:LocalState:Path` 启用逐事件原子文件与哈希链，持久化本地测试身份变更、批准文档生命周期和摄取 Checkpoint；崩溃遗留的 `.pending-*.tmp` 不会被当成已提交事件，正式事件损坏则拒绝恢复。源文件删除或变为无效 UTF-8、空文件、超限文件时，旧投影会从检索中撤出并记录最小隔离原因。该账本仍是单进程本地机制，不是正式审计、数据库、恶意文件扫描、审核或企业 Connector。

启动 API 后调用：

```powershell
$headers = @{ 'X-Poc-User' = 'alice-finance' }
$body = @{ question = '预算报销规则是什么' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri 'http://localhost:5000/api/v1/query' -Headers $headers -ContentType 'application/json' -Body $body
```

`dotnet run` 使用 `launchSettings.json` 进入 Development，并由 `appsettings.Development.json` 启用测试身份。其他环境默认禁用 `X-Poc-User`；若在 Production 显式启用，应用会拒绝启动。生产实现必须使用受信 IdP 声明解析身份，该测试头不得晋级到 Gate P。

API Trace 默认写入被 Git 忽略的 `.gate-f\search-traces.jsonl`。CI（`.github\workflows\docs-quality.yml`）在文档、脚本、evaluation、src、tests 变更时运行文档门禁与 Gate F 证据导出/离线验证；最小只读权限、作业超时，并取消同分支过期运行。评测报告不保存问题原文。损坏数据集必须非零退出且不得产生伪造 Passed 报告。OIDC 等外部集成保持延期，直到获得企业权限和目标系统批准。

## 文档入口

从 [`docs\00_Index.md`](docs/00_Index.md) 开始阅读。能力状态以各文档页眉与 ADR 为准：目标架构多为 **Planned**；Gate F 本地确定性切片为 **PartiallyImplemented / Implemented（仅本地契约）**，不得外推为生产安全或业务验收。

Gate F V1.0 已通过注解标签 `V1.0` 发布；固定契约与排除范围见 [`docs\25_Gate_F_V1.0基线清单.md`](docs/25_Gate_F_V1.0基线清单.md)，实际发布证据与已知限制见 [`docs\26_V1.0发布记录.md`](docs/26_V1.0发布记录.md)。
