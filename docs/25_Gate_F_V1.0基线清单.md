# 25 Gate F V1.0 基线清单

> 状态：**Released** ｜ 版本：**V1.0** ｜ 范围：单企业内部、本地确定性 Gate F 契约 ｜ 不代表：业务试点、生产发布或概率性 AI Evaluation

## 1. 基线目的

本清单定义 Gate F V1.0 可重复验证的最小安全边界。基线以 Git 注解标签 `V1.0` 指向的提交为唯一代码锚点；标签已发布，发布事实见 [26 V1.0 发布记录](26_V1.0发布记录.md)。标签不得移动、覆盖或复用，后续内容变化必须使用新提交和新版本标识重新执行全部门禁。

## 2. 固定契约

| 项目 | V1.0 固定值 |
| --- | --- |
| Tenant | `enterprise-internal` |
| Policy | `gate-f-acl-v1` |
| Trace schema | `1.0` |
| Evidence schema/type | `1.0` / `gate-f-local-contract` |
| Evaluation type | `gate-f-local-deterministic` |
| Golden Dataset | `gate-f-golden`，版本 `1`，12 个合成用例 |
| Dataset SHA-256 | `ae02c6692bc4198f8af173c6eb96032fbd60e7eb5e05458058a9629783b8f4a0` |
| Snapshot | `gate-f-approved-snapshot`，3 个合成文档 |
| Manifest SHA-256 | `8c5fa85956c93f65ed7fce4bf19121e5deba118c20ed47443350423ddd5d3816` |
| 回归门禁 | 63/63 |
| Evaluation 门禁 | 12/12、越权引用 0、引用精确匹配率 1、拒答一致率 1 |

Trace 最终哈希包含运行时间等本次执行信息，每次运行可以不同；它必须由离线验证器逐条重算并与同一 Evidence Bundle、Evaluation Report 一致，不能作为跨运行固定版本值。

## 3. 纳入与排除范围

V1.0 纳入固定测试身份、部门 ACL 预过滤、批准快照完整性、抽取式回答、引用、统一拒答、干净 4xx、哈希链 Trace、Golden Dataset、原子 Evidence 发布和离线验证。

以下能力明确排除：真实 OIDC、动态组撤权、SharePoint、业务数据、PostgreSQL/pgvector、模型生成、概率性评测、生产 SLO、灾备和正式审计后端。任何本地 Passed 状态都不能替代 Sponsor、Security Owner、Data Owner 或业务用户批准。

## 4. 建立基线的硬门禁

在干净 `main` 上依次执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Export-GateFEvidence.ps1 -SelfTest
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-GateFEvidence.ps1 -SelfTest
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Export-GateFEvidence.ps1 -RequireCleanWorktree
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-GateFEvidence.ps1 -EvidencePath .\artifacts\gate-f-evidence.json
git status --short
git rev-parse HEAD
git ls-remote origin refs/heads/main
```

只有满足以下全部条件才能创建 `V1.0` 标签：

- 本地和远端 `main` SHA 一致，工作区为空；
- GitHub `validate-docs`、`gate-f-poc` 和 Artifact 上传成功；
- Evidence 的 `commit_sha` 等于候选提交且 `worktree_clean=true`；
- 63 条回归、12 条 Golden、负向自测及离线验证全部通过；
- Dataset、Snapshot、Policy 和 schema 与本清单一致；
- Artifact digest、Actions 运行链接和标签创建人已记录在发布说明中。

## 5. 变更控制

修改固定值、ACL/身份语义、拒答规则、Trace 哈希算法、Evidence schema、Golden 期望或批准快照会形成 V1.0 之后的新版本候选。变更必须先增加失败回归，再修改实现，更新相关 ADR/测试计划，并重新生成 Evidence。`V1.0` 已经用户明确授权创建并推送；不得删除、重建、强制更新或复用该标签。
