# Enterprise AI Platform

本仓库包含企业内部 AI 平台的目标设计，以及一个可运行的 Gate F 权限感知检索 PoC。

## 当前可运行范围

PoC 只验证最小安全假设：单一企业 Tenant 内，不同部门用户只能检索其源 ACL 允许的文档；检索结果包含固定版本与位置引用，无证据时统一拒答。`Data\approved-source.json` 是仓库内的合成批准快照，逐项声明 Owner、分类、ACL、版本和 SHA-256；加载器拒绝内容篡改、路径越界、重解析点及缺失 ACL。本地 Trace 以 JSONL 哈希链记录问题哈希、权限输入、快照、决策和授权引用，不保存问题原文或答案。`evaluation\gate-f-golden-v1.json` 提供 12 个版本化合成问题，确定性评测要求越权引用为 0、引用精确匹配率和拒答一致率均为 100%。它不接入真实 IdP、SharePoint、向量数据库或大模型，不代表业务试点、生产容量或概率性 AI 质量已经通过。

测试身份：

- `alice-finance`：`employees`、`finance`；
- `bob-hr`：`employees`、`hr`。

## 本地运行

```powershell
dotnet build .\tests\EnterpriseAI.Poc.Regression\EnterpriseAI.Poc.Regression.csproj
dotnet run --project .\tests\EnterpriseAI.Poc.Regression\EnterpriseAI.Poc.Regression.csproj --no-build
dotnet run --project .\tests\EnterpriseAI.Poc.Evaluation\EnterpriseAI.Poc.Evaluation.csproj -- --self-test
dotnet run --project .\src\EnterpriseAI.Poc\EnterpriseAI.Poc.csproj
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Export-GateFEvidence.ps1 -SelfTest
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Export-GateFEvidence.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-GateFEvidence.ps1 -SelfTest
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-GateFEvidence.ps1 -EvidencePath .\artifacts\gate-f-evidence.json
```

启动 API 后调用：

```powershell
$headers = @{ 'X-Poc-User' = 'alice-finance' }
$body = @{ question = '预算报销规则是什么' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri 'http://localhost:5000/api/v1/query' -Headers $headers -ContentType 'application/json' -Body $body
```

`dotnet run` 使用 `launchSettings.json` 进入 Development，并由 `appsettings.Development.json` 启用测试身份。其他环境默认禁用 `X-Poc-User`；若在 Production 显式启用，应用会拒绝启动。生产实现必须使用受信 IdP 声明解析身份，该测试头不得晋级到 Gate P。

API Trace 默认写入被 Git 忽略的 `.gate-f\search-traces.jsonl`。证据脚本重新执行构建、41 条回归（含 `REG-EVAL-*` 与扩展 `REG-API-*`）、12 个 Golden 用例和文档校验，并在 `artifacts\` 生成证据包、评测报告与评测 Trace；CI 为每个提交上传同结构 Artifact。评测报告不保存问题原文，只记录 Case ID、实际状态和引用。损坏数据集必须非零退出且不得产生伪造 Passed 报告。OIDC 等外部集成保持延期，直到获得企业权限和目标系统批准。

## 文档入口

从 [`docs\00_Index.md`](docs/00_Index.md) 开始阅读。所有能力状态和证据边界以设计文档及 ADR 登记簿为准。
