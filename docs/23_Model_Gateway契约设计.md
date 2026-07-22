# 23 Model Gateway 契约设计

> 状态：**Planned（目标契约，尚未实现）** ｜ 责任域：Platform / Model Gateway ｜ 关联决策：ADR-005 ｜ 候选实现：自研 Adapter 或经 PoC 的 LiteLLM 等网关，不构成既定依赖

## 1. 目标与边界

Model Gateway 是平台面向模型供应商的唯一受控边界，负责模型目录、能力匹配、策略路由、凭据引用、数据地域/保留约束、预算、限流、重试/Fallback、流式语义、用量计量和供应商错误归一。

它不拥有 Agent 业务目标、Prompt 内容版本、Tool 授权、Knowledge ACL 或最终业务决策。Runtime 只声明能力和受控 Prompt 包；供应商 SDK、Secret、模型别名和价格差异不得进入核心领域模型。

## 2. 不变量

1. 每次调用固定 `model_policy_version_id`、Prompt/输入摘要、数据分类、允许区域、预算和截止时间。
2. Runtime、Agent 和业务模块不直接持有供应商凭据；Gateway 仅使用 Secret Reference 获取短期凭据。
3. Route/Fallback 只能从已批准模型目录选择，不能突破区域、训练/保留、分类、能力和许可证约束。
4. 模型显示名称或可变别名不能充当版本证据；必须记录供应商返回版本、部署 ID、能力指纹或可获得的等价证据。
5. Fallback 必须有该 Route 的评测证据和显式顺序；“供应商可用”不等于“质量与合规兼容”。
6. 流式 Delta 不是最终 Artifact；中断、取消或最终 Schema 校验失败时必须发送明确终止事件。
7. 预算使用“预留—计量—结算—对账”，价格表更新不能改写历史成本。
8. Prompt、模型输出和 Tool 结果默认不写入 Trace/日志正文；审计保存受控引用、摘要和策略事实。

## 3. 领域对象与事实源

| 对象 | 关键内容 | 所有权/约束 |
|---|---|---|
| ModelCatalogEntry | provider、model/deployment、能力、区域、上下文、状态、指纹 | Platform；无 Secret；变更版本化 |
| ModelPolicyVersion | 允许 Route、Fallback、数据策略、预算、重试和内容策略 | Agent/Platform 协作；Published 后不可变 |
| ModelInvocation | 固定请求快照、选路原因、总体状态、结果与用量 | Model Gateway；一次逻辑调用 |
| ModelAttempt | 具体供应商尝试、provider request ID、时间、错误和流式进度 | Model Gateway；仅追加尝试 |
| ModelUsageLedger | token/字符/图片/缓存/费用、价格表和币种 | FinOps；历史不可回写 |
| ModelPriceBookVersion | 单价、计费单位、生效期、来源和币种 | Model Platform/Finance 批准 |
| ProviderCredentialRef | Secret Provider 引用、用途、Tenant/环境/Route Scope | Secret 系统；Gateway 不保存明文 |

## 4. 内部调用契约

Gateway Port 使用语言中立、版本化 Schema。以下 JSON 只展示字段，不代表公开 HTTP API：

```json
{
  "contract_version": "1.0",
  "invocation_id": "inv-uuid",
  "tenant_id": "tenant-a",
  "principal_id": "principal-uuid",
  "agent_execution_id": "exec-uuid",
  "agent_step_id": "step-uuid",
  "model_policy_version_id": "policy-version-uuid",
  "capability_request": {
    "input_modalities": ["text"],
    "output_modalities": ["text"],
    "streaming": true,
    "structured_output": true,
    "tool_calling": false,
    "minimum_context_tokens": 16000
  },
  "data_policy": {
    "classification": "internal",
    "allowed_regions": ["approved-region"],
    "retention_mode": "no_store",
    "training_use_allowed": false
  },
  "prompt_package": {
    "prompt_version_id": "prompt-version-uuid",
    "content_ref": "secure://prompt-package/uuid",
    "content_hash": "sha256:...",
    "trust_segments": ["system", "user", "retrieved_evidence"]
  },
  "response_schema_ref": "schema://answers/v1",
  "limits": {
    "deadline_at": "2026-07-22T10:30:00Z",
    "max_input_tokens": 12000,
    "max_output_tokens": 2000,
    "max_cost_amount": "1.50",
    "currency": "CNY"
  },
  "idempotency_key": "model-business-attempt-key",
  "trace_id": "trace-id"
}
```

校验顺序固定为：契约版本与 Schema → 身份/Tenant → PolicyVersion 与 Route → 数据分类/地域/保留 → 能力与上下文 → 预算/限流/截止时间 → Prompt 包完整性 → 供应商调用。

## 5. 归一化结果

```json
{
  "invocation_id": "inv-uuid",
  "status": "succeeded",
  "completion_state": "complete",
  "selected_route_id": "route-uuid",
  "provider": "approved-provider",
  "provider_model_ref": "provider-model-or-deployment",
  "provider_version_evidence": "provider-version-or-fingerprint",
  "output_ref": "secure://model-output/uuid",
  "output_hash": "sha256:...",
  "finish_reason": "stop",
  "usage_state": "final",
  "usage": {
    "input_tokens": 8000,
    "output_tokens": 700,
    "cached_input_tokens": 0,
    "cost_amount": "0.82",
    "currency": "CNY",
    "price_book_version_id": "price-version-uuid"
  },
  "attempt_count": 1,
  "trace_id": "trace-id"
}
```

`status` 描述调用生命周期：`accepted | running | succeeded | rejected | failed | cancelled`。`completion_state` 独立表示 `none | partial | complete`；`usage_state` 表示 `estimated | final | unknown`。失败或取消可以已经产生 Partial 输出或费用，不能只看 status 推断成本和内容是否暴露。

## 6. 路由决策

Route 选择按硬约束先过滤、软目标后排序：

1. 固定 ModelPolicyVersion，加载允许的 Provider/Model/Region/Retention 组合；
2. 按数据分类、Tenant、用途、供应商条款、Kill Switch 和能力硬过滤；
3. 排除上下文、结构化输出、流式、Tool Calling 等能力不满足者；
4. 仅使用在当前用例和风险等级达到 EvalPolicy 门禁的 Route；
5. 在剩余集合中按质量、供应商健康、延迟预算、成本预算和并发配额排序；
6. 记录候选、排除原因、最终 Route、策略版本和价格表版本。

不得仅按最低价格或模型尺寸路由。动态健康和价格可以改变排序，但不能绕过固定 Policy 与评测门禁；模型别名解析变化视为模型变更，触发冻结、告警和回归。

## 7. Retry、Fallback、幂等与取消

### 7.1 重试条件

- 仅对明确 transient 的连接、429/限流或供应商 5xx，在截止时间、预算和重试上限内尝试；
- 尊重 `Retry-After`，采用退避与抖动；
- 身份、策略、内容过滤、区域、预算和 Schema 错误默认不可重试；
- 结构化输出无效只在 ModelPolicy 明确允许时有限重试，并固定修复 Prompt/Schema 版本；
- 每个 Attempt 追加记录，不能覆盖第一次失败。

### 7.2 Fallback 条件

Fallback 必须同时满足：Policy 显式列出；能力和输出 Schema 兼容；数据地域/保留/训练约束不变；目标 Route 已通过同一用例门禁；剩余预算和 Deadline 足够。

流式已经向客户端发送可见 Delta 后，不自动切换模型并拼接输出。若允许从头重启，必须发送 `stream.restarted`、撤销此前 Partial Artifact 的最终性、创建新 Attempt，并使调用者能够识别模型变化。

### 7.3 幂等与取消

同一 Tenant、ModelPolicyVersion、规范化请求哈希和 Idempotency Key 返回同一 ModelInvocation；Key 相同但请求哈希不同返回冲突。供应商是否支持幂等必须登记，不能假设。

取消是 Best Effort：Gateway 记录请求、向 Provider 传播并记录是否确认；已经返回的 Delta、已计费用量和 Provider 迟到结果仍需对账。取消不能删除审计或成本事实。

## 8. 流式事件

流式传输使用有序事件和稳定 sequence：

| 事件 | 语义 |
|---|---|
| `invocation.started` | Route、Attempt 和策略检查完成；不暴露 Secret |
| `output.delta` | 已通过增量安全检查的 Partial 内容 |
| `usage.estimated` | 供应商尚未给最终账单时的估算 |
| `stream.restarted` | 经策略允许从头创建新 Attempt，前一 Partial 不再可作为最终结果 |
| `invocation.completed` | 最终 Schema/安全校验通过，Artifact 可被标记为最终 |
| `invocation.failed` | 稳定错误、completion_state 和是否允许平台重试 |
| `invocation.cancelled` | 取消已收敛；仍可附 Partial/用量和迟到对账状态 |

客户端断线不等于 Provider 调用取消。Gateway 根据 Policy 决定继续、传播取消或完成后只保存受控 Artifact；事件重连使用游标且不承诺恰好一次。

## 9. 错误分类

| 稳定错误 | 默认重试 | 说明/最低处理 |
|---|---|---|
| `contract_invalid` | 否 | Schema/版本错误；调用者修复 |
| `identity_or_tenant_invalid` | 否 | 默认拒绝并审计，不泄露资源存在性 |
| `model_policy_denied` | 否 | 区域、分类、用途、供应商或 Kill Switch 拒绝 |
| `budget_exhausted` | 否 | 不创建供应商 Attempt |
| `rate_limited` | 有条件 | 尊重 Retry-After、预算和 Deadline |
| `provider_unavailable` | 有条件 | 有界重试或合规 Fallback |
| `timeout_before_output` | 有条件 | 仅在剩余预算/截止时间允许时 |
| `stream_interrupted` | 否/策略化 | 已有 Partial 时不得无提示拼接 Fallback |
| `content_filtered` | 否 | 返回稳定分类，不暴露供应商内部策略 |
| `structured_output_invalid` | 有条件 | 有界修复尝试；失败转人工/明确失败 |
| `provider_auth_failed` | 否 | 隔离 Route、告警 Secret/配置 Owner |
| `usage_unknown` | 否 | 输出结果与费用对账分离；成功输出不因此改写为失败，但必须保留预算预留、创建对账并禁止记为零成本 |

`retryable` 只表示 Gateway 当前策略允许再次尝试，调用者不得自行绕过 Attempt、预算和 Policy 机制。

## 10. 预算、计量与对账

1. 请求前按最大 Token/计费单位和价格表预留预算；预留失败不调用 Provider。
2. 流式中更新估算用量并执行软/硬阈值；硬终止仍遵循 Provider 可取消能力。
3. 完成后使用供应商最终用量结算，释放剩余预留；无最终账单时标记 `usage_state=estimated/unknown`。
4. Provider 账单异步到达时追加对账差异，不修改原价格表和运行事实。
5. 成本归属 tenant、use_case、Agent/Execution、Route、Attempt、环境和版本；人工复核与平台成本由上层 ROI 另行合并。

预算检查与扣减必须防并发超卖；具体一致性方案、货币精度、汇率来源和账单延迟通过 ADR/财务策略固定。

## 11. 数据、凭据与内容安全

- Prompt 包区分 system、user、retrieved evidence、Tool output 等信任段，低信任内容不能提升为系统指令；
- DLP 在构建 Prompt 前、Gateway 出口和响应入口执行；Restricted 默认不得发往未批准外部模型；
- Secret Broker 返回短期、audience/Route/环境受限凭据；凭据不进入请求 Schema、数据库、日志或 Trace；
- 供应商登记训练使用、保留、地域、子处理方、删除和事件通知条款；未经批准 Route 默认不可用；
- 响应在进入 Agent/Tool 前执行大小、内容类型、Schema、恶意内容和敏感字段检查；
- 模型输出不能直接成为 SQL、URL、文件路径、Tool 参数或长期 Memory。

## 12. 持久化映射

| 表 | 必需字段/扩展 | 关键约束 |
|---|---|---|
| `model_catalog` | provider/model/deployment、能力、区域、上下文、指纹、状态、生效期 | 不保存 Secret；别名变化生成新目录版本或漂移事件 |
| `model_policy_versions` | routes、fallback、数据/内容策略、重试、预算、hash | Published 不可变 |
| `model_invocations` | invocation/request hash、policy、route、status、completion/usage state、output ref、trace | Tenant/时间分区；一次逻辑调用 |
| `model_attempts` | invocation、attempt、provider request ID、模型证据、开始/结束、错误、partial hash | 仅追加；Attempt 顺序唯一 |
| `model_usage_ledger` | attempt、计费单位、数量、价格表、币种、估算/最终、差异 | 追加式账本；不得回写历史价格 |
| `model_price_book_versions` | provider、计费项、单价、币种、生效期、来源 | 版本化、批准和可复算 |

数据库 DDL、索引和保留策略在实现阶段形成迁移；本表是逻辑契约，不代表已落库。

## 13. Trace、审计与可观测性

每次 Invocation/Attempt 记录：Tenant、Principal、Agent/Step、ModelPolicyVersion、Route、实际模型证据、排除/选择原因、数据分类、区域、Deadline、重试/Fallback、Token/计费、延迟、completion/usage state、错误和 Trace。

指标至少覆盖请求量、首 Token/总延迟、429/5xx、取消、Partial、中断、Fallback、Schema 无效、内容过滤、预算拒绝、用量未知和账单差异。高基数 ID 进入 Trace，不直接作为 Metrics 标签。

Prompt/响应正文默认只保存受控引用、哈希、大小和脱敏摘要。审计与 Trace 的读取、导出和保留同样受 Tenant、分类和用途策略约束。

## 14. PoC 与契约测试矩阵

| Verification ID | 场景 | 通过条件 | 证据状态 |
|---|---|---|---|
| VER-MGW-001 | 未批准区域/供应商/保留策略 | 在创建 Provider Attempt 前拒绝并记录 PolicyDecision | —（未执行） |
| VER-MGW-002 | 模型别名静默漂移 | 检测真实版本/指纹变化，阻断或触发门禁复审 | —（未执行） |
| VER-MGW-003 | 429、5xx、Retry-After | 重试有界、遵守 Deadline/预算且 Attempt 可追踪 | —（未执行） |
| VER-MGW-004 | Fallback 不兼容 | 能力、Schema、区域或评测不兼容时拒绝切换 | —（未执行） |
| VER-MGW-005 | 流式中断后 Fallback | 不拼接不同模型；Partial、重启和最终性明确 | —（未执行） |
| VER-MGW-006 | 结构化输出无效 | 有界修复，最终失败不会进入 Tool 参数或最终 Artifact | —（未执行） |
| VER-MGW-007 | 取消与迟到响应 | 状态、Partial、费用和迟到对账均可收敛 | —（未执行） |
| VER-MGW-008 | 并发预算与幂等 | 不超卖预算；重复 Key 不产生不可解释的双计费 | —（未执行） |
| VER-MGW-009 | Secret/敏感正文检测 | 凭据不进入持久化/遥测，受限数据不发往禁止 Route | —（未执行） |
| VER-MGW-010 | 供应商账单差异 | 估算与最终费用均保留，历史可复算且差异告警 | —（未执行） |

## 15. 组件采用与退出门禁

候选网关必须锁定 release/tag/commit，提供许可证、SBOM、漏洞响应、HA/扩缩、升级/回滚、数据流、凭据模型、观测、性能和退出方案。PoC 至少比较直接 Adapter 与候选代理的延迟、故障隔离、协议覆盖、安全边界和运维成本。

退出方案必须证明：ModelPolicy/Invocation/Usage 可导出；供应商特有字段被隔离；可双跑和回滚；凭据与数据可删除；Fallback 不依赖单一产品；迁移成本和最大停机已评估。未通过 P0 数据或安全门禁的候选只能标记为 Observe，不能进入生产依赖。

## 16. 完成定义

- [ ] ADR-005 已 Accepted，Gateway Owner、信任边界和部署模式明确。
- [ ] 请求、结果、流式事件、错误和用量 Schema 进入版本控制。
- [ ] ModelCatalog、Policy、Invocation、Attempt、Ledger 和价格表迁移完成。
- [ ] Route/Fallback 具有用例级 EvalPolicy 证据，不存在静默降级。
- [ ] Credential、DLP、区域、保留和内容安全通过对抗测试。
- [ ] PoC 矩阵、容量、故障、账单和退出演练产生可复核证据。
