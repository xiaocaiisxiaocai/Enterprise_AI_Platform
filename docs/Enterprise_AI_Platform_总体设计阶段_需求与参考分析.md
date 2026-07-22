# Enterprise AI Operating Platform 总体设计与开源项目对标分析

版本：V1.0

状态：Review

能力状态：Target / Planned（不代表已实现）

核查日期：2026-07-22
适用范围：需求、架构、技术选型和开源组件预研

本文是开源参考、差距评估与采用决策的事实源。SRS 只保留稳定业务需求，不重复易变化的项目结论。所有外部项目能力均来自其官方 GitHub 仓库或规范仓库；“参考”不等于已引入依赖，正式采用必须经过许可证、安全、性能、运维和退出策略评审。

------------------------------------------------------------------------

## 1. 项目背景与定位

### 1.1 项目定位

Enterprise AI Operating Platform（企业级 AI
平台）不是一个简单的聊天机器人，也不是一个普通 RAG 系统。

它定位为：

> 企业 AI 基础设施平台（Enterprise AI Operating Platform）

目标：

-   将企业知识资产化
-   将企业软件 AI 化
-   将业务流程智能化
-   建立企业 Agent 运行体系
-   形成持续优化的 AI 飞轮

------------------------------------------------------------------------

## 2. 核心问题分析

企业实际问题：

### 2.1 知识孤岛

企业知识分散：

-   公共文件
-   技术文档
-   经验总结
-   培训资料
-   邮件
-   Teams
-   项目资料

问题：

-   找不到
-   不知道哪个版本正确
-   老员工经验无法复制

------------------------------------------------------------------------

### 2.2 经验无法沉淀

大量知识存在：

-   工程师脑中
-   销售经验中
-   运维处理记录中

需要：

AI辅助沉淀企业 Memory。

------------------------------------------------------------------------

### 2.3 软件系统无法被 AI 理解

传统系统：

-   ERP
-   MES
-   IPMS
-   自研系统

都是给人使用。

未来需要：

AI Ready。

------------------------------------------------------------------------

## 3. 总体设计思想

核心思想：

### 3.1 不是做一个 AI 助手

而是：

### 3.2 建设企业 AI 操作平台

架构：

``` mermaid
graph TD

User[用户]

User --> Experience[AI体验层]

Experience --> APIGateway[API Gateway]

APIGateway --> Identity[Identity and Tenant]

APIGateway --> Agent[Agent Runtime]

Agent --> Knowledge[Knowledge Platform]

Agent --> Tool[Tool Platform]

Agent --> Workflow[Workflow Engine]

Agent --> Memory[Memory System]

Tool --> Business[企业业务系统]

Knowledge --> Connector[数据连接层]

Agent --> Policy[Policy Decision]

Agent --> ModelGateway[Model Gateway]

Agent --> Evaluation[Evaluation and Observability]

Policy --> Audit[Immutable Audit]
```

架构必须区分：

- API Gateway：入口认证、限流、租户解析和协议适配。
- Model Gateway：模型供应商适配、路由、预算、限额、降级和数据地域策略。
- 控制面：Agent、Prompt、Model Policy、Tool、Knowledge、Workflow 和 Evaluation 的版本与发布。
- 数据面：Agent 执行、权限感知检索、Tool 执行、持久工作流和模型调用。
- 横切可信内核：Identity、Policy、Approval、Audit、Secrets、Observability；任何内部调用不得绕过。

------------------------------------------------------------------------

## 4. 核心模块设计

### 4.1 Agent Runtime

负责：

-   理解用户目标
-   制定计划
-   调用工具
-   管理状态
-   反思优化

核心组件：

-   Router Agent
-   Planner Agent
-   Executor
-   Reviewer
-   Reflection Agent

流程：

``` mermaid
sequenceDiagram

User->>Agent: Request

Agent->>Planner: Create Plan

Planner->>Executor: Execute

Executor->>Knowledge: Retrieve

Executor->>Tool: Execute Action

Tool-->>Executor: Result

Executor->>Reviewer: Validate

Reviewer-->>User: Response
```

------------------------------------------------------------------------

### 4.2 Knowledge Platform

定位：

企业知识智能平台。

不是：

PDF聊天机器人。

能力：

-   Connector
-   Parser
-   Cleaner
-   Metadata Extract
-   Knowledge Extraction
-   Chunk
-   Embedding
-   Retrieval
-   Rerank
-   Citation
-   Knowledge Governance

流程：

``` mermaid
graph LR

Source --> Connector

Connector --> Parser

Parser --> Cleaner

Cleaner --> Extract

Extract --> Chunk

Chunk --> Embedding

Embedding --> Index

Index --> Retrieval
```

------------------------------------------------------------------------

### 4.3 Tool Platform

负责：

让AI连接企业能力。

包括：

-   Tool Registry
-   Tool Discovery
-   Tool Execution
-   Permission
-   Audit

例如库存系统：

``` text
query_inventory()

create_order()

stock_low_event
```

------------------------------------------------------------------------

### 4.4 AI Enablement SDK

核心设计：

未来所有业务系统天然支持AI。

业务系统提供：

-   Tool
-   Event
-   Knowledge
-   Permission
-   Audit

例如：

``` csharp
[AITool(Name="GetDeviceStatus")]
public DeviceStatus GetStatus(string id)
{
}
```

------------------------------------------------------------------------

### 4.5 Workflow Engine

原则：

Agent负责智能。

Workflow负责流程。

支持：

-   审批
-   长任务
-   重试
-   恢复
-   人工介入

------------------------------------------------------------------------

### 4.6 Governance

企业必须具备：

-   Identity
-   RBAC
-   ABAC
-   Policy Engine
-   Audit
-   Risk Control

------------------------------------------------------------------------

## 5. 数据源规划

### 第一阶段

接入：

-   公共文件区域
-   人工知识录入

原因：

高价值、低风险。

------------------------------------------------------------------------

### 后续扩展

预留：

-   Email
-   Teams
-   IPMS
-   工单系统
-   ERP
-   MES
-   CRM

------------------------------------------------------------------------

## 6. 数据治理设计

知识进入流程：

``` mermaid
graph LR

Data --> AIProcess

AIProcess --> HumanReview

HumanReview --> Publish

Publish --> Feedback

Feedback --> Improve
```

阶段：

### 初期

人工审核。

### 中期

AI辅助审核。

### 后期

高可信知识自动治理。

------------------------------------------------------------------------

## 7. 受治理的持续改进闭环

用户反馈、失败轨迹和专家修正只能生成改进候选，禁止直接改变生产知识、Prompt、Agent 或 Skill。参考 Hermes Agent 的经验沉淀与 Skill 机制，但增加企业发布门禁：

``` mermaid
stateDiagram-v2
    [*] --> Candidate
    Candidate --> Quarantined: 来源或安全检查失败
    Candidate --> Evaluating: 脱敏并锁定数据集
    Evaluating --> Rejected: 质量或安全退化
    Evaluating --> Reviewing: 达到离线门槛
    Reviewing --> Canary: Owner批准
    Canary --> Rejected: 在线指标退化
    Canary --> Published: 灰度通过
    Published --> RolledBack: 触发回滚
    Published --> Superseded: 新版本替代
```

该图描述跨资产发布门禁的 `promotion_stage`，不替代 Knowledge、Agent、Tool、Skill 或 Policy 的领域 `lifecycle_status`；各资产仍按所属事实源执行合法状态转换，Canary/回滚通过 Release 记录和流量策略落地。

每个候选必须保存来源、生成主体、输入证据、目标范围、版本、数据分类、评测结果、审批记录和回滚目标。改进闭环包括知识缺口、检索策略、Prompt、模型路由和 Skill，但各自使用独立版本，不共享未经验证的生产写权限。

------------------------------------------------------------------------

## 8. GitHub 项目对标与采用结论

### 8.1 选取方法

不以 Star 数作为单一依据。对标维度为：与业务问题的匹配度、维护状态、协议稳定性、多租户与安全边界、可观测性、部署复杂度、许可证、数据驻留和可替换性。结论分为：

- **采用机制**：进入本平台规范，但不必直接依赖项目代码。
- **PoC 候选**：必须通过场景化验证和 ADR 才能成为依赖。
- **观察**：保留设计启发，不进入一期关键路径。
- **不直接采用**：信任模型、维护状态或产品定位不适合企业共享平台。

### 8.2 一体化平台与知识工程

| 项目与官方证据 | 已核验机制 | 本平台吸收点 | 结论与边界 |
|---|---|---|---|
| [Dify](https://github.com/langgenius/dify/blob/main/README.md) | Workflow、RAG、Agent、模型管理、LLMOps、API | 控制台工作台、应用发布、Prompt/数据集/模型联动 | 产品体验基准；不作为 Identity、Policy、Audit 的事实源 |
| [RAGFlow](https://github.com/infiniflow/ragflow/blob/main/README.md) | 深度解析、可解释 Chunk、混合检索、Rerank、引用、摄取编排 | 解析质量门禁、证据引用、人工干预、权限感知检索 | Knowledge Provider PoC 候选；必须验证 ACL、删除传播和资源成本 |
| [Docling](https://github.com/docling-project/docling/blob/main/README.md) | 多格式解析、布局/表格/OCR、统一文档表示、本地运行 | 标准 Document IR、离线敏感文档解析、解析器可插拔 | 解析器 PoC 候选；模型许可证单独审查 |
| [MinerU](https://github.com/opendatalab/MinerU/blob/master/README.md) | PDF/Office/图片解析、结构化输出、OCR、私有部署 | 中文复杂版面和长文档基准集 | 解析器 PoC 候选；自定义许可证和模型许可证需法务复核 |
| [LlamaIndex](https://github.com/run-llama/llama_index/blob/main/README.md) | Connector、Index、Retriever、Reranker 可组合抽象 | Provider Port、Connector 与索引解耦 | 采用抽象思想；避免框架对象成为核心领域模型 |

### 8.3 Agent Runtime、个人 Agent 与持久工作流

| 项目与官方证据 | 已核验机制 | 本平台吸收点 | 结论与边界 |
|---|---|---|---|
| [LangGraph](https://github.com/langchain-ai/langgraph/blob/main/README.md) | Durable Execution、HITL、短期/长期记忆、状态图 | Checkpoint、Interrupt、Resume、可重放状态转换 | Runtime PoC 候选；领域状态不能绑定框架序列化格式 |
| [Microsoft Agent Framework](https://github.com/microsoft/agent-framework/blob/main/README.md) | Python/.NET、多 Agent 图工作流、Checkpoint、HITL、OpenTelemetry | 跨语言契约、中间件、可观测和多 Agent 模式 | Runtime PoC 候选；评估云绑定和第三方数据边界 |
| [OpenAI Agents SDK](https://github.com/openai/openai-agents-python/blob/main/README.md) | Agent/Tool/Handoff、Guardrail、Session、HITL、Tracing、Sandbox | 简洁运行模型、Guardrail 分层、Agent-as-Tool | SDK 参考与 PoC 候选；通过 Model Gateway 保持供应商可替换 |
| [AutoGen](https://github.com/microsoft/autogen/blob/main/README.md) | 多 Agent、事件驱动 Runtime、Benchmark | 历史模式与迁移经验 | 官方已标记维护模式；新项目不以其作为基线 |
| [Temporal](https://github.com/temporalio/temporal/blob/main/README.md) | Durable Workflow、失败恢复、重试、长任务 | 业务长流程、定时器、Signal、Activity 幂等与补偿 | Workflow Engine PoC 候选；不替代单次 Agent 推理状态 |
| [OpenClaw README](https://github.com/openclaw/openclaw/blob/main/README.md)、[安全模型](https://github.com/openclaw/openclaw/blob/main/SECURITY.md)、[威胁模型](https://github.com/openclaw/openclaw/blob/main/docs/security/THREAT-MODEL-ATLAS.md) | 本地 Gateway、多渠道、会话路由、Skill、Sandbox、Exec Approval、供应链威胁建模 | 渠道适配、工作区隔离、工具审批、Skill 签名/扫描/锁版/回滚 | **不直接作为企业共享 Runtime**；其单用户可信操作员模型不是多租户授权边界 |
| [Hermes Agent README](https://github.com/NousResearch/hermes-agent/blob/main/README.md)、[安全文档](https://github.com/NousResearch/hermes-agent/blob/main/website/docs/user-guide/security.md)、[Skill Manager](https://github.com/NousResearch/hermes-agent/blob/main/tools/skill_manager_tool.py) | 经验生成 Skill、跨会话检索、子 Agent、Cron、多后端、命令审批和写路径保护 | 受治理的 Skill 候选、程序性记忆、失败复盘、危险操作硬阻断 | **不允许生产自修改**；所有学习产物必须走 Candidate→Evaluating→Reviewing→Canary→Published，并支持拒绝、隔离与回滚 |

### 8.4 协议、网关、安全与评测

| 项目与官方证据 | 已核验机制 | 本平台吸收点 | 结论与边界 |
|---|---|---|---|
| [MCP 2025-11-25](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/specification/2025-11-25/index.mdx)、[安全实践](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/docs/tutorials/security/security_best_practices.mdx) | Host/Client/Server、能力协商、Tools/Resources/Prompts、OAuth、Consent、禁止 Token Passthrough、SSRF 防护 | MCP Gateway、远程服务器信任、最小 Scope、每客户端同意、审计 | 采用稳定版协议；草案需另行 ADR，不自动追随 main |
| [A2A](https://github.com/a2aproject/A2A/blob/main/README.md) | Agent Card、任务生命周期、Streaming、Push、Opaque Agent 协作 | 外部 Agent 发现和长任务互操作边界 | 观察/兼容；一期不把内部模块通信改造成 A2A |
| [LiteLLM](https://github.com/BerriAI/litellm/blob/main/README.md) | 统一模型 API、Virtual Key、预算、Guardrail、负载均衡 | Model Gateway PoC 基线 | 候选而非既定依赖；验证延迟、故障隔离、审计和数据地域 |
| [OPA](https://github.com/open-policy-agent/opa/blob/main/README.md) | 通用上下文策略决策 | Tool、数据、部署和发布策略的 PDP | Policy Engine 候选；策略版本、测试、解释和 fail-closed 必须由平台定义 |
| [OpenFGA](https://github.com/openfga/openfga/blob/main/README.md) | Zanzibar 风格细粒度关系授权 | 文档、知识空间、Agent、Tool 的资源关系权限 | Authorization 候选；先验证一致性、延迟、模型迁移与租户隔离 |
| [Langfuse](https://github.com/langfuse/langfuse/blob/main/README.md) / [Phoenix](https://github.com/Arize-ai/phoenix/blob/main/README.md) | Trace、Prompt 版本、Dataset、Eval、Experiment | 统一 Trace 与评测工作台候选 | 二选一或适配，不允许双写成为长期默认 |
| [Promptfoo](https://github.com/promptfoo/promptfoo/blob/main/README.md) | LLM 回归、红队、安全扫描、CI 门禁 | Prompt/模型/Agent 安全回归 | 评测执行器候选；敏感用例应本地运行并脱敏 |

------------------------------------------------------------------------

## 9. 关键架构决策摘要

| 决策 | 原因 | 约束 |
|---|---|---|
| 从权限感知知识助手起步，而非纯聊天或全自主 Agent | 先验证可量化业务价值并控制副作用 | 无证据必须拒答；检索前执行租户与 ACL 过滤 |
| 模块化单体起步 | 降低早期部署、调试和事务复杂度 | 逻辑模块独立数据所有权；达到量化拆分条件后才微服务化 |
| Tool/Workflow 承载行动，Agent 只做受限决策 | 确定性副作用不能交给概率模型 | 每次执行重新鉴权；高风险动作绑定参数哈希、审批和审计 |
| AI Enablement SDK 以契约优先 | 让业务系统显式暴露 Tool、Event、Knowledge，而非被屏幕自动化绕过 | 权限、风险、副作用和幂等必须显式声明，禁止模型推断 |
| 开源组件通过 Port/Adapter 接入 | 降低框架锁定和替换成本 | 领域模型、审计 ID、策略接口与版本快照归平台所有 |
| IM/多渠道晚于可信知识核心 | 渠道扩大攻击面和授权复杂度 | 仍需预留 Channel Adapter；接入时默认配对/白名单和最小 Tool Profile |

完整决策及复审条件以 `20_ADR技术决策.md` 为事实源。

------------------------------------------------------------------------

## 10. 目标结果

正式产品名为 **Enterprise AI Operating Platform（企业 AI 操作平台）**。“Operating System”仅可作为愿景比喻，不作为产品名或技术边界。

目标能力：

- 权限感知、可追溯、可撤回的企业知识服务。
- 可版本化、可恢复、受预算和策略约束的 Agent Runtime。
- 有明确风险、Schema、幂等、审批和审计的 Tool/SDK 生态。
- 确定性、持久化、可补偿的 Workflow 与 Human-in-the-loop。
- 覆盖质量、安全、延迟、成本和业务价值的 Evaluation/Observability 闭环。

------------------------------------------------------------------------

## 11. 当前交付状态与下一步

当前仓库只有设计文档，没有源码、测试、构建或部署产物。`01` 至 `20` 已存在但状态均为目标设计，不得表述为已实现或已验证。

下一步按以下顺序推进：

1. 关闭 SRS 中的业务规模、SLO、数据分类、保留期、RPO/RTO 等 TBD。
2. 冻结领域所有权、Agent/Tool/Knowledge/Workflow 状态机和版本契约。
3. 使用真实脱敏样本完成解析、检索、Runtime、Policy、Workflow、Evaluation 的 PoC。
4. 为候选组件形成包含版本、许可证、SBOM、安全公告、容量和退出方案的 ADR。
5. 只有通过验收门禁后，才能把能力状态从 Planned 改为 Implemented，再以测试证据改为 Verified。

------------------------------------------------------------------------

## 12. 修订前文档基线（历史记录）

以下评分记录 2026-07-22 系统性修订开始前的文档状态，用于解释第 13 节修订缘由；它不是当前成熟度，更不是运行结果。保留原始分数是为了避免用事后修改覆盖历史判断。

| 维度 | 修订前成熟度（5分） | 当时主要证据 | 目标 |
|---|---:|---|---|
| 业务定位 | 3.0 | 问题与目标清楚，但需求缺验收指标和范围边界 | 每条 BR 可追踪到 FR、设计与验收 |
| 总体架构 | 2.0 | 组件齐全，但控制面/数据面、Gateway、租户和信任边界不清 | 架构不变量、失败路径和所有权明确 |
| 领域与数据 | 1.5 | 多数只有实体名称，缺不变量、版本、租户、血缘和删除语义 | 聚合、事件、Schema、迁移和保留可实施 |
| Agent/Workflow | 2.0 | 有状态图思想，缺 Durable Execution、幂等、补偿和预算 | 可恢复、可取消、可审批、可回放 |
| 安全与治理 | 1.5 | 有 RBAC/ABAC/审计名词，缺威胁模型和端到端强制点 | 默认拒绝、纵深防御、追加式可校验防篡改审计、红队门禁 |
| Evaluation/运维 | 1.5 | 有指标分类，缺数据集、阈值、SLO、告警与回滚 | 版本化评测、灰度、错误预算和 Runbook |
| 可实施性 | 1.0 | 尚无源码、OpenAPI、事件 Schema、构建测试或环境 | Phase 0 形成最小可信可运行内核 |

### 12.1 修订后判断（不打主观分数）

本轮已经补齐主要目标架构边界、状态与版本原则、失败路径、安全控制、评测和运维门禁，但所有能力仍为 Planned。当前不应通过再次主观打分制造“成熟度上升”的错觉；后续只以决议和证据推进状态：

| 维度 | 修订后判断 | 尚未关闭的决定性缺口 |
|---|---|---|
| 业务与产品 | 目标和价值框架已形成，决策闭合度仍偏低 | 首个试点、真实用户/规模、业务基线、Go/Hold/Stop 与责任人未批准 |
| 目标架构 | 模块边界和演进原则较完整 | ADR 均未 Accepted，租户/部署/组件选择仍有 TBD |
| 领域与运行语义 | 已明确主要聚合、版本和失败原则 | 状态矩阵、逻辑 DDL、事件/错误契约仍需转成机器可验证事实源 |
| 安全与治理 | 控制覆盖较完整 | 威胁→控制→检测→测试→证据追踪、实际数据清单和租户/密钥决策未闭合 |
| 工程可实施性 | 仍低 | 尚无源码、OpenAPI、逐事件 Schema、迁移、自动化测试、环境或 PoC 证据 |
| 生产就绪 | 未验证 | 没有部署、容量、恢复、红队、SLO 或业务收益运行证据 |

## 13. 已处置的 P0 差距与修订落点（历史记录）

| 修订前 P0 差距 | 风险 | 修订落点 |
|---|---|---|
| 模块化单体与多服务部署图冲突 | 团队会过早拆分或重复基础设施 | `03`、`12`、`19`、ADR-001 |
| PostgreSQL+pgvector 与独立 VectorDB 冲突 | 数据迁移和运维责任不清 | `04`、`07`、`12`、`19`、ADR-004 |
| Identity、Governance、Adapter 同时拥有权限/审计 | 策略可被绕过、审计不可追责 | `01`、`02`、`03`、`08`、`10`、`18` |
| AgentDefinition 与 AgentExecution 状态混淆 | 版本不可复现、恢复语义错误 | `04`、`06`、`14`、`15` |
| 治理、审批、监控在路线图后置 | 形成“先能执行、以后补安全”的窗口 | `10`、`11`、`13`、SRS |
| Knowledge 审核与自动发布规则冲突 | 未经验证内容进入生产 | `07`、`16`、SRS |
| MCP、Skill、Tool、A2A 边界不清 | 远程代码、Token 传递和供应链风险 | `08`、`10`、`17`、`18`、ADR |

## 14. 组件采用门禁

任何 PoC 候选均按同一评分卡评审：

| 门禁 | 必须提供的证据 |
|---|---|
| 功能适配 | 三个真实场景、异常路径、性能基线和不适用范围 |
| 安全 | 威胁模型、默认配置、身份传播、沙箱/出口、漏洞响应和渗透结果 |
| 数据与合规 | 数据流向、地域、保留、删除、训练使用声明和敏感字段处理 |
| 工程质量 | 版本策略、升级/回滚、迁移工具、健康检查、可观测和故障演练 |
| 供应链 | 许可证、依赖/模型许可证、SBOM、镜像签名、来源锁定和安全公告 |
| 经济性 | 资源成本、人工运维、扩容曲线、托管与自建 TCO |
| 可退出性 | 标准协议、导出格式、替代实现、数据迁移和最大可接受停机 |

若任一 P0 安全或数据门禁无证据，结论只能是“观察”，不能进入生产依赖。

## 15. 来源与时效边界

- 本文引用的项目状态核查于 2026-07-22；实现前必须锁定 release/tag/commit，不得以 `main` 直接进入生产。
- MCP 采用已发布的 `2025-11-25` 基线；仓库中的 draft 或候选版本需要兼容性测试和单独 ADR。
- 外部 README 中的性能、成熟度或“enterprise-ready”等表述属于项目方声明，本评估只将其作为 PoC 线索，不视为本平台验证结果。
- 未执行任何外部项目、基准测试或部署；本文所有采用结论均为设计阶段判断，必须由后续 PoC 证据更新。
