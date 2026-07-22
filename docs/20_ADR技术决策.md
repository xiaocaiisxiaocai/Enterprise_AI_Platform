# 20 架构决策记录（ADR 登记簿）

> 文档状态：**Planned** ｜ 决策基线日期：2026-07-22 ｜ 实现/生产验证：**无** ｜ Owner：平台架构负责人（待正式指派）

本登记簿记录目标设计，不证明相关能力已经实现。每项决策在进入开发前仍须由架构、安全、数据和业务 Owner 审批；发生复审条件时，必须新增替代记录或更新状态，不能静默改变结论。

## 1. 状态与字段约定

状态：`Proposed`（尚无推荐方向）、`Planned`（已有推荐的目标方向，但尚未审批或实现）、`Accepted`（已由责任人审批）、`Implemented`（已实现）、`Verified`（已用证据验证）、`Superseded`（被替代）、`Rejected`（不采用）。当前登记项统一为 **Planned**；各条目的“决策”字段表示待审批的推荐方案，不等同于 Accepted，也不构成实现证明。

每项 ADR 至少包含背景、选项、决策、后果、验证和复审条件。外部项目只是证据或实现候选，不能替代本平台的权限、租户和安全边界。

| ID | 决策主题 | 状态 |
|---|---|---|
| ADR-001 | 一期采用模块化单体 | Planned |
| ADR-002 | 使用 DDD 维护业务边界 | Planned |
| ADR-003 | Agent Runtime 采用可持久图式执行与框架隔离 | Planned |
| ADR-004 | 一期采用 PostgreSQL + pgvector | Planned |
| ADR-005 | 建立模型 Gateway 与供应商适配层 | Planned |
| ADR-006 | 建立语言中立 AI Enablement Contract/SDK | Planned |
| ADR-007 | 高风险副作用采用 Human-in-the-loop | Planned |
| ADR-008 | 知识采用版本化、策略驱动治理 | Planned |
| ADR-009 | MCP 基线固定为 2025-11-25 | Planned |
| ADR-010 | Evaluation 作为发布阻断门禁 | Planned |
| ADR-011 | 部署单元、异步任务和耐久工作流分层 | Planned |
| ADR-012 | 多租户、身份委托和权限交集模型 | Planned |
| ADR-013 | Skill 学习采用受控供应链，不直接复制个人 Agent 模型 | Planned |
| ADR-014 | A2A 只用于受控 Agent 联邦，不替代 MCP/Tool | Planned |
| ADR-015 | Policy Enforcement 与关系/属性授权分层 | Planned |
| ADR-016 | OpenTelemetry 为观测规范，评测后端可替换 | Planned |

## ADR-001：一期采用模块化单体

**状态**：Planned

**背景**：领域和团队边界仍在演进，直接拆分微服务会提前承担网络一致性、独立部署、数据复制和运维成本。

**选项**：单体无模块边界；模块化单体；完整微服务。

**决策**：一期使用模块化单体。Agent、Knowledge、Tool、Workflow、Governance、Evaluation 是代码与数据所有权的逻辑模块；Web/API 与 Worker 可以独立扩缩，但共享版本化制品，不能据此称为微服务。

**后果**：部署、调试和事务管理更简单，但必须通过模块依赖测试、Schema 所有权、内部 API 和事件边界避免“大泥球”。模块故障可能扩大进程影响面。

**验证与复审**：持续检查循环依赖、跨模块表访问、构建/发布耦合和故障影响。只有某模块在团队所有权、独立扩缩、发布频率、合规隔离或可用性方面形成持续且量化的独立需求时，才评估拆分。

## ADR-002：使用 DDD 维护业务边界

**状态**：Planned

**背景**：身份、Agent、知识、Tool、流程、治理、Memory 和 Evaluation 具有不同语言、生命周期与一致性要求。

**选项**：按技术层组织；按 CRUD 实体组织；按限界上下文组织。

**决策**：采用 DDD 的限界上下文、聚合、不变量和领域事件；以 `14_DDD详细领域模型.md` 为上下文基线。DDD 不要求每个上下文成为独立服务。

**后果**：业务规则和责任更清晰，但需要维护 Context Map、统一语言和事件契约；简单 CRUD 不强制套用复杂模式。

**验证与复审**：架构测试阻止跨模块直接访问内部类型/表；领域事件有 Owner 和版本。若模型只增加样板而未保护任何不变量，应简化局部实现，而非放弃边界。

## ADR-003：Agent Runtime 采用可持久图式执行与框架隔离

**状态**：Planned

**背景**：长任务需要暂停、审批、检查点、恢复、取消、Trace 和确定版本；简单循环无法可靠覆盖这些语义。

**选项**：自制无状态 Agent Loop；直接把某框架类型作为领域模型；通过内部 Port 适配图式/Agent 框架。

**决策**：领域层采用 `15_Agent状态机设计.md` 的持久状态机和节点图；框架位于 Adapter 后。优先验证 Microsoft Agent Framework 作为 .NET 新实现候选；LangGraph 的 checkpoint/graph 机制和 OpenAI Agents SDK 的 handoff/guardrail/trace 用作对照，不把任一框架对象写入领域契约。AutoGen 已进入维护模式，不作为新平台核心选型。

**外部依据**：[Microsoft Agent Framework](https://github.com/microsoft/agent-framework)、[LangGraph](https://github.com/langchain-ai/langgraph)、[OpenAI Agents SDK](https://github.com/openai/openai-agents-python)、[AutoGen](https://github.com/microsoft/autogen)（核查日期：2026-07-22）。

**后果**：获得可恢复执行并降低框架锁定，但需实现 Port、状态映射和框架升级兼容测试；抽象层不能掩盖框架不支持的语义。

**验证与复审**：用崩溃恢复、双 Worker、审批过期、取消、重复副作用和版本升级场景做 Spike。候选框架若无法满足状态语义、许可证、安全或运维门槛，则替换 Adapter，不改变领域状态。

**PoC 最小证据（VER-ADR-003）**：

- `VER-ADR-003-001`：在检查点后强制终止 Worker，重启后从同一版本快照恢复，不重复已确认副作用；
- `VER-ADR-003-002`：双 Worker 竞争同一执行，过期 `lease_epoch` 的写入被拒绝且状态历史无分叉；
- `VER-ADR-003-003`：Cancel、Deadline 与迟到结果竞态保持合法状态和 `result_certainty`，未知结果进入对账而非盲重试；
- `VER-ADR-003-004`：领域状态、持久化记录和测试契约不引用候选框架类型，更换 Fake Adapter 后核心状态测试仍通过。

四项均需保存代码提交、场景输入、状态历史、日志/Trace、断言结果和失败记录。演示视频或 happy path 不能使 ADR 进入 Verified。

## ADR-004：一期采用 PostgreSQL + pgvector

**状态**：Planned

**背景**：业务 Metadata、ACL、版本和向量需要事务一致与联合过滤；一期规模和专用向量库收益尚无证据。

**选项**：PostgreSQL + pgvector；独立向量数据库；两套双写。

**决策**：一期使用 PostgreSQL + pgvector，Metadata、ACL、Embedding 和索引版本通过同一 `version_id` 管理；不部署独立 VectorDB，不做双写。

**后果**：降低组件和一致性成本，但向量规模、索引构建和高并发可能受单库限制；必须隔离资源、优化索引并监控数据库饱和。

**验证与复审**：基于真实分布测量召回、p95/p99、索引时长、存储、备份和租户过滤。当批准的容量/SLO 连续两个评测周期无法满足，且分区/索引/扩容无效时，才比较专用向量库；迁移需双读验证、回滚和 ACL 一致性证明。

**PoC 最小证据（VER-ADR-004）**：

- `VER-ADR-004-001`：至少两个隔离 Tenant/主体的合成权限图中，未授权向量、详情和缓存命中均为 0；
- `VER-ADR-004-002`：保存代表性查询的 `EXPLAIN (ANALYZE, BUFFERS)`，证明 Tenant/ACL 约束进入检索计划，而不是召回后在应用层补过滤；
- `VER-ADR-004-003`：在批准的数据分布与并发下报告 Recall、p95/p99、索引时间、存储和数据库饱和点，不只报告平均延迟；
- `VER-ADR-004-004`：撤权、删除、备份恢复后再删除以及索引重建均保持版本和 ACL 一致。

未批准工作负载或只使用玩具数据时，性能结论必须标记 Blocked/Exploratory；它只能证明查询语义，不能证明生产容量。

## ADR-005：建立模型 Gateway 与供应商适配层

**状态**：Planned

**背景**：模型供应商在认证、API、地域、日志、配额、错误、成本和能力上不同，业务模块不应直接耦合供应商 SDK。

**选项**：各模块直连；自建统一接口；采用兼容代理并保留内部 Port。

**决策**：建立内部 Model Gateway Port，统一身份、路由、预算、限流、重试、数据策略、Trace 和错误；供应商名称不进入领域模型。LiteLLM 作为代理/适配候选进行安全与兼容 Spike，而不是未经评审直接成为信任边界。

**外部依据**：[LiteLLM 官方仓库](https://github.com/BerriAI/litellm)（核查日期：2026-07-22）。

**后果**：集中治理并支持替换模型，但 Gateway 可能成为瓶颈和高权限组件；降级模型可能改变质量、合规和数据驻留，不能只按价格自动切换。

**验证与复审**：验证流式、Tool call、结构化输出、错误映射、数据不留存、地域、故障切换和成本准确性。任何 fallback 都必须关联 Evaluation 证据和显式策略。

**详细契约**：[23 Model Gateway契约设计](23_Model_Gateway契约设计.md)。该文档仍为 Planned；只有 ADR-005 Accepted 且契约测试通过后才能成为实现基线。

## ADR-006：建立语言中立 AI Enablement Contract/SDK

**状态**：Planned

**背景**：企业系统技术栈不同，若仅提供语言装饰器，会造成权限、风险和事件语义漂移。

**选项**：各系统自定义 API；只提供单语言 SDK；先定义语言中立 Contract，再生成/实现 SDK。

**决策**：以 `17_AI_Enablement_SDK规范.md` 的 Tool/Event/Knowledge Contract 和 conformance suite 为事实源，Python、C#、Java、JavaScript SDK 只是等价封装。SDK 不自动推断权限、风险或审批。

**后果**：跨语言一致、可做兼容验证，但 Contract 演进和代码生成需要专门治理；各语言 GA 时间可能不同。

**验证与复审**：同一 Contract 在各 SDK 产生一致 Schema、错误、身份和 Trace；破坏性变更必须升主版本并通过新旧兼容测试。

## ADR-007：高风险副作用采用 Human-in-the-loop

**状态**：Planned

**背景**：付款、删除、权限变更、外发和生产控制等动作具有不可逆、安全或合规影响。

**选项**：完全自动；所有动作人工批准；按风险和策略决定审批。

**决策**：采用分级审批。高风险或策略命中的副作用必须展示动作预览、影响范围、依据、幂等键和有效期，由有权且职责分离的人员批准。参数、权限、风险或策略快照变化使审批失效。

**后果**：降低误操作风险并可追责，但增加等待时间和审批疲劳；低风险只读操作可由策略自动允许。

**验证与复审**：测试自批、重放旧审批、参数篡改、审批过期、撤权和 Audit 故障。未经有效审批的高风险执行目标必须为 0。

## ADR-008：知识采用版本化、策略驱动治理

**状态**：Planned

**背景**：来源可信不代表内容有效；解析错误、ACL 丢失、版本冲突和提示注入会把错误扩大到所有 Agent。

**选项**：上传即发布；固定全人工；根据来源、分类、用途、质量和安全证据选择审核路径。

**决策**：采用 `16_Knowledge数据治理设计.md` 的不可变版本、来源血缘、ACL 继承、策略审核、隔离和撤回。自动发布只允许低风险且持续通过评测的类别。文档解析器置于沙箱并输出统一中间结构；Docling 作为多格式解析候选之一，不作为治理或正确性的替代。

**外部依据**：[Docling 官方仓库](https://github.com/docling-project/docling)（核查日期：2026-07-22）。

**后果**：可追溯、可撤回且更安全，但增加审核和版本存储成本；Owner 和 ACL 撤权传播成为生产依赖。

**验证与复审**：以版面、表格、OCR、ACL、冲突、恶意文档和撤回语料验证。解析器升级必须跑回归并保留旧版本重建能力。

## ADR-009：MCP 基线固定为 2025-11-25

**状态**：Planned

**背景**：MCP 可标准化 Tool、Resource 和上下文交换，但协议兼容不自动提供企业身份、租户、审批或远端 Server 信任。

**选项**：私有协议；跟随 latest；固定稳定版并受控升级。

**决策**：首个兼容基线固定为 **MCP 2025-11-25**。面向外部消费者时，平台主要作为受管 MCP Server；平台 Agent 调用经批准的外部 MCP Server 时，Agent Runtime 承担 Host，并为每个远端 Server 建立隔离 Client。两种角色使用不同入口、凭据、会话和审计策略。所有调用仍经过身份委托、Tenant、Policy、Approval、限流和 Audit；客户端兼容按测试矩阵逐项声明。

**外部依据**：[Model Context Protocol 官方仓库](https://github.com/modelcontextprotocol/modelcontextprotocol)（核查日期：2026-07-22）。

**后果**：获得标准连接和明确测试基线，但需维护版本协商、Schema 转换、远端内容隔离和安全补丁升级；不能宣称所有 MCP 客户端天然兼容。

**验证与复审**：覆盖 Tool/Resource、认证、会话隔离、取消、超时、恶意 Server、Schema 变化和跨租户拒绝。新规范只有通过兼容、安全和回滚评审才替代基线。

## ADR-010：Evaluation 作为发布阻断门禁

**状态**：Planned

**背景**：代码测试不能证明模型、Prompt、检索、Agent、Skill 或 Policy 变更仍满足业务质量与安全。

**选项**：只看线上反馈；发布后抽检；离线回归 + 安全红队 + Canary + 线上监控。

**决策**：任何 Model、Prompt、Agent、Skill、Knowledge 策略、检索、Tool Contract 或 Policy 变更都绑定版本化 Evaluation Run。门禁至少覆盖任务成功、groundedness、引用正确、安全违规、越权 Tool、拒答、延迟和成本；失败阻断发布。

**后果**：降低无证据发布风险，但需要高质量数据集、评测器校准和人工仲裁；自动评测分数不能单独代表真实业务价值。

**验证与复审**：数据集有来源、租户/敏感处理、Owner 和更新记录；评测器与人工标注定期校准。线上异常自动关联发布版本并触发回滚或 Kill Switch。

## ADR-011：部署单元、异步任务和耐久工作流分层

**状态**：Planned

**背景**：短时后台作业、领域事件和跨天审批流程有不同可靠性需求；Kafka、Redis Queue 或 Workflow Engine 不能混为同一概念。

**选项**：全部同步；Redis 统一承载；PostgreSQL Outbox + Worker；所有任务直接引入耐久工作流平台。

**决策**：一期 Web/API 与 Worker 使用模块化单体制品。业务状态和 Outbox 在 PostgreSQL 同事务提交，Worker 以租约和幂等执行文档处理等有界作业；Redis 不作事实源。Workflow 通过 Port 隔离，Temporal 是跨服务、长时间、强恢复工作流的首个 Spike 候选，未达到引入门槛前不作为一期必需组件。

**外部依据**：[Temporal 官方仓库](https://github.com/temporalio/temporal)（核查日期：2026-07-22）。

**后果**：一期依赖更少且语义清晰，但 Outbox/Worker 有吞吐和调度上限；未来引入 Workflow Engine 需运维额外控制面和迁移在途实例。

**验证与复审**：当流程跨天、定时器/人工节点密集、跨系统补偿复杂，或现有恢复/SLO 连续不达标时，执行 Temporal Spike；必须证明重放确定性、升级、租户隔离、可观测和灾备后再接受。

**PoC 最小证据（VER-ADR-011）**：

- `VER-ADR-011-001`：业务状态与 Outbox 在同一事务提交，事务失败时两者均不可见；
- `VER-ADR-011-002`：重复、乱序、租约过期和 Worker 崩溃后重投不会重复已确认业务效果；
- `VER-ADR-011-003`：毒消息进入可审计隔离/死信路径，修复与重放不覆盖原失败证据；
- `VER-ADR-011-004`：用有界后台作业证明 Outbox + Worker 的恢复边界；只有出现长等待、密集 Timer/HITL 或复杂补偿证据时才启动 Temporal 对照 Spike。

一次端到端演示可以承载三项 ADR 的场景，但每个 ADR 必须拥有独立 Verification/Evidence 和判定，不能以“整体 PoC 成功”批量关闭。

## ADR-012：多租户、身份委托和权限交集模型

**状态**：Planned

**背景**：Agent 代人执行会跨越用户、Agent、Tool、数据和租户边界，仅用 RBAC 或服务账号会产生权限放大。

**选项**：共享服务账号；只看用户角色；用户授权、Agent Grant、Tool Scope、数据 ACL 和策略上下文取交集。

**决策**：所有实体、缓存、事件、Trace 和索引携带 Tenant；Tenant 来自受信身份映射。人员、Service 和 Agent 使用不同 Principal；Agent 通过短期委托代表用户执行，有效权限取四层授权交集，高风险动作再加审批。

**后果**：最小权限和归责更可靠，但权限计算、缓存失效和撤权传播更复杂；授权服务成为关键依赖，失败时写/高风险操作必须 fail closed。

**验证与复审**：建立跨租户、混淆代理、缓存污染、撤权、离职、审批失效和备份恢复测试；跨租户泄露与未经授权动作目标为 0。

## ADR-013：Skill 学习采用受控供应链，不直接复制个人 Agent 模型

**状态**：Planned

**背景**：个人 Agent 项目展示了本地 Skill、Memory 和自我改进体验，但企业平台面向多租户和高风险系统，不能让运行中 Agent 自行生成并发布代码或权限。

**选项**：禁止动态 Skill；运行中自动学习并立即生效；把生成结果作为候选，经过供应链门禁后发布。

**决策**：Skill 生命周期固定为 **Candidate→Evaluating→Reviewing→Canary→Published**，异常或替代状态为 **Rejected / Quarantined / RolledBack / Superseded**。Reviewing 包含代码/依赖/Secret/许可证扫描、沙箱测试、权限差异、安全评测和人工审核；任何自动生成或修改只产生不可执行 Candidate，发布物必须签名且权限不得超出宿主 Agent。

OpenClaw 的单用户/个人助手信任模型不直接采用，仅参考其 Skill/Tool 使用体验，并以其安全与威胁模型识别边界。Hermes Agent 的 Skill 管理和学习机制仅作为 Candidate 生成参考，禁止绕过企业门禁。

**外部依据**：[OpenClaw](https://github.com/openclaw/openclaw)、[OpenClaw 安全模型](https://github.com/openclaw/openclaw/blob/main/SECURITY.md)、[OpenClaw 威胁模型](https://github.com/openclaw/openclaw/blob/main/docs/security/THREAT-MODEL-ATLAS.md)、[Hermes Agent](https://github.com/NousResearch/hermes-agent)、[Hermes 安全文档](https://github.com/NousResearch/hermes-agent/blob/main/website/docs/user-guide/security.md)、[Hermes Skill 管理实现](https://github.com/NousResearch/hermes-agent/blob/main/tools/skill_manager_tool.py)（核查日期：2026-07-22）。

**后果**：保留学习效率且可审计、可回滚，但降低即时自我修改速度并增加评测成本。必须建设隔离构建环境和签名验证链。

**验证与复审**：测试恶意 Skill、依赖投毒、越权权限、隐藏网络访问、提示注入、自修改、Canary 异常和一键回滚。任何未签名/未批准版本不得执行。

## ADR-014：A2A 只用于受控 Agent 联邦，不替代 MCP/Tool

**状态**：Planned

**背景**：MCP 解决上下文、Tool 和 Resource 连接；A2A 面向 Agent 之间的能力发现、任务委托和协作，信任与生命周期不同。

**选项**：统一私有 Agent API；用 MCP 模拟所有 Agent 协作；为远程 Agent 建立受控 A2A Gateway。

**决策**：一期不把 A2A 作为核心依赖，只预留内部任务/结果契约。需要跨平台 Agent 联邦时，通过 A2A Gateway 接入，远端 Agent 视为外部不可信执行者；每次委托设置能力白名单、数据最小化、预算、截止时间、回调验证和审计。A2A 不得绕过 Tool Runtime、Policy 或 Approval。

**外部依据**：[A2A 官方仓库](https://github.com/a2aproject/A2A)（核查日期：2026-07-22）。

**后果**：避免过早扩大攻击面并保留互操作路径；未来需实现 Agent Card 验证、身份联邦、任务取消、结果溯源和协议升级。

**验证与复审**：只有明确跨组织/跨平台用例且私有 Contract 成本不可接受时启动；用伪造 Agent、重放、超额预算、数据外泄和取消失败场景验证。

## ADR-015：Policy Enforcement 与关系/属性授权分层

**状态**：Planned

**背景**：企业授权既有组织/资源关系，也有时间、设备、数据分类、风险和金额等上下文规则；策略必须独立版本、可解释并在执行点强制。

**选项**：业务代码散落判断；只用 RBAC；关系授权 + 属性/风险策略 + 统一 PEP。

**决策**：Runtime、Knowledge Retrieval 和 Adapter 是 Policy Enforcement Point。关系授权模型与上下文策略通过内部 Policy Port 隔离；OpenFGA 作为关系授权候选，OPA 作为属性/风险策略候选。即使同时采用，两者也只提供决策证据，最终以权限交集和本地 fail-closed 规则执行。

**外部依据**：[OpenFGA](https://github.com/openfga/openfga)、[Open Policy Agent](https://github.com/open-policy-agent/opa)（核查日期：2026-07-22）。

**后果**：策略集中、可测试、可解释，但双引擎会增加模型同步、延迟和运维成本；一期必须以真实授权图证明同时引入的必要性。

**验证与复审**：使用策略单测、决策差异测试、影子评估、撤权传播和故障注入。策略变更版本化、双人审批、Canary 并可立即回滚。

## ADR-016：OpenTelemetry 为观测规范，评测后端可替换

**状态**：Planned

**背景**：Agent Trace、模型调用、检索、Tool、审批和成本需跨组件关联，但把业务契约绑定某个观测产品会产生锁定和敏感数据扩散。

**选项**：各组件私有日志；直接绑定单一 LLM Observability 产品；使用 OpenTelemetry/内部评测 Schema，再选择后端。

**决策**：OpenTelemetry Trace Context 和受控内部事件 Schema 是规范事实源；原始 Prompt/Tool 载荷默认不采集。Langfuse 与 Phoenix 作为互斥的观测/评测后端候选，必须经部署、权限、脱敏、保留和成本 Spike 后选择一个；Promptfoo 作为 CI 质量/安全回归候选，不承担生产 Trace 事实源。

**外部依据**：[Langfuse](https://github.com/langfuse/langfuse)、[Phoenix](https://github.com/Arize-ai/phoenix)、[Promptfoo](https://github.com/promptfoo/promptfoo)（核查日期：2026-07-22）。

**后果**：信号可移植、职责清晰，但需维护语义映射、采样和脱敏；后端未选定前必须先完成最小 Dashboard、告警和 Evaluation 结果存储设计。

**验证与复审**：比较 Trace 完整性、租户隔离、字段级脱敏、自托管、保留/删除、规模、成本和导出能力。任何后端都不得成为授权或执行状态的事实源。

## 2. 全局复审与追踪规则

- 每项 ADR 必须链接对应需求、设计、实现、测试/Evaluation 和 Runbook；当前实现链接为空表示尚未交付。
- 框架、协议、模型和开源项目升级不得直接改变领域契约，必须通过兼容、安全、许可证、质量、成本和回滚评审。
- 出现跨租户事件、未经审批的高风险动作、SLO/RPO/RTO 连续不达标、许可证变化或上游停止维护时，立即触发相关 ADR 复审。
- 新证据否定现有假设时，应将原记录标为 Superseded 并保留历史，不删除不利结论。
