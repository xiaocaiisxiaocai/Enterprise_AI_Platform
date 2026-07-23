const ADMIN_USER = "admin-governance";
const API_ROOT = "/api/v1/poc/governance";

const state = {
  overview: null,
  dialogTarget: null
};

const elements = {
  connectionDot: document.querySelector("#connection-dot"),
  connectionLabel: document.querySelector("#connection-label"),
  repositoryRevision: document.querySelector("#repository-revision"),
  identityRevision: document.querySelector("#identity-revision"),
  publishedCount: document.querySelector("#published-count"),
  documentRows: document.querySelector("#document-rows"),
  identityGrid: document.querySelector("#identity-grid"),
  queryUser: document.querySelector("#query-user"),
  queryForm: document.querySelector("#query-form"),
  queryQuestion: document.querySelector("#query-question"),
  queryResult: document.querySelector("#query-result"),
  manifestLabel: document.querySelector("#manifest-label"),
  refreshButton: document.querySelector("#refresh-button"),
  dialog: document.querySelector("#group-dialog"),
  groupForm: document.querySelector("#group-form"),
  dialogTitle: document.querySelector("#dialog-title"),
  dialogGroups: document.querySelector("#dialog-groups"),
  toast: document.querySelector("#toast")
};

async function api(path, options = {}, user = ADMIN_USER) {
  const response = await fetch(path, {
    ...options,
    headers: {
      "X-Poc-User": user,
      ...(options.body ? { "Content-Type": "application/json" } : {}),
      ...options.headers
    }
  });

  if (!response.ok) {
    let message = `请求失败（${response.status}）`;
    try {
      const payload = await response.json();
      message = payload.message || message;
    } catch {
      // 无结构化错误体时保留状态码。
    }
    throw new Error(message);
  }

  return response.status === 204 ? null : response.json();
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function renderTags(groups) {
  return `<div class="tags">${groups
    .map(group => `<span class="tag">${escapeHtml(group)}</span>`)
    .join("")}</div>`;
}

function lifecycleLabel(status) {
  return {
    Published: "已发布",
    Withdrawn: "已撤回",
    Deleted: "已删除"
  }[status] || status;
}

function renderOverview() {
  const overview = state.overview;
  elements.repositoryRevision.textContent = overview.repositoryRevision;
  elements.identityRevision.textContent = overview.identityRevision;
  elements.publishedCount.textContent = overview.documents
    .filter(document => document.status === "Published").length;
  elements.manifestLabel.textContent =
    `manifest ${overview.manifestSha256.slice(0, 12)}…`;

  elements.documentRows.innerHTML = overview.documents.map(document => {
    const action = document.status === "Published" ? "withdraw" : "publish";
    const actionLabel = action === "withdraw" ? "撤回" : "发布";
    const disabled = document.status === "Deleted" ? "disabled" : "";
    return `
      <tr>
        <td>
          <span class="asset-title">${escapeHtml(document.title)}</span>
          <span class="asset-id">${escapeHtml(document.id)} · ${escapeHtml(document.section)}</span>
        </td>
        <td>
          ${escapeHtml(document.sourcePath)}
          <span class="source-meta">version ${escapeHtml(document.version)}</span>
        </td>
        <td>${renderTags(document.allowedGroups)}</td>
        <td><span class="status ${document.status.toLowerCase()}">${lifecycleLabel(document.status)}</span></td>
        <td>
          <div class="row-actions">
            <button class="compact-button edit-groups" type="button"
              data-type="document" data-id="${escapeHtml(document.id)}"
              data-title="${escapeHtml(document.title)}"
              data-groups="${escapeHtml(document.allowedGroups.join(","))}" ${disabled}>ACL</button>
            <button class="compact-button lifecycle-action" type="button"
              data-id="${escapeHtml(document.id)}" data-action="${action}" ${disabled}>${actionLabel}</button>
          </div>
        </td>
      </tr>`;
  }).join("");

  elements.identityGrid.innerHTML = overview.identities.map((identity, index) => `
    <article class="identity-card">
      <span class="identity-index">${String(index + 1).padStart(2, "0")}</span>
      ${identity.isGovernanceAdmin ? '<span class="admin-seal">GOVERNANCE ADMIN</span>' : ""}
      <h3>${escapeHtml(identity.principalId)}</h3>
      <p>${escapeHtml(identity.tenantId)} · ${identity.enabled ? "ENABLED" : "DISABLED"}</p>
      ${renderTags(identity.groups)}
      ${identity.isGovernanceAdmin ? "" : `
        <button class="compact-button edit-groups" type="button"
          data-type="identity" data-id="${escapeHtml(identity.principalId)}"
          data-title="${escapeHtml(identity.principalId)}"
          data-groups="${escapeHtml(identity.groups.join(","))}">编辑 Group</button>`}
    </article>
  `).join("");

  const currentUser = elements.queryUser.value;
  elements.queryUser.innerHTML = overview.identities
    .filter(identity => identity.enabled)
    .map(identity => `<option value="${escapeHtml(identity.principalId)}">${escapeHtml(identity.principalId)}</option>`)
    .join("");
  if ([...elements.queryUser.options].some(option => option.value === currentUser)) {
    elements.queryUser.value = currentUser;
  } else if ([...elements.queryUser.options].some(option => option.value === "alice-finance")) {
    elements.queryUser.value = "alice-finance";
  }
}

async function loadOverview({ quiet = false } = {}) {
  try {
    state.overview = await api(`${API_ROOT}/overview`);
    renderOverview();
    elements.connectionDot.className = "connection-dot online";
    elements.connectionLabel.textContent = "API 已连接";
    if (!quiet) showToast("治理快照已刷新");
  } catch (error) {
    elements.connectionDot.className = "connection-dot offline";
    elements.connectionLabel.textContent = "连接失败";
    elements.documentRows.innerHTML =
      `<tr><td colspan="5" class="loading-cell">${escapeHtml(error.message)}</td></tr>`;
    showToast(error.message, true);
  }
}

function openGroupDialog(button) {
  state.dialogTarget = {
    type: button.dataset.type,
    id: button.dataset.id
  };
  elements.dialogTitle.textContent =
    `${button.dataset.type === "document" ? "编辑文档 ACL" : "编辑身份 Group"} · ${button.dataset.title}`;
  elements.dialogGroups.value = button.dataset.groups;
  elements.dialog.showModal();
  elements.dialogGroups.focus();
}

async function saveGroups(event) {
  event.preventDefault();
  const groups = elements.dialogGroups.value
    .split(",")
    .map(group => group.trim())
    .filter(Boolean);
  if (!groups.length) {
    showToast("至少保留一个 Group", true);
    return;
  }

  const target = state.dialogTarget;
  const path = target.type === "document"
    ? `${API_ROOT}/documents/${encodeURIComponent(target.id)}/acl`
    : `${API_ROOT}/identities/${encodeURIComponent(target.id)}/groups`;
  try {
    await api(path, {
      method: "PUT",
      body: JSON.stringify({ groups })
    });
    elements.dialog.close();
    await loadOverview({ quiet: true });
    showToast("权限组已更新，新的查询将立即采用");
  } catch (error) {
    showToast(error.message, true);
  }
}

async function updateLifecycle(button) {
  button.disabled = true;
  try {
    await api(
      `${API_ROOT}/documents/${encodeURIComponent(button.dataset.id)}/lifecycle/${button.dataset.action}`,
      { method: "POST" });
    await loadOverview({ quiet: true });
    showToast(button.dataset.action === "withdraw" ? "文档已撤回" : "文档已重新发布");
  } catch (error) {
    button.disabled = false;
    showToast(error.message, true);
  }
}

async function runQuery(event) {
  event.preventDefault();
  const user = elements.queryUser.value;
  const question = elements.queryQuestion.value.trim();
  if (!question) {
    showToast("请输入业务问题", true);
    return;
  }

  elements.queryResult.innerHTML =
    '<span class="result-kicker">正在判定权限</span><p>读取当前身份与知识快照…</p>';
  try {
    const result = await api("/api/v1/query", {
      method: "POST",
      body: JSON.stringify({ question })
    }, user);
    const citations = result.citations.length
      ? `<ul class="citation-list">${result.citations.map(citation => `
          <li>${escapeHtml(citation.documentId)} · v${escapeHtml(citation.version)} · ${escapeHtml(citation.section)}</li>
        `).join("")}</ul>`
      : "<p>没有返回授权引用。</p>";
    elements.queryResult.innerHTML = `
      <span class="result-kicker">${result.status === "answered" ? "ANSWERED" : "REFUSED"} · ${escapeHtml(user)}</span>
      <p>${escapeHtml(result.answer)}</p>
      ${citations}
      <span class="trace-id">trace ${escapeHtml(result.traceId)}</span>`;
  } catch (error) {
    elements.queryResult.innerHTML =
      `<span class="result-kicker">REQUEST FAILED</span><p>${escapeHtml(error.message)}</p>`;
  }
}

let toastTimer;
function showToast(message, isError = false) {
  clearTimeout(toastTimer);
  elements.toast.textContent = message;
  elements.toast.className = `toast visible${isError ? " error" : ""}`;
  toastTimer = setTimeout(() => {
    elements.toast.className = "toast";
  }, 3200);
}

elements.refreshButton.addEventListener("click", () => loadOverview());
elements.groupForm.addEventListener("submit", saveGroups);
elements.queryForm.addEventListener("submit", runQuery);
document.addEventListener("click", event => {
  const groupButton = event.target.closest(".edit-groups");
  if (groupButton) openGroupDialog(groupButton);
  const lifecycleButton = event.target.closest(".lifecycle-action");
  if (lifecycleButton) updateLifecycle(lifecycleButton);
});

loadOverview({ quiet: true });
