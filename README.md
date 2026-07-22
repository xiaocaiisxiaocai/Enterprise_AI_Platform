# Enterprise AI Platform

本仓库包含企业内部 AI 平台的目标设计，以及一个可运行的 Gate F 权限感知检索 PoC。

## 当前可运行范围

PoC 只验证最小安全假设：单一企业 Tenant 内，不同部门用户只能检索其源 ACL 允许的文档；检索结果包含固定版本与位置引用，无证据时统一拒答。它不接入真实 IdP、SharePoint、向量数据库或大模型，不代表业务试点、生产容量或 AI 质量已经通过。

测试身份：

- `alice-finance`：`employees`、`finance`；
- `bob-hr`：`employees`、`hr`。

## 本地运行

```powershell
dotnet build .\tests\EnterpriseAI.Poc.Regression\EnterpriseAI.Poc.Regression.csproj
dotnet run --project .\tests\EnterpriseAI.Poc.Regression\EnterpriseAI.Poc.Regression.csproj --no-build
dotnet run --project .\src\EnterpriseAI.Poc\EnterpriseAI.Poc.csproj
```

启动 API 后调用：

```powershell
$headers = @{ 'X-Poc-User' = 'alice-finance' }
$body = @{ question = '预算报销规则是什么' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri 'http://localhost:5000/api/v1/query' -Headers $headers -ContentType 'application/json' -Body $body
```

生产实现必须使用受信 IdP 声明解析身份；`X-Poc-User` 只允许出现在本地 Gate F PoC，不得晋级到 Gate P。

## 文档入口

从 [`docs\00_Index.md`](docs/00_Index.md) 开始阅读。所有能力状态和证据边界以设计文档及 ADR 登记簿为准。
