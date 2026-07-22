# 05 API 接口设计

> 状态：**Planned（目标设计，尚未实现）**
>
> HTTP 契约：OpenAPI 3.x 文件为实施阶段事实源
>
> MCP 协议基线：**2025-11-25**

## 1. 目标与非目标

API 是 Enterprise AI Operating Platform 的外部契约，目标是提供稳定、可版本化、可鉴权、可审计、可幂等和可异步恢复的接口。

本文定义资源形态和关键语义，不代替最终 OpenAPI/JSON Schema；示例字段是目标契约，不构成实现证据。GraphQL、任意版本 MCP 兼容和公开 A2A 接口不属于 Phase 0 承诺，需独立 ADR 与安全评审。

## 2. 协议与入口

| 入口 | 用途 | 目标协议 |
|---|---|---|
| Public/Enterprise API | 用户、业务应用、管理控制面 | HTTPS + REST/JSON + OpenAPI |
| Execution Event Stream | Agent/Workflow 长任务进度 | Server-Sent Events（SSE） |
| Webhook | 服务端异步通知 | HTTPS + 签名 + 重放防护 |
| MCP Server | 工具、资源、Prompt 互操作 | MCP `2025-11-25`，远程采用该版本规范支持的 Streamable HTTP |
| Internal Contract | 一期模块化单体内部调用 | 进程内 Application Port，不绕行 HTTP |

API Gateway 与 Model Gateway 必须分离：本文件定义前者的客户端契约；模型供应商协议由 Model Gateway Adapter 封装。

## 3. 通用约定

### 3.1 URL 与版本

- 基础路径：`/api/v1`；资源使用小写复数和稳定 ID。
- 破坏性变更升级主版本；新增可选字段、枚举值的兼容策略必须在 SDK 中验证。
- 响应中未知字段应被客户端忽略；服务端不得在同一版本改变字段含义。
- 弃用接口返回标准弃用/下线信息，并提供迁移窗口；安全紧急撤销可缩短窗口但必须审计。

### 3.2 媒体类型与时间

- 默认 `application/json; charset=utf-8`；SSE 使用 `text/event-stream`。
- 时间使用 RFC 3339 UTC；ID 使用不含业务语义的 UUID。
- 文件、长 Prompt 和大结果使用已授权的 `artifact_id`，不接受服务端任意抓取客户端 URL，防止 SSRF。
- 请求体、附件、字段深度和单字段长度必须有限制；超限返回 `413` 或明确验证错误。

### 3.3 请求头

| Header | 要求 |
|---|---|
| `Authorization` | OIDC/OAuth Bearer 或获批服务身份；不得记录明文 Token |
| `traceparent` / `tracestate` | 遵循 W3C Trace Context；缺失时 Gateway 创建 |
| `Idempotency-Key` | 创建 Execution、Tool 副作用、Workflow Signal 等命令必填 |
| `If-Match` | 更新可变控制面资源时携带 ETag，防止丢失更新 |
| `Last-Event-ID` | SSE 断线续传；服务端验证该游标属于同 Tenant/Execution |

Tenant 由验证后的 Claims、客户端注册或服务绑定推导，不接受自由 `X-Tenant-Id` 或请求体字段扩大访问范围。

## 4. 身份、授权与审计

1. API Gateway 验证 Issuer、Audience、签名、有效期和撤销状态，并映射 `tenant_id + principal_id`。
2. 每个端点声明资源、动作和 Scope；未命中 Policy、PDP 不可用或身份不完整时默认 `Deny`。
3. Gateway PEP 只负责入口决定；Knowledge、Model、Tool、Memory 和 Workflow 在实际动作点再次执行 PEP。
4. Tool 每次调用以最终 `tool_version_id + arguments_hash` 重新决策，结果仅为：
   - `Allow`
   - `Deny`
   - `RequireApproval`
   - `RequireStepUpAuth`
5. Approval 或 Step-up 完成后必须携带证据重新决策；不能将中间结果视为永久 Allow。
6. 所有命令记录 Principal、服务主体、Tenant、PolicyDecision、版本快照、Trace、结果和脱敏摘要。

详细规则见 [10 Governance与Security设计](10_Governance_Security设计.md)。

## 5. 幂等、并发与异步语义

### 5.1 幂等

- 相同 Tenant、操作、主体和 `Idempotency-Key`，请求摘要相同则返回原资源；摘要不同返回 `409 Conflict`。
- 幂等记录保留期必须覆盖客户端最大重试窗口和外部系统对账窗口。
- `GET` 不产生副作用；取消、Signal、审批和 Tool 调用均有独立幂等键。

### 5.2 异步资源

长任务创建成功返回 `202 Accepted`、`Location` 和资源快照，不用 `200 running` 伪装同步完成。资源可通过 GET 查询、SSE 订阅、Webhook 接收，并具有明确终态。

### 5.3 并发

- 可变控制面资源使用 ETag/`If-Match`；版本资源发布后不可变。
- 状态命令提交预期状态或 ETag；冲突返回 `409`/`412`，不得静默覆盖。
- AgentExecution 状态枚举和转换以 [15 Agent状态机设计](15_Agent状态机设计.md) 为事实源；Workflow/Task/Approval 与 ToolExecution 结果分别以 `09` 和 `08` 为事实源，禁止共用或复制枚举。

## 6. Agent 控制面 API

| 方法与路径 | 用途 | 关键约束 |
|---|---|---|
| `POST /api/v1/agents` | 创建 Agent 草稿 | `agent_key` 在 Tenant 内唯一 |
| `POST /api/v1/agents/{agent_id}/versions` | 创建不可变 AgentVersion | 固定 Prompt/ModelPolicy/ToolBinding/KnowledgePolicy 版本 |
| `POST /api/v1/agent-versions/{version_id}/validate` | 运行 Schema、安全和依赖验证 | 异步 Operation；不等于发布 |
| `POST /api/v1/agent-versions/{version_id}/evaluations` | 启动发布门禁评测 | 固定 SuiteVersion/DatasetVersion |
| `POST /api/v1/agents/{agent_id}/releases` | 提升到环境或回滚 | 必须带 Gate Evidence 和审批；创建新记录 |
| `GET /api/v1/agents/{agent_id}/versions` | 列出版本 | Cursor Pagination |

发布版本不得通过 PATCH 修改；任何 Prompt、模型路由、工具或知识策略变化都创建新版本。

## 7. Agent Execution API

### 7.1 创建执行

```http
POST /api/v1/agents/{agent_id}/executions
Idempotency-Key: 8f1d...
```

```json
{
  "release": "production",
  "input": {
    "content": [
      { "type": "text", "text": "分析设备故障并给出带来源的处置建议" },
      { "type": "artifact_ref", "artifact_id": "uuid" }
    ]
  },
  "session_id": "uuid",
  "deadline_at": "2026-07-22T10:30:00Z",
  "budget": {
    "max_steps": 20,
    "max_input_tokens": 50000,
    "max_output_tokens": 8000,
    "max_cost": "10.00",
    "currency": "CNY"
  },
  "approval_mode": "pause",
  "metadata": {
    "business_reference": "incident-123"
  }
}
```

客户端预算只能收紧服务端/Tenant/Agent Policy，不得放宽。普通生产调用按 Release 解析版本；显式 `agent_version_id` 只向获授权的测试/评测入口开放。

目标响应：

```http
HTTP/1.1 202 Accepted
Location: /api/v1/agent-executions/uuid
```

```json
{
  "execution_id": "uuid",
  "status": "queued",
  "state_reason": "accepted",
  "terminal_reason_code": null,
  "cancellation_status": "none",
  "result_certainty": "not_applicable",
  "intervention_required": false,
  "version_snapshot": {
    "agent_version_id": "uuid",
    "prompt_version_id": "uuid",
    "model_policy_version_id": "uuid",
    "tool_binding_version_id": "uuid",
    "knowledge_policy_version_id": "uuid",
    "snapshot_hash": "sha256:..."
  },
  "trace_id": "...",
  "created_at": "2026-07-22T10:00:00Z",
  "links": {
    "self": "/api/v1/agent-executions/uuid",
    "events": "/api/v1/agent-executions/uuid/events",
    "cancel": "/api/v1/agent-executions/uuid/cancel"
  }
}
```

### 7.2 查询、取消和事件

| 方法与路径 | 语义 |
|---|---|
| `GET /api/v1/agent-executions/{id}` | 状态、原因码、取消传播、结果确定性、未解析 ToolExecution/对账链接、预算、Artifact 和错误摘要 |
| `POST /api/v1/agent-executions/{id}/cancel` | 记录幂等取消请求并返回 `202` 与当前状态；不承诺响应时已进入 Cancelled，也不掩盖在途或结果未知副作用 |
| `GET /api/v1/agent-executions/{id}/events` | SSE 事件流；支持 `Last-Event-ID`、心跳和断线重连 |
| `POST /api/v1/agent-executions/{id}/feedback` | 提交评分/分类反馈，不直接修改 Agent |

SSE 每条事件含 `event_id`、`sequence`、`type`、`occurred_at`、`trace_id` 和脱敏 `data`。断档或超出保留期返回可识别错误，客户端回退到 GET，不得假定事件流恰好一次投递。

## 8. Knowledge API

### 8.1 Gate F 本地 PoC（非规范接口）

`src\EnterpriseAI.Poc` 暂时暴露 `POST /api/v1/query`，只用于本地回归。请求体仅包含 `question`，测试身份来自 `X-Poc-User`；Tenant 固定为企业安全域，任何请求体 `tenantId` 都返回 `400`。响应只返回权限过滤后的抽取式证据、固定版本/位置引用或统一拒答。

该端点不属于目标生产契约，不支持真实认证、向量检索、模型生成或业务 SLA。Gate P 必须迁移到本节后续定义的 Knowledge API 和受信身份上下文，禁止保留 `X-Poc-User`。

### 8.2 目标检索接口

```http
POST /api/v1/knowledge-searches
```

```json
{
  "query": "E302 故障如何处理？",
  "knowledge_base_ids": ["uuid"],
  "filters": {
    "document_types": ["maintenance_manual"],
    "published_after": "2025-01-01T00:00:00Z"
  },
  "retrieval": {
    "mode": "hybrid",
    "top_k": 8,
    "rerank": true
  }
}
```

- 客户端 KnowledgeBase/Filter 只用于缩小范围；服务端必须与 KnowledgePolicy、Tenant、ACL、分类和数据区域取交集。
- `top_k`、候选数和查询长度受预算/配额限制。
- 禁止先全 Tenant 向量召回再在应用层过滤无权结果。

```json
{
  "search_id": "uuid",
  "items": [
    {
      "chunk_id": "uuid",
      "document_id": "uuid",
      "document_version_id": "uuid",
      "title": "设备维护手册",
      "content": "已授权的命中片段",
      "locator": { "page": 42, "section": "5.3" },
      "scores": { "retrieval": 0.82, "rerank": 0.91 },
      "embedding_space_id": "uuid",
      "citation_id": "uuid"
    }
  ],
  "trace_id": "..."
}
```

分数仅在同一检索配置/Embedding Space 内解释，不承诺跨模型归一。检索设计见 [07 Knowledge Platform设计](07_Knowledge_Platform设计.md)。

## 9. Tool API

### 9.1 控制面

| 方法与路径 | 用途 |
|---|---|
| `POST /api/v1/tools` | 创建 Tool 草稿 |
| `POST /api/v1/tools/{tool_id}/versions` | 创建不可变 ToolVersion |
| `POST /api/v1/tool-versions/{id}/validate` | Schema、连通性、权限、网络与安全验证 |
| `POST /api/v1/tool-versions/{id}/publish` | 审批后发布 |
| `POST /api/v1/tool-versions/{id}/revoke` | 紧急撤销并触发 Kill Switch/绑定检查 |

### 9.2 执行

```http
POST /api/v1/tool-versions/{tool_version_id}/executions
Idempotency-Key: 3ca9...
```

```json
{
  "arguments": {
    "work_order_id": "WO-123",
    "new_status": "closed"
  },
  "agent_execution_id": "uuid",
  "agent_step_id": "uuid",
  "dry_run": false
}
```

服务端规范化参数并计算 `action_hash` 后再次调用 PDP：

| 决定 | API 行为 |
|---|---|
| `Allow` | 执行 obligations 后创建 ToolExecution；副作用异步时返回 `202` |
| `Deny` | 返回 `403` Problem Details，记录 Decision/Audit，不泄露策略内部表达式 |
| `RequireApproval` | 创建 ApprovalTask，返回 `202` 及等待资源；批准后重新决策 |
| `RequireStepUpAuth` | 返回 `401` 与受控认证 Challenge；完成后绑定 ActionHash 并重新决策 |

`dry_run` 仍需鉴权，且只有 ToolVersion 明确支持时可用。超时后外部结果不确定时，ToolExecution 记录 `result_state=ResultUnknown`，关联 AgentExecution 保持 `WaitingExternal`；禁止把超时简单映射为可重试失败。

| 方法与路径 | 语义 |
|---|---|
| `GET /api/v1/tool-executions/{id}` | 查询 Tool 调用生命周期、`result_state`、幂等键、对账任务、脱敏结果和解析证据 |

`ResultUnknown` 对客户端固定为 `retryable=false`。只有平台对账确认请求未执行，或 Tool 契约证明同一幂等键可安全重放后，才可创建新的受审计重试尝试；客户端不得自行推断。

## 10. Workflow 与 Approval API

| 方法与路径 | 语义 |
|---|---|
| `POST /api/v1/workflows/{workflow_id}/instances` | 按已发布 WorkflowVersion 创建实例 |
| `GET /api/v1/workflow-instances/{id}` | 查询实例、任务、等待和补偿状态 |
| `POST /api/v1/workflow-instances/{id}/signals` | 发送幂等 Signal |
| `POST /api/v1/workflow-instances/{id}/cancel` | 异步记录取消意图并返回 `202`；停止创建新任务，已开始的调用、对账和补偿按状态机继续安全收敛，不承诺响应时已终止 |
| `GET /api/v1/approval-tasks` | 按授权范围列出待办，Cursor Pagination |
| `POST /api/v1/approval-tasks/{id}/decisions` | 批准/拒绝；需 ETag、幂等键和理由 |

Approval 响应必须展示具体动作摘要、参数哈希、风险、请求者、策略依据、有效期和职责分离要求；参数变化或任务过期后原批准失效。

## 11. 列表、过滤与查询

- 使用不透明 Cursor，不暴露可修改 SQL offset；Cursor 绑定 Tenant、Principal、过滤条件和排序。
- 默认、最大 `page_size` 在 OpenAPI 中明确；服务端可下调。
- 过滤字段使用 allowlist 和类型化 Schema，禁止透传 SQL/表达式。
- 稳定排序至少含唯一 ID 作为次序键。
- 搜索/导出等昂贵查询受单独 Scope、预算、速率和审计约束。

## 12. 错误模型

HTTP 错误采用 Problem Details 风格，并保留稳定业务 `code`：

```json
{
  "type": "https://docs.example.invalid/problems/policy-denied",
  "title": "请求未获策略允许",
  "status": 403,
  "detail": "当前主体无权执行该动作",
  "instance": "/api/v1/tool-executions/uuid",
  "code": "POLICY_DENIED",
  "trace_id": "...",
  "retryable": false,
  "errors": [
    { "field": "arguments.new_status", "code": "VALUE_NOT_ALLOWED" }
  ]
}
```

- `detail` 不泄露 Policy 源码、Secret、内部堆栈或其他 Tenant 资源存在性。
- `retryable` 只表达当前错误类别；客户端仍须遵守 `Retry-After`、截止时间和幂等要求。
- 常见稳定代码至少覆盖：身份无效、策略拒绝、需审批、需增强认证、版本冲突、预算耗尽、截止超时、资源撤销、结果未知、速率限制和依赖不可用。

## 13. Webhook

- Subscription 固定 Tenant、事件类型、目标 allowlist、Secret Reference 和有效期；
- 每次投递包含 Event ID、时间戳、内容摘要签名和 Trace；接收方校验时间窗并去重；
- 重试指数退避且有上限，永久失败进入 Dead Letter 和人工处置；
- 重放必须产生审计，不能修改原 Event；
- Webhook 载荷默认只含资源引用和最小摘要，接收方再通过授权 API 获取详情。

## 14. MCP 接口基线

### 14.1 版本与生命周期

- Phase 0 仅承诺 MCP `2025-11-25`；握手通过 `initialize` 协商协议版本和 capabilities。
- 客户端请求不受支持版本时明确返回支持范围，不静默按其他版本解释。
- 初始化完成前不接受业务调用；Session ID 仅用于关联，不作为认证凭据。
- MCP Server 通过 API Gateway/专用受控入口接入 OIDC/OAuth 授权，并映射 Tenant 与 Principal。

### 14.2 能力映射

| MCP Primitive | 平台映射 | 企业约束 |
|---|---|---|
| Tools | 已发布 ToolVersion 的受控视图 | 每次 `tools/call` 重新 PDP 决策；不自动暴露所有内部工具 |
| Resources | 已授权 Knowledge/Artifact 资源 | 读取前 Tenant/ACL/分类过滤；URI 不授予权限 |
| Prompts | 已发布 PromptVersion 的受控视图 | 返回权限和版本受控模板；不得泄露系统 Secret |

原文档中的泛化“Context”不作为 MCP Primitive。Sampling、Elicitation 或其他客户端能力默认关闭，只有在明确业务场景、Capability 协商、策略和人工交互设计完成后逐项启用。

### 14.3 安全与审计

- MCP Tool Name 解析到不可变 `tool_version_id`，名称冲突按 Tenant/命名空间拒绝；
- Tool 参数在 Schema 验证、规范化和 `action_hash` 计算后鉴权；
- 不允许将上游 Token 无审计地透传给下游系统；使用受控 Token Exchange 或服务凭据引用；
- Streamable HTTP 校验来源、Host/Origin、Session 归属、消息大小和超时，防止会话劫持与本地服务暴露；
- JSON-RPC 错误与平台 Problem Details 做稳定映射，但不泄露内部异常；
- MCP 调用产生与 REST 相同的 PolicyDecision、AuditEvent、Trace、预算和 ToolExecution 记录。

## 15. A2A 预留边界

A2A 不属于 Phase 0 公网承诺。未来接入时只复用已发布 Agent Capability，至少固定协议版本、Agent 身份、Tenant、委托 Scope、任务/Artifact 状态、预算、截止时间、Trace、签名和撤销机制。远端 Agent 不因支持 A2A 自动获得本地 Tool、Memory 或 Knowledge 权限。

详细执行委托约束见 [06 Agent Runtime设计](06_Agent_Runtime设计.md)。

## 16. 失败路径

| 失败 | API 目标行为 |
|---|---|
| OIDC/PDP 不可用 | 默认拒绝；返回稳定错误和 Trace，不降级为匿名 |
| Idempotency 存储不可用 | 不执行可能产生副作用的命令 |
| Worker 暂不可用 | 已持久化请求保持 Accepted/Queued 语义；超出容量返回 `429/503 + Retry-After` |
| SSE 断线 | 允许按 Last-Event-ID 续传；过期后回退 GET |
| Tool 结果未知 | ToolExecution 返回 `result_state=ResultUnknown`；AgentExecution 保持 `WaitingExternal`，并提供对账/人工处置链接 |
| Webhook 失败 | 有界重试和 Dead Letter，不回滚已完成业务事务 |
| MCP 版本不兼容 | 握手失败并报告支持版本，不猜测兼容 |
| A2A 对端失联 | 保留远端任务关联，禁止无幂等保证重复委托 |

## 17. OpenAPI 与兼容性门禁

- OpenAPI、JSON Schema、错误代码表和示例随代码版本管理；
- CI 计划执行语法、破坏性变更、Schema 示例和生成 SDK 编译检查；
- Consumer Contract 覆盖内部 Adapter、SDK、MCP 映射和关键客户集成；
- 日志/Trace 验证敏感字段脱敏；
- API 文档显示 Scope、策略动作、幂等、配额、异步状态和错误；
- 实际实现与 OpenAPI 不一致视为缺陷，不能通过手工文档解释规避。

## 18. 评审与验收点

- [ ] Agent Execution 返回 `202 + Location`，支持 GET、取消、SSE 和幂等重放。
- [ ] 任意执行响应可查看五类不可变版本及 `snapshot_hash`。
- [ ] Tenant 只从受信身份上下文解析，跨 Tenant ID/Cursor/SSE 重用均被拒绝。
- [ ] Tool 四类策略决定均有端到端契约场景，Approval/Step-up 绑定 ActionHash。
- [ ] Knowledge 检索在向量召回前应用 ACL，结果含 DocumentVersion 和定位引用。
- [ ] 错误不泄露策略、堆栈、Secret 或跨 Tenant 资源存在性。
- [ ] MCP `2025-11-25` 初始化、能力协商、Tools/Resources/Prompts 和不兼容版本完成测试。
- [ ] Webhook 签名、重放、重试、Dead Letter 和最小载荷完成验证。
- [ ] OpenAPI 兼容性检查、SDK 编译和 Consumer Contract 均产生证据。
- [ ] 取消传播未确认、Deadline 与迟到结果竞态可收敛；`ResultUnknown` 默认不可重试且不会使关联 Agent 提前终态化。

## 19. 关联文档

- 架构与信任边界：[01 总体架构设计](01_总体架构设计.md)
- 数据与幂等记录：[04 数据库模型设计](04_数据库模型设计.md)
- Agent 执行语义：[06 Agent Runtime设计](06_Agent_Runtime设计.md)
- Tool 安全：[08 Tool Platform与AI SDK设计](08_Tool_Platform_AI_SDK设计.md)
- Workflow/Approval：[09 Workflow与Human-in-the-Loop设计](09_Workflow_Human_In_Loop设计.md)
- 状态事实源：[15 Agent状态机设计](15_Agent状态机设计.md)

## 20. 参考来源及吸收点

- [MCP 2025-11-25 规范](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/specification/2025-11-25/index.mdx)：协议版本、初始化/能力协商、Tools、Resources、Prompts 和传输安全的规范基线。
- [A2A](https://github.com/a2aproject/A2A)：参考远端 Agent 能力发现、任务和 Artifact 互操作；本文将其限制为显式身份与最小权限的未来边界。
