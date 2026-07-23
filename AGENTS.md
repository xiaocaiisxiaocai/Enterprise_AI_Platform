# Repository Guidelines

## 项目结构与模块组织

本仓库包含企业 AI 平台目标设计与 Gate F 权限感知检索 PoC。27 个架构 Markdown 文件位于 `docs\`，`README.md` 是运行入口。`src\EnterpriseAI.Poc` 提供 .NET 8 最小 API、批准快照、权限预过滤和本地哈希链 Trace；`tests\EnterpriseAI.Poc.Regression` 运行缺陷回归，`tests\EnterpriseAI.Poc.Evaluation` 根据 `evaluation\gate-f-golden-v1.json` 运行本地确定性评测。`scripts\Validate-Docs.ps1` 校验文档，`scripts\Export-GateFEvidence.ps1` 生成证据包；`.github\workflows\docs-quality.yml` 在 push 和 PR 时执行门禁。

## 架构约束

设计以 DDD 和模块化单体起步，按 Identity、Agent、Knowledge、Tool、Workflow、Governance、Evaluation 划分边界。PostgreSQL、pgvector、Kafka、Kubernetes 等目前是规划技术，不应描述为已落地依赖。改变服务边界、数据存储、安全模型或编排方式时，应同步更新 `docs\20_ADR技术决策.md`，记录原因、取舍和影响。

## 本地检查命令

提交前执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Validate-Docs.ps1 -SelfTest
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Export-GateFEvidence.ps1
dotnet restore .\tests\EnterpriseAI.Poc.Regression\EnterpriseAI.Poc.Regression.csproj
dotnet build .\tests\EnterpriseAI.Poc.Regression\EnterpriseAI.Poc.Regression.csproj --configuration Release --no-restore
dotnet run --project .\tests\EnterpriseAI.Poc.Regression\EnterpriseAI.Poc.Regression.csproj --configuration Release --no-build
Get-ChildItem .\docs -Filter *.md | Sort-Object Name
rg -n 'TODO|FIXME|TBD|待定' .\docs
rg -n '^#|^```' .\docs
```

文档脚本先证明校验器能拒绝损坏样例，再检查标题、围栏、相对链接、JSON、版本和标识一致性。证据脚本重新执行 Release 构建、19 条回归、12 个 Golden 用例和文档门禁，并记录 commit、环境、来源/数据集哈希与评测 Trace 锚点。PoC 构建启用警告即错误；还需人工预览 Markdown/Mermaid 并验证 YAML 语义。

## 编写风格与命名

使用 UTF-8、中文说明和准确的领域术语；首次出现的英文缩写应解释。沿用邻近文档的编号章节，代码围栏标注 `mermaid`、`json`、`yaml`、`python` 或 `csharp`。API 路径采用小写复数与版本前缀，如 `/api/v1/agents/{id}/execute`；JSON 与 Python 使用 `snake_case`，C# 标识符使用 PascalCase，YAML 使用两空格缩进，权限标识使用 `domain.action`。

## 验证要求

文档变更必须检查术语、链接和跨文档一致性。需求变更回写 SRS；API 或实体变更联动服务边界、数据库模型和接口设计。修改 `Data\fixtures` 时必须同步更新 `approved-source.json` 的 SHA-256、ACL 和版本；修改 Golden Dataset 时必须保留版本、负向自测和硬门禁。Gate F 回归与 Golden 结果只证明本地确定性契约，不得描述为真实 IdP、SharePoint、向量检索、概率性 AI Evaluation 或生产安全证据。发现缺陷时先增加或强化回归场景，再修复实现。

## Commit 与 Pull Request

每完成一组逻辑完整的文件修改，必须先执行适用验证，再立即创建 Git 提交；不得将多组无关改动长期堆积在工作区。提交前使用 `git status --short` 和 `git diff --check` 核对范围，只暂存本次任务文件。提交应保持单一目的并使用简洁、带范围的祈使标题，例如 `docs(architecture): 澄清 Agent Runtime 恢复语义`。除非用户明确授权，不得推送、改写历史、合并或删除分支。

PR 需说明目标、影响文档、决策依据、关联需求或 ADR、验证结果及安全影响；Mermaid 或排版变化附渲染截图。禁止提交真实密钥、令牌、客户数据或内部地址，示例统一使用明显占位符。
