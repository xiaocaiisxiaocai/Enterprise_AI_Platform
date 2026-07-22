# Enterprise AI Operating Platform Architecture Book

> 文档状态：**Planned（目标设计，尚未实现）**
>
> 产品正式名：**Enterprise AI Operating Platform**
>
> 适用阶段：Phase 0 至后续演进
>
> 本索引不构成上线、验收或投产能力证明。

## 1. 文档定位

本架构书描述企业级 AI Agent 平台的目标能力、领域边界、接口契约和治理要求。平台目标是将企业知识、业务工具和工作流转化为可版本化、可授权、可审计、可评测的 Agent 能力，而不是构建单一聊天机器人。

目标包括：

- 企业知识资产化与权限一致的检索；
- 企业系统 AI 化与受控工具调用；
- Agent 规划、执行、恢复和持续评测；
- 人机协同的流程自动化；
- 全链路身份、策略、审计、Trace 和成本治理。

## 2. 目标架构基线

以下内容均为一期目标设计：

1. 一期采用**模块化单体**；逻辑模块不等于独立进程或独立部署服务。
2. 区分控制面与数据面；区分面向客户端的 API Gateway 与面向模型供应商的 Model Gateway。
3. Gate F 先以单企业固定 Tenant、合成身份和权限预过滤验证最小契约；Gate P 前再接入 OIDC、目标 Policy 实现、追加式审计、Trace、预算和正式评测。
4. 每次 Agent 执行固定 Agent、Prompt、ModelPolicy、ToolBinding、KnowledgePolicy 的不可变版本快照。
5. 每次 Tool 调用都重新鉴权，策略结果限定为 `Allow`、`Deny`、`RequireApproval`、`RequireStepUpAuth`。
6. 一期数据基线为 PostgreSQL + pgvector；密钥仅保存引用，不保存明文。
7. Agent 状态机以 [15 Agent状态机设计](15_Agent状态机设计.md) 为事实源；其他文档只保留摘要和链接。
8. MCP 兼容基线固定为 `2025-11-25`；升级必须经兼容性评估和 ADR。

## 3. 事实源与优先级

发生冲突时按以下顺序处理，并通过 ADR 消除冲突：

1. 已批准的 SRS 与安全/合规约束；
2. [20 ADR技术决策](20_ADR技术决策.md) 中状态为 Accepted 的决策；
3. 领域、状态机、API、数据模型等专项设计；
4. 总体架构和路线规划中的摘要；
5. 示例与参考分析。

`02` 与 `14`、`06` 与 `15` 分别为概览与详细设计关系。详细设计是对应主题的事实源，但不得突破总体架构不变量。任何实体、状态、API 或安全模型变更必须同步检查其上下游文档。

## 4. 需求与研究基线

- [总体设计阶段需求规格说明书 SRS V1.0](Enterprise_AI_Platform_总体设计阶段_需求规格说明书_SRS_V1.0.md)
- [总体设计阶段需求与参考分析](Enterprise_AI_Platform_总体设计阶段_需求与参考分析.md)
- [Gate F 可运行 PoC 与命令](../README.md)：批准快照完整性、本地权限检索、引用与拒答；不代表正式业务试点或生产能力。

## 5. 架构与实施文档

| 编号 | 文档 | 主要事实范围 | 状态 |
|---|---|---|---|
| 01 | [总体架构设计](01_总体架构设计.md) | 架构驱动、分层、控制面/数据面、信任边界 | Planned |
| 02 | [DDD领域模型设计](02_DDD领域模型设计.md) | 限界上下文、所有权、聚合与领域事件概览 | Planned |
| 03 | [服务边界设计](03_服务边界设计.md) | 模块化单体边界、契约、通信与拆分门槛 | Planned |
| 04 | [数据库模型设计](04_数据库模型设计.md) | PostgreSQL + pgvector、Schema、约束和生命周期 | Planned |
| 05 | [API接口设计](05_API接口设计.md) | HTTP/OpenAPI、异步执行、错误和 MCP 契约 | Planned |
| 06 | [Agent Runtime设计](06_Agent_Runtime设计.md) | 执行语义、版本快照、策略检查点和恢复 | Planned |
| 07 | [Knowledge Platform设计](07_Knowledge_Platform设计.md) | 知识摄取、检索与证据链 | Planned |
| 08 | [Tool Platform与AI SDK设计](08_Tool_Platform_AI_SDK设计.md) | 工具注册、执行隔离与 SDK | Planned |
| 09 | [Workflow与Human-in-the-Loop设计](09_Workflow_Human_In_Loop设计.md) | 长流程、审批与人工任务 | Planned |
| 10 | [Governance与Security设计](10_Governance_Security设计.md) | 身份、策略、审计、安全和风险控制 | Planned |
| 11 | [Evaluation、Monitoring与Cost设计](11_Evaluation_Monitoring_Cost设计.md) | 离线/在线评测、可观测性和成本 | Planned |
| 12 | [Deployment架构设计](12_Deployment架构设计.md) | 部署拓扑、环境和高可用目标 | Planned |
| 13 | [开发路线规划](13_开发路线规划.md) | 阶段门禁、依赖和交付物 | Planned |
| 14 | [DDD详细领域模型](14_DDD详细领域模型.md) | 聚合、实体、值对象和不变量事实源 | Planned |
| 15 | [Agent状态机设计](15_Agent状态机设计.md) | Agent 执行状态与转换事实源 | Planned |
| 16 | [Knowledge数据治理设计](16_Knowledge数据治理设计.md) | 分类、保留、删除和质量治理 | Planned |
| 17 | [AI Enablement SDK规范](17_AI_Enablement_SDK规范.md) | 接入 SDK 契约与兼容性 | Planned |
| 18 | [企业系统接入规范](18_企业系统接入规范.md) | Adapter、身份传播和审计接入 | Planned |
| 19 | [生产运维规范](19_生产运维规范.md) | SLO、告警、恢复和变更管理 | Planned |
| 20 | [ADR技术决策](20_ADR技术决策.md) | 关键技术选择、取舍和状态 | Planned |
| 21 | [首个试点用例与验收基线](21_首个试点用例与验收基线.md) | 推荐试点、用户旅程、范围、数据准备和 Go/Hold/Stop/Retire | Proposed |
| 22 | [组织责任与RACI运营模型](22_组织责任与RACI运营模型.md) | 唯一 Accountable、职责分离、人工容量和事件升级 | Planned |
| 23 | [Model Gateway契约设计](23_Model_Gateway契约设计.md) | 模型调用、路由、流式、预算、错误、计量和退出契约 | Planned |
| 24 | [测试评测与证据追踪计划](24_测试评测与证据追踪计划.md) | 原子验证、AI Eval、威胁测试、证据包和发布门禁 | PartiallyImplemented（文档门禁 + Gate F 回归） |

## 6. 推荐阅读路径

- 产品与业务：SRS → `21` → `22` → `01` → `13`。
- 架构与后端：`01` → `02` → `03` → `14` → `04` → `05` → `06` → `23`。
- Agent 与 AI：`06` → `15` → `07` → `08` → `23` → `11`。
- 安全与治理：`10` → `16` → `18` → `22` → `19` → `20`。
- 交付与验收：`21` → `24` → `13` → `11` → `12` → `19`。

## 7. 文档变更规则

- 文档描述目标设计时使用“计划、必须、目标”，不得使用实现完成态措辞。
- 新增关键技术、改变模块所有权、状态语义、数据存储、安全模型或协议基线时，必须新增或更新 ADR。
- API 或实体变更必须检查 SRS、DDD、数据库、状态机、治理、评测和运维文档的一致性。
- Mermaid 图、JSON/YAML 示例和相对链接必须在评审前完成渲染或语法检查；未验证项明确标记为 `TBD`。
- 每次架构评审至少给出：范围、非目标、关键假设、失败路径、验收点、剩余风险。
