# 22 组织责任与 RACI 运营模型

> 状态：**Planned（待组织批准）** ｜ 适用阶段：Phase 0 起 ｜ 真实人员映射：**未完成** ｜ 原则：单一 Accountable、职责分离、容量可证明、代理可追踪

## 1. 目的与约束

本文定义平台从试点选择、数据接入、资产发布到生产运营和退役的责任模型。角色名称不是责任落实；进入相应门禁前，每个 Accountable、Responsible、Approver 和值班角色必须映射到真实人员或受治理组织账号。

RACI 语义：

- **A（Accountable）**：对结果最终负责且具有决策权，每项只能有一个；
- **R（Responsible）**：执行工作，可有多个；
- **C（Consulted）**：决策前必须咨询；
- **I（Informed）**：决策后必须通知。

批准人不自动成为 A。高风险操作必须同时满足 RACI、源系统授权和职责分离，业务批准不能豁免安全硬门禁。

## 2. 角色登记

| 角色代码 | 角色与核心责任 | 真实映射 | 代理/升级 | 状态 |
|---|---|---|---|---|
| EXS | Executive Sponsor：投资、跨部门冲突、Go/Hold/Stop/Retire | Open | Open | Open |
| BUS | Business Owner：业务结果、流程风险、最终验收 | Open | Open | Open |
| PRD | Product Owner：范围、优先级、用户旅程、价值口径 | Open | Open | Open |
| ARC | Architecture Owner：边界、ADR、可替换性和技术债 | Open | Open | Open |
| SEC | Security / Model Risk Owner：威胁、控制、风险接受建议 | Open | Open | Open |
| DAT | Data Owner：用途、分类、地域、保留、删除和授权 | Open | Open | Open |
| KNO | Knowledge Owner：质量、有效期、发布、撤回和 Reviewer | Open | Open | Open |
| MOD | Model Platform Owner：模型目录、Route、价格和供应商运行 | Open | Open | Open |
| EVA | Evaluation Owner：数据集、指标、统计方法和发布门禁 | Open | Open | Open |
| TOL | Tool / Integration Owner：契约、风险、副作用、幂等和回滚 | Open | Open | Open |
| WFL | Workflow Owner：Task、审批、Timer、补偿和人工队列 | Open | Open | Open |
| SRE | SRE / Incident Commander：SLO、容量、发布、恢复和事件指挥 | Open | Open | Open |
| LEG | Legal / Compliance：适用性、合同、隐私、许可证和证据要求 | Open | Open | Open |
| FIN | Finance / Operations：全量成本、收益核算、采购与预算 | Open | Open | Open |
| CHG | Change / Service Owner：培训、沟通、服务台和采用反馈 | Open | Open | Open |

角色可由同一人员兼任，但不得违反第 4 节职责分离。组织规模不足时须记录补偿控制，例如独立复核、限时授权、抽样审计或外部评审。

## 3. 决策 RACI

| 决策/活动 | A | R | C | I |
|---|---|---|---|---|
| 试点选择、范围和退出标准 | PRD | PRD、BUS | EXS、DAT、SEC、EVA、FIN、SRE | ARC、CHG |
| 试点投资 Go/Hold/Stop/Retire | EXS | PRD、FIN | BUS、SEC、DAT、SRE、EVA | 全体 Owner |
| Tenant/身份与权限映射 | SEC | ARC、SEC | DAT、BUS、SRE | PRD、KNO、TOL |
| 数据源接入和用途授权 | DAT | DAT、KNO | SEC、LEG、BUS | PRD、SRE |
| Knowledge 审核策略和发布 | KNO | KNO、Reviewer | DAT、SEC、EVA | BUS、PRD、SRE |
| 模型供应商/区域/数据条款 | MOD | MOD | DAT、SEC、LEG、FIN、ARC | PRD、EVA、SRE |
| Model Route/Fallback 变更 | MOD | MOD | EVA、SEC、DAT、SRE | ARC、PRD |
| Tool/Integration 发布或撤销 | TOL | TOL | SEC、BUS、DAT、WFL、SRE | ARC、PRD、EVA |
| 高风险动作审批矩阵 | BUS | WFL、SEC | TOL、DAT、LEG | PRD、SRE |
| Policy 生产激活 | SEC | SEC、ARC | DAT、BUS、TOL、SRE | EVA、PRD |
| EvalPolicy 和发布门禁 | EVA | EVA | BUS、SEC、KNO、MOD、SRE | PRD、ARC |
| 生产发布/回滚 | SRE | SRE、交付团队 | EVA、SEC、DAT、业务 Owner | EXS、CHG、Service Desk |
| 未关闭 P0 风险的 Hold/Stop 与整改资源决策 | EXS | 风险 Owner | BUS、SEC、DAT、LEG、SRE | PRD、相关 Owner |
| P1 残余风险接受 | BUS | 风险 Owner | SEC、DAT、LEG、SRE | EXS、PRD |
| 安全/隐私事件指挥 | SRE | Incident Team | SEC、DAT、LEG、BUS | EXS、受影响 Owner |
| 数据删除、Legal Hold 和恢复后再删除 | DAT | DAT、SRE | LEG、SEC、KNO | BUS、PRD |
| 供应商退出和数据迁移 | MOD | MOD、SRE、DAT | ARC、SEC、LEG、FIN | EXS、PRD、EVA |
| 平台或用例退役 | EXS | PRD、SRE | BUS、DAT、SEC、FIN、LEG | 用户与全部 Owner |

表中“业务 Owner”表示与具体用例绑定的 BUS；不得用共享群组代替最终 A。每项决议需要 ID、版本、批准者、时间、适用范围、到期/复审条件和证据链接。

## 4. 职责分离与禁止组合

| 场景 | 禁止规则 | 最低补偿控制 |
|---|---|---|
| Knowledge 发布 | 候选生成者不得单独批准自己生成的高风险内容 | 独立 Reviewer、差异与证据展示、可撤回 Release |
| Tool 高风险动作 | 请求者、Agent 生成者和最终审批者不能是同一责任主体 | 源系统重新鉴权、双人审批或独立业务 Approver |
| Model/Prompt 发布 | 实现者不得单独修改评测集并批准同一 Candidate | 独立 EVA、冻结数据集、变更差异和回归证据 |
| Policy 激活 | Policy 作者不得独自批准扩大权限的规则 | SEC A、正反例测试、Canary、Break-glass 复核 |
| 生产发布 | 制品构建者不得绕过门禁直接提升到生产 | 签名制品、环境晋级权限分离、SRE 执行 |
| 平台运维访问 | 日常管理员不得拥有无期限跨租户明文访问 | JIT 授权、增强认证、双人批准、会话录制/审计 |
| 审计验证 | 被审计功能 Owner 不得是完整性验证的唯一执行者 | 独立 Security/Audit 抽检和外部锚定（如适用） |
| 风险接受 | 风险制造者不得自行接受残余 P0/P1 风险 | BUS/EXS 按等级批准，SEC/LEG 提供独立意见 |

Break-glass 仅用于已定义紧急场景，必须限时、限范围、增强认证、自动告警并在事后独立复核；它不是常规运维捷径。

## 5. 运营机制与决策节奏

| 机制 | 目的 | 触发/节奏 | 最小产物 |
|---|---|---|---|
| Pilot Steering | 范围、价值、资源和退出决策 | 立项、阶段门禁、重大范围变化 | 决议、TBD 状态、价值与风险摘要 |
| Architecture / ADR Review | 边界、组件、数据与演进决策 | 新依赖、破坏性变更、复审条件触发 | Accepted/Rejected ADR、PoC 和退出方案 |
| Data & Knowledge Review | 数据接入、质量、权限、发布和撤回 | 新数据源、审核积压、质量或权限异常 | 数据清单、审核记录、删除/撤权证据 |
| Model & Evaluation Review | Route、质量、安全、成本和漂移 | 模型/Prompt/Retriever 变更、门禁失败 | EvalRun、差异、风险与发布建议 |
| Release Readiness Review | 判断制品能否晋级 | 每个生产候选 | 版本清单、测试证据、SLO、Runbook、回滚 |
| Incident Command | 快速隔离、通知、恢复和复盘 | 告警达到事件分级标准 | 时间线、影响范围、处置、通知与行动项 |
| Quarterly/Trigger Review | 复审用例价值、供应商和组织能力 | 定期或事故/合同/法规变化 | Go/Hold/Stop/Retire 和 ADR 复审 |

具体周期由团队容量和风险等级批准，不在无实际组织信息时虚构固定会议频率。

## 6. 人工容量与队列模型

### 6.1 必填参数

| 队列 | 到达量 | 平均处理时长 | 目标完成时限 | 可用人员/时段 | 积压上限 | 代理/升级 | 状态 |
|---|---:|---:|---:|---|---:|---|---|
| Knowledge Review | Open | Open | Open | Open | Open | Open | Open |
| Approval / HITL | Open | Open | Open | Open | Open | Open | Open |
| Security/Model Risk Triage | Open | Open | Open | Open | Open | Open | Open |
| Data 删除/撤权异常 | Open | Open | Open | Open | Open | Open | Open |
| Service Desk / 用户反馈 | Open | Open | Open | Open | Open | Open | Open |

### 6.2 计算与门禁

```text
工作量 = 到达量 × 平均处理时长
理论 FTE = 工作量 ÷ 可用工时
计划 FTE = 理论 FTE ÷ 目标利用率 + 值班/休假/峰值缓冲
```

- 不以 100% 利用率规划人工队列；
- 到达量必须含正常、异常、复核、申诉和回归抽检；
- 超过积压或时限阈值时暂停自动发布/高风险执行，而不是降低审核标准；
- 无有效代理时，高风险审批到期应拒绝或停止，不自动转为允许；
- 人工成本计入 `cost_per_success` 和 ROI。

## 7. 事件响应与升级责任

| 事件等级 | 示例 | 指挥与决策 | 最低动作 |
|---|---|---|---|
| P0 | 跨租户泄漏、密钥泄露、未授权重大副作用、审计系统性缺失 | SRE 指挥；SEC/DAT/BUS 共同判定影响；EXS 获知 | Kill Switch、隔离、保全、通知决策、恢复门禁 |
| P1 | 大范围错误回答、删除传播失败、审批绕过未造成重大影响 | SRE 或指定 Incident Commander | 限流/回滚、影响分析、修复、复盘 |
| P2 | 局部质量退化、队列积压、单一供应商故障 | 服务 Owner | 降级/切换、工单、趋势跟踪 |

事件等级、通知时限和监管/客户通报必须由适用性评估批准。任何人不得为了指标表现降低事件等级。

## 8. 人员生命周期与权限回收

- Joiner：完成角色培训、利益冲突声明、最小权限和环境隔离后授权；
- Mover：职责变化立即重算 Scope、审批资格、数据访问和代理关系；
- Leaver：撤销会话、Token、审批资格、Secret 和 Break-glass；转移 Owner 与未结任务；
- 定期复核：高权限、跨租户支持、审批和发布权限按风险周期复核；
- 服务身份同样有 Owner、用途、凭据轮换、到期和退役流程，不允许“无人账号”。

## 9. 变更管理与用户支持

Change Owner 在 Phase 0/1 即建立利益相关方地图、可接受使用政策、角色培训、服务台分类、反馈闭环和已知限制说明。用户必须知道：系统可能拒答、引用如何核验、什么内容禁止输入、如何纠错/申诉以及何时联系人工。

培训通过不能替代授权；高风险角色需要场景演练。每次重大能力或风险变化更新培训、Runbook、FAQ 和用户告知，并记录理解/采用证据。

## 10. 完成定义

- [ ] 所有 A/R/Approver 映射真实主体，代理和升级路径可用。
- [ ] 试点、数据、模型、评测、发布、事件和退役均只有一个 A。
- [ ] 职责分离由 Policy/工作流强制，不只依赖书面承诺。
- [ ] Knowledge、Approval、Security、Data 和 Support 队列完成容量测算。
- [ ] 高风险角色完成培训和演练，权限生命周期可验证。
- [ ] 每项决策和风险接受均有关联证据、到期日和复审条件。
