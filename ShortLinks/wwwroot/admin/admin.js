const searchInput = document.querySelector("#search-input");
const linksBody = document.querySelector("#links-body");
const resultCount = document.querySelector("#result-count");
const adminStatus = document.querySelector("#admin-status");
const editDialog = document.querySelector("#edit-dialog");
const editForm = document.querySelector("#edit-form");
const editCode = document.querySelector("#edit-code");
const editTarget = document.querySelector("#edit-target");
const cancelEdit = document.querySelector("#cancel-edit");

let links = [];
let editingCode = null;
let sort = "createdAt";
let dir = "desc";
let searchTimer = null;

function setStatus(message, isError = false) {
  adminStatus.textContent = message;
  adminStatus.classList.toggle("error", isError);
}

function formatDate(value) {
  if (!value) {
    return "Never";
  }

  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short"
  }).format(new Date(value));
}

function escapeHtml(value) {
  return value.replace(/[&<>"']/g, (character) => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    "\"": "&quot;",
    "'": "&#039;"
  }[character]));
}

async function loadLinks() {
  const params = new URLSearchParams({
    sort,
    dir
  });

  if (searchInput.value.trim()) {
    params.set("search", searchInput.value.trim());
  }

  const response = await fetch(`/api/admin/links?${params.toString()}`);
  if (!response.ok) {
    throw new Error("Could not load links.");
  }

  links = await response.json();
  renderLinks();
}

function renderLinks() {
  resultCount.textContent = `${links.length} ${links.length === 1 ? "link" : "links"}`;

  document.querySelectorAll(".sort-button").forEach((button) => {
    const isActive = button.dataset.sort === sort;
    button.classList.toggle("active", isActive);
    button.dataset.direction = isActive ? (dir === "asc" ? "↑" : "↓") : "";
  });

  if (links.length === 0) {
    linksBody.innerHTML = `<tr><td colspan="7" class="empty-cell">No links found.</td></tr>`;
    return;
  }

  linksBody.innerHTML = links.map((link) => `
    <tr>
      <td class="code-cell">${escapeHtml(link.code)}</td>
      <td class="url-cell">
        <a href="${escapeHtml(link.shortUrl)}" target="_blank" rel="noreferrer">${escapeHtml(link.shortUrl)}</a>
        <br>
        <a href="${escapeHtml(link.targetUrl)}" target="_blank" rel="noreferrer">${escapeHtml(link.targetUrl)}</a>
      </td>
      <td>${link.clickCount}</td>
      <td class="muted-cell">${formatDate(link.createdAt)}</td>
      <td class="muted-cell">${formatDate(link.lastClickedAt)}</td>
      <td class="muted-cell">${escapeHtml(link.createdIp || "Unknown")}</td>
      <td>
        <div class="action-row">
          <button class="small-button" type="button" data-action="edit" data-code="${escapeHtml(link.code)}">Edit</button>
          <button class="danger-button" type="button" data-action="delete" data-code="${escapeHtml(link.code)}">Delete</button>
        </div>
      </td>
    </tr>
  `).join("");
}

async function refreshLinks() {
  try {
    await loadLinks();
    setStatus("Ready.");
  } catch (error) {
    setStatus(error.message, true);
  }
}

searchInput.addEventListener("input", () => {
  window.clearTimeout(searchTimer);
  searchTimer = window.setTimeout(refreshLinks, 220);
});

document.querySelectorAll(".sort-button").forEach((button) => {
  button.addEventListener("click", () => {
    if (sort === button.dataset.sort) {
      dir = dir === "asc" ? "desc" : "asc";
    } else {
      sort = button.dataset.sort;
      dir = "asc";
    }

    refreshLinks();
  });
});

linksBody.addEventListener("click", async (event) => {
  const button = event.target.closest("button");
  if (!button) {
    return;
  }

  const link = links.find((item) => item.code === button.dataset.code);
  if (!link) {
    return;
  }

  if (button.dataset.action === "edit") {
    editingCode = link.code;
    editCode.textContent = link.code;
    editTarget.value = link.targetUrl;
    editDialog.showModal();
    return;
  }

  if (button.dataset.action === "delete") {
    const confirmed = window.confirm(`Delete ${link.code}?`);
    if (!confirmed) {
      return;
    }

    const response = await fetch(`/api/admin/links/${encodeURIComponent(link.code)}`, {
      method: "DELETE"
    });

    if (!response.ok) {
      setStatus("Could not delete link.", true);
      return;
    }

    setStatus("Deleted.");
    await refreshLinks();
  }
});

editForm.addEventListener("submit", async (event) => {
  event.preventDefault();

  const response = await fetch(`/api/admin/links/${encodeURIComponent(editingCode)}`, {
    method: "PUT",
    headers: {
      "Content-Type": "application/json"
    },
    body: JSON.stringify({ targetUrl: editTarget.value })
  });

  if (!response.ok) {
    const payload = await response.json().catch(() => ({}));
    setStatus(payload.error || "Could not save link.", true);
    return;
  }

  editDialog.close();
  setStatus("Saved.");
  await refreshLinks();
});

cancelEdit.addEventListener("click", () => {
  editDialog.close();
});

refreshLinks();
