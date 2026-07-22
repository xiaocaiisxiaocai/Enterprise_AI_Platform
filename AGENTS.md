# Repository Guidelines

## 项目结构与模块组织

本仓库当前是企业 AI 平台的设计文档库，27 个 Markdown 文件均位于 `docs\`，尚无产品 `src\`、产品测试或构建清单。以 `docs\00_Index.md` 为入口；`01_` 至 `13_` 覆盖总体设计与路线规划，`14_` 至 `24_` 覆盖详细领域模型、SDK、运维、ADR、试点验收、组织治理、Model Gateway 和证据追踪。两份 `Enterprise_AI_Platform_*` 文档保存需求基线与参考分析。`scripts\Validate-Docs.ps1` 是文档回归校验器，`.github\workflows\docs-quality.yml` 在 push 和 PR 时执行它。新增主题使用 `NN_主题.md`，并同步更新索引。

## 架构约束

设计以 DDD 和模块化单体起步，按 Identity、Agent、Knowledge、Tool、Workflow、Governance、Evaluation 划分边界。PostgreSQL、pgvector、Kafka、Kubernetes 等目前是规划技术，不应描述为已落地依赖。改变服务边界、数据存储、安全模型或编排方式时，应同步更新 `docs\20_ADR技术决策.md`，记录原因、取舍和影响。

## 本地检查命令

当前没有可执行工程、测试框架或覆盖率门槛，不要虚构构建与运行结果。提交前可执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Validate-Docs.ps1 -SelfTest
Get-ChildItem .\docs -Filter *.md | Sort-Object Name
rg -n 'TODO|FIXME|TBD|待定' .\docs
rg -n '^#|^```' .\docs
```

首条命令先证明校验器能拒绝损坏样例，再检查标题、围栏、相对链接、JSON、版本和标识一致性；其余命令用于人工盘点。还需人工预览 Markdown 和 Mermaid，并验证 YAML 示例语义。引入首个产品代码模块时，应同时补充真实的构建、测试和格式化命令。

## 编写风格与命名

使用 UTF-8、中文说明和准确的领域术语；首次出现的英文缩写应解释。沿用邻近文档的编号章节，代码围栏标注 `mermaid`、`json`、`yaml`、`python` 或 `csharp`。API 路径采用小写复数与版本前缀，如 `/api/v1/agents/{id}/execute`；JSON 与 Python 使用 `snake_case`，C# 标识符使用 PascalCase，YAML 使用两空格缩进，权限标识使用 `domain.action`。

## 验证要求

文档变更必须检查术语、链接和跨文档一致性。需求变更回写 SRS；API 或实体变更联动服务边界、数据库模型和接口设计；高风险动作必须保留审批、最小权限和审计路径。单元测试、集成测试、AI Evaluation 与安全扫描属于实现阶段门禁，目前不能视为已执行。

## Commit 与 Pull Request

每完成一组逻辑完整的文件修改，必须先执行适用验证，再立即创建 Git 提交；不得将多组无关改动长期堆积在工作区。提交前使用 `git status --short` 和 `git diff --check` 核对范围，只暂存本次任务文件。提交应保持单一目的并使用简洁、带范围的祈使标题，例如 `docs(architecture): 澄清 Agent Runtime 恢复语义`。除非用户明确授权，不得推送、改写历史、合并或删除分支。

PR 需说明目标、影响文档、决策依据、关联需求或 ADR、验证结果及安全影响；Mermaid 或排版变化附渲染截图。禁止提交真实密钥、令牌、客户数据或内部地址，示例统一使用明显占位符。
