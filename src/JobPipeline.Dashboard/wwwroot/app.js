"use strict";

// ── Status vocabulary ──────────────────────────────────────────────────────────────────────────────────────────
const FLOW = ["New", "Tailoring", "ReadyToApply", "Applying", "Applied", "Interviewing"];
const EXITS = ["ActionNeeded", "Rejected", "Skipped", "Expired", "Failed"];
const LABEL = {
  New: "New", Tailoring: "Tailoring", ReadyToApply: "Ready to apply", Applying: "Applying", Applied: "Applied",
  Interviewing: "Interviewing", ActionNeeded: "Needs you", Rejected: "Rejected", Skipped: "Skipped",
  Expired: "Expired", Failed: "Failed",
};
// Statuses you can set by hand (Tailoring and Applying belong to the pipeline).
const SETTABLE = ["New", "ReadyToApply", "Applied", "Interviewing", "ActionNeeded", "Rejected", "Skipped", "Expired", "Failed"];
const REFRESH_MS = 15000;

// ── State ──────────────────────────────────────────────────────────────────────────────────────────────────────
const state = {
  status: null,          // filter
  q: "",
  jobs: [],
  stats: {},
  openId: null,          // job in the drawer; "new" while adding
  dirty: false,          // drawer has unsaved edits: don't reload it underneath the user
};

const $ = (id) => document.getElementById(id);
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

// ── API ────────────────────────────────────────────────────────────────────────────────────────────────────────
async function api(path, options = {}) {
  const res = await fetch(`/api${path}`, {
    headers: options.body ? { "Content-Type": "application/json" } : undefined,
    ...options,
    body: options.body ? JSON.stringify(options.body) : undefined,
  });
  if (res.status === 204) return null;
  const text = await res.text();
  const data = text ? JSON.parse(text) : null;
  if (!res.ok) {
    const err = new Error(data?.error || data?.title || `Request failed (${res.status})`);
    err.status = res.status;
    err.data = data;
    throw err;
  }
  return data;
}

// ── Formatting ─────────────────────────────────────────────────────────────────────────────────────────────────
const fmtDateTime = (iso) => (iso ? new Date(iso).toLocaleString([], { dateStyle: "medium", timeStyle: "short" }) : "");
function fmtAgo(iso) {
  if (!iso) return "";
  const s = Math.round((Date.now() - new Date(iso)) / 1000);
  if (s < 60) return "just now";
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  return new Date(iso).toLocaleDateString([], { month: "short", day: "numeric" });
}
function fmtPay(j) {
  if (j.salaryText) return j.salaryText;
  if (j.salaryMin || j.salaryMax) {
    const k = (n) => (n ? `$${Math.round(n / 1000)}k` : "");
    return [k(j.salaryMin), k(j.salaryMax)].filter(Boolean).join("–");
  }
  return "";
}
const badge = (status) => `<span class="badge" style="--c: var(--s-${status})">${esc(LABEL[status] ?? status)}</span>`;
const remoteTag = (j) => (j.isRemote === true && !/remote/i.test(j.location || "") ? `<span class="tag">Remote</span>` : "");

// ── Toast ──────────────────────────────────────────────────────────────────────────────────────────────────────
let toastTimer;
function toast(message, isError = false) {
  const t = $("toast");
  t.textContent = message;
  t.classList.toggle("error", isError);
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (t.hidden = true), isError ? 7000 : 3000);
}

// ── Rail ───────────────────────────────────────────────────────────────────────────────────────────────────────
function renderRail() {
  const seg = (s) => {
    const n = state.stats[s] ?? 0;
    return `<button class="seg${n ? "" : " zero"}" type="button" data-status="${s}" aria-pressed="${state.status === s}"
        style="--c: var(--s-${s}); --w: ${1 + Math.sqrt(n)}"><span class="n">${n}</span><span class="l">${esc(LABEL[s])}</span></button>`;
  };
  $("rail").innerHTML = FLOW.map(seg).join("") + `<span class="gap" aria-hidden="true"></span>` + EXITS.map(seg).join("");
}

$("rail").addEventListener("click", (e) => {
  const b = e.target.closest(".seg");
  if (!b) return;
  state.status = state.status === b.dataset.status ? null : b.dataset.status;
  refresh();
});

// ── Table ──────────────────────────────────────────────────────────────────────────────────────────────────────
function renderRows() {
  const total = Object.values(state.stats).reduce((a, b) => a + b, 0);
  const parts = [];
  if (state.status) parts.push(`${LABEL[state.status]} only`);
  if (state.q) parts.push(`matching “${state.q}”`);
  $("filterNote").innerHTML = parts.length
    ? `${state.jobs.length} of ${total} jobs, ${esc(parts.join(", "))}. <button class="btn ghost" type="button" id="clearFilter">Show all</button>`
    : `${total} jobs`;
  $("clearFilter")?.addEventListener("click", () => { state.status = null; state.q = ""; $("search").value = ""; refresh(); });

  $("rows").innerHTML = state.jobs.map((j) => `
    <tr data-id="${j.id}" tabindex="0" class="${j.id === state.openId ? "selected" : ""}">
      <td><div class="job-title">${esc(j.title)}</div><div class="job-company">${esc(j.company)}</div></td>
      <td>${badge(j.status)}${j.status === "ActionNeeded" && j.actionDesc ? `<div class="dim" style="font-size:13px;max-width:260px">${esc(j.actionDesc)}</div>` : ""}</td>
      <td class="opt">${esc(j.location ?? "")}${remoteTag(j)}</td>
      <td class="opt nowrap">${esc(fmtPay(j))}</td>
      <td class="opt nowrap dim">${esc(j.datePosted ?? "")}</td>
      <td class="nowrap dim" title="${esc(fmtDateTime(j.updatedAt))}">${esc(fmtAgo(j.updatedAt))}</td>
      <td class="nowrap">${j.resumePath ? `<a href="/api/jobs/${j.id}/resume" target="_blank" rel="noopener">Resume</a>` : ""}</td>
    </tr>`).join("");

  const empty = $("empty");
  if (state.jobs.length) { empty.hidden = true; return; }
  empty.hidden = false;
  empty.textContent = total === 0
    ? "No jobs yet. Ask Claude to run the job hunt, or add one yourself with “Add a job”."
    : "No jobs match these filters.";
}

$("rows").addEventListener("click", (e) => {
  if (e.target.closest("a")) return;
  const tr = e.target.closest("tr[data-id]");
  if (tr) openJob(tr.dataset.id);
});
$("rows").addEventListener("keydown", (e) => {
  const tr = e.target.closest("tr[data-id]");
  if (tr && (e.key === "Enter" || e.key === " ")) { e.preventDefault(); openJob(tr.dataset.id); }
});

let searchTimer;
$("search").addEventListener("input", (e) => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(() => { state.q = e.target.value.trim(); refresh(); }, 250);
});

// ── Refresh loop ───────────────────────────────────────────────────────────────────────────────────────────────
async function refresh() {
  try {
    const params = new URLSearchParams();
    if (state.status) params.set("status", state.status);
    if (state.q) params.set("q", state.q);
    const [stats, jobs] = await Promise.all([api("/stats"), api(`/jobs?${params}`)]);
    state.stats = stats;
    state.jobs = jobs;
    renderRail();
    renderRows();
    $("refreshed").textContent = `Updated ${new Date().toLocaleTimeString([], { hour: "numeric", minute: "2-digit", second: "2-digit" })}`;
    // Keep the open job current, unless you're in the middle of editing it.
    const editing = state.dirty || $("drawer").contains(document.activeElement) && document.activeElement.matches("input, select, textarea");
    if (state.openId && state.openId !== "new" && !editing) await loadDrawer(state.openId, { quiet: true });
  } catch (err) {
    $("refreshed").textContent = `Can't reach the dashboard server: ${err.message}`;
  }
}

// ── Drawer ─────────────────────────────────────────────────────────────────────────────────────────────────────
function showDrawer() {
  $("drawer").hidden = false;
  $("scrim").hidden = false;
}
function closeDrawer() {
  if (state.dirty && !window.confirmClose) {
    window.confirmClose = true;
    toast("You have unsaved changes. Close again to discard them.");
    setTimeout(() => (window.confirmClose = false), 4000);
    return;
  }
  window.confirmClose = false;
  state.openId = null;
  state.dirty = false;
  $("drawer").hidden = true;
  $("scrim").hidden = true;
  renderRows();
}
$("closeDrawer").addEventListener("click", closeDrawer);
$("scrim").addEventListener("click", closeDrawer);
document.addEventListener("keydown", (e) => { if (e.key === "Escape" && !$("drawer").hidden) closeDrawer(); });

async function openJob(id) {
  state.openId = id;
  state.dirty = false;
  renderRows();
  showDrawer();
  await loadDrawer(id);
}

async function loadDrawer(id, { quiet = false } = {}) {
  let detail;
  try {
    detail = await api(`/jobs/${id}`);
  } catch (err) {
    if (err.status === 404) {
      if (!quiet) toast("That job no longer exists.", true);
      state.dirty = false;
      closeDrawer();
      return;
    }
    if (!quiet) toast(err.message, true);
    return;
  }
  const scroll = $("drawerBody").scrollTop;
  renderDrawer(detail.job, detail.jdText);
  $("drawerBody").scrollTop = scroll;
}

const inputDate = (iso) => {
  if (!iso) return "";
  const d = new Date(iso);
  const pad = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
};

function field(name, label, value, { type = "text", wide = false, required = false, rows = 0, options = null } = {}) {
  const cls = `field${wide ? " wide" : ""}${type === "checkbox" ? " check" : ""}`;
  let control;
  if (options) {
    control = `<select name="${name}">${options.map(([v, t]) => `<option value="${esc(v)}"${String(value ?? "") === v ? " selected" : ""}>${esc(t)}</option>`).join("")}</select>`;
  } else if (rows) {
    control = `<textarea name="${name}" rows="${rows}">${esc(value ?? "")}</textarea>`;
  } else if (type === "checkbox") {
    return `<label class="${cls}"><input type="checkbox" name="${name}"${value ? " checked" : ""}> <span>${esc(label)}</span></label>`;
  } else {
    control = `<input type="${type}" name="${name}" value="${esc(value ?? "")}"${required ? " required" : ""}>`;
  }
  return `<label class="${cls}"><span>${esc(label)}${required ? " (required)" : ""}</span>${control}</label>`;
}

function detailsFields(j) {
  return `
    ${field("company", "Company", j.company, { required: true })}
    ${field("title", "Title", j.title, { required: true })}
    ${field("listingUrl", "Listing URL", j.listingUrl, { type: "url", wide: true })}
    ${field("applyUrl", "Apply URL", j.applyUrl, { type: "url", wide: true })}
    ${field("location", "Location", j.location)}
    ${field("isRemote", "Remote", j.isRemote === true ? "true" : j.isRemote === false ? "false" : "", { options: [["", "Unknown"], ["true", "Yes"], ["false", "No"]] })}
    ${field("salaryMin", "Pay from", j.salaryMin, { type: "number" })}
    ${field("salaryMax", "Pay to", j.salaryMax, { type: "number" })}
    ${field("salaryText", "Pay as posted", j.salaryText)}
    ${field("salaryCurrency", "Currency", j.salaryCurrency)}
    ${field("datePosted", "Posted", j.datePosted, { type: "date" })}
    ${field("source", "Source", j.source)}
    ${field("externalJobId", "Job ID on the posting", j.externalJobId)}
    ${field("matchedTitle", "Why it was kept", j.matchedTitle)}
    ${field("dateApplied", "Applied on", inputDate(j.dateApplied), { type: "datetime-local" })}
    ${field("interviewDate", "Interview", inputDate(j.interviewDate), { type: "datetime-local" })}
    ${field("isEasyApply", "Easy Apply on the job site", j.isEasyApply, { type: "checkbox", wide: true })}
    ${field("actionDesc", "What it needs from you / why it was closed", j.actionDesc, { rows: 2, wide: true })}
    ${field("techStack", "Tech stack (comma-separated, in posting order)", (j.techStack ?? []).join(", "), { rows: 4, wide: true })}`;
}

function readDetails(form) {
  const v = (n) => form.elements[n].value.trim();
  const num = (n) => (v(n) === "" ? null : Number(v(n)));
  const dt = (n) => (v(n) === "" ? null : new Date(v(n)).toISOString());
  return {
    company: v("company"), title: v("title"), source: v("source") || null, matchedTitle: v("matchedTitle") || null,
    listingUrl: v("listingUrl") || null, applyUrl: v("applyUrl") || null, externalJobId: v("externalJobId") || null,
    location: v("location") || null, isRemote: v("isRemote") === "" ? null : v("isRemote") === "true",
    salaryMin: num("salaryMin"), salaryMax: num("salaryMax"), salaryText: v("salaryText") || null,
    salaryCurrency: v("salaryCurrency") || null, datePosted: v("datePosted") || null,
    isEasyApply: form.elements.isEasyApply.checked, actionDesc: v("actionDesc") || null,
    dateApplied: dt("dateApplied"), interviewDate: dt("interviewDate"),
    techStack: v("techStack").split(",").map((t) => t.trim()).filter(Boolean),
  };
}

function renderDrawer(j, jdText) {
  $("drawerCompany").textContent = j.company;
  $("drawerTitle").textContent = j.title;
  const links = [
    j.listingUrl && `<a href="${esc(j.listingUrl)}" target="_blank" rel="noopener">Listing</a>`,
    j.applyUrl && `<a href="${esc(j.applyUrl)}" target="_blank" rel="noopener">Apply page</a>`,
    j.resumePath && `<a href="/api/jobs/${j.id}/resume" target="_blank" rel="noopener">Resume</a>`,
  ].filter(Boolean);
  $("drawerMeta").innerHTML = `${badge(j.status)}${links.join("")}`;

  const statusOptions = SETTABLE.map((s) => [s, LABEL[s]]);
  const history = [...(j.history ?? [])].reverse();
  const comments = [...(j.comments ?? [])].reverse();

  $("drawerBody").innerHTML = `
    <section class="block">
      <h3>Status</h3>
      ${j.status === "ActionNeeded" && j.actionDesc ? `<p class="hint">${esc(j.actionDesc)}</p>` : ""}
      ${j.lastError ? `<p class="hint">Last tailoring error: ${esc(j.lastError)}</p>` : ""}
      <form id="statusForm" class="row">
        ${field("status", "Change to", SETTABLE.includes(j.status) ? j.status : "", { options: [["", "Choose a status"], ...statusOptions] })}
        ${field("note", "Note (optional)", "")}
        <button class="btn primary" type="submit">Change status</button>
      </form>
      <p class="hint" style="margin-top:8px">New sends it back to the worker for a fresh resume.</p>
    </section>

    <section class="block">
      <h3>Comments</h3>
      <form id="commentForm" class="row">
        ${field("text", "Add a comment", "", { rows: 2 })}
        <button class="btn" type="submit">Add comment</button>
      </form>
      ${comments.length ? `<ul class="list" style="margin-top:10px">${comments.map((c) => `
        <li><div><div class="text">${esc(c.text)}</div><div class="when">${esc(fmtDateTime(c.at))}</div></div>
            <button class="btn ghost" type="button" data-del-comment="${c.id}">Delete</button></li>`).join("")}</ul>` : ""}
    </section>

    <section class="block">
      <h3>Details</h3>
      <form id="detailsForm" class="grid">
        ${detailsFields(j)}
        <div class="wide row" style="margin-top:4px">
          <button class="btn primary" type="submit">Save details</button>
          <button class="btn ghost" type="button" id="revertDetails">Undo changes</button>
        </div>
      </form>
    </section>

    <section class="block">
      <h3>Job description</h3>
      <form id="jdForm">
        <label class="field"><span class="visually-hidden">Job description</span>
          <textarea class="jd" name="text">${esc(jdText ?? "")}</textarea></label>
        <div class="row"><button class="btn" type="submit">Save description</button></div>
      </form>
    </section>

    <section class="block">
      <h3>History</h3>
      <ul class="list history">${history.map((h) => `
        <li><span class="when">${esc(fmtDateTime(h.at))}</span>
            <span>${h.from && h.from !== h.to ? `${esc(LABEL[h.from] ?? h.from)} to ` : ""}${esc(LABEL[h.to] ?? h.to)}
            <span class="dim">by ${esc(h.by)}${h.note ? `: ${esc(h.note)}` : ""}</span></span></li>`).join("")}</ul>
    </section>

    <section class="block">
      <h3>Delete</h3>
      <p class="hint">Removes the job from the list. Its resume files stay on disk.</p>
      <div class="confirm" id="deleteArea"><button class="btn danger" type="button" id="deleteJob">Delete this job</button></div>
    </section>`;

  wireDrawer(j);
}

function wireDrawer(j) {
  const details = $("detailsForm");
  details.addEventListener("input", () => (state.dirty = true));
  $("jdForm").addEventListener("input", () => (state.dirty = true));
  $("commentForm").addEventListener("input", (e) => (state.dirty = e.target.value.trim() !== ""));

  details.addEventListener("submit", (e) => act(e, async () => {
    if (!details.reportValidity()) return;
    await api(`/jobs/${j.id}`, { method: "PUT", body: readDetails(details) });
    return "Details saved.";
  }));
  $("revertDetails").addEventListener("click", () => { state.dirty = false; loadDrawer(j.id); });

  $("statusForm").addEventListener("submit", (e) => act(e, async () => {
    const f = e.target;
    if (!f.elements.status.value) throw new Error("Choose a status first.");
    const updated = await api(`/jobs/${j.id}/status`, { method: "POST", body: { status: f.elements.status.value, note: f.elements.note.value } });
    return `Status changed to ${LABEL[updated.status]}.`;
  }));

  $("commentForm").addEventListener("submit", (e) => act(e, async () => {
    await api(`/jobs/${j.id}/comments`, { method: "POST", body: { text: e.target.elements.text.value } });
    return "Comment added.";
  }));
  $("drawerBody").querySelectorAll("[data-del-comment]").forEach((b) =>
    b.addEventListener("click", (e) => act(e, async () => {
      await api(`/jobs/${j.id}/comments/${b.dataset.delComment}`, { method: "DELETE" });
      return "Comment deleted.";
    })));

  $("jdForm").addEventListener("submit", (e) => act(e, async () => {
    await api(`/jobs/${j.id}/description`, { method: "PUT", body: { text: e.target.elements.text.value } });
    return "Description saved.";
  }));

  $("deleteJob").addEventListener("click", () => {
    $("deleteArea").innerHTML = `<span>Delete ${esc(j.company)} – ${esc(j.title)}?</span>
      <button class="btn danger solid" type="button" id="confirmDelete">Delete job</button>
      <button class="btn ghost" type="button" id="cancelDelete">Keep it</button>`;
    $("cancelDelete").addEventListener("click", () => renderDrawer(j, $("jdForm").elements.text.value));
    $("confirmDelete").addEventListener("click", async () => {
      try {
        await api(`/jobs/${j.id}`, { method: "DELETE" });
        state.dirty = false;
        closeDrawer();
        toast("Job deleted.");
        refresh();
      } catch (err) { toast(err.message, true); }
    });
  });
}

// Runs a drawer action: disables the button, reports the outcome, reloads.
async function act(e, fn) {
  e.preventDefault();
  const button = e.submitter ?? e.currentTarget;
  if (button) button.disabled = true;
  try {
    const message = await fn();
    if (message === undefined) return;
    state.dirty = false;
    toast(message);
    await refresh();
    if (state.openId) await loadDrawer(state.openId, { quiet: true });
  } catch (err) {
    toast(err.message, true);
  } finally {
    if (button) button.disabled = false;
  }
}

// ── Add a job ──────────────────────────────────────────────────────────────────────────────────────────────────
$("addJob").addEventListener("click", () => {
  state.openId = "new";
  state.dirty = false;
  renderRows();
  $("drawerCompany").textContent = "Add a job";
  $("drawerTitle").textContent = "New job";
  $("drawerMeta").innerHTML = `<span class="dim">Company, title and a URL are required.</span>`;
  $("drawerBody").innerHTML = `
    <section class="block">
      <form id="newForm" class="grid">
        ${detailsFields({ techStack: [] })}
        <label class="field wide"><span>Job description (the worker tailors from it)</span>
          <textarea class="jd" name="jdText"></textarea></label>
        <div class="wide row"><button class="btn primary" type="submit">Add job</button></div>
      </form>
    </section>`;
  const form = $("newForm");
  form.addEventListener("input", () => (state.dirty = true));
  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    if (!form.reportValidity()) return;
    const d = readDetails(form);
    const body = { ...d, jdText: form.elements.jdText.value.trim() || null, source: d.source || "manual" };
    try {
      const created = await api("/jobs", { method: "POST", body });
      toast("Job added. The worker will make its resume shortly.");
      state.dirty = false;
      await refresh();
      openJob(created.id);
    } catch (err) {
      if (err.status === 409 && err.data?.id) {
        toast("That job is already in the list; opening it.", true);
        state.dirty = false;
        openJob(err.data.id);
      } else toast(err.message, true);
    }
  });
  showDrawer();
  form.elements.company.focus();
});

// ── Start ──────────────────────────────────────────────────────────────────────────────────────────────────────
(async () => {
  try {
    const info = await api("/info");
    $("dataDir").textContent = `Reading ${info.database}`;
  } catch {
    $("dataDir").textContent = "";
  }
  await refresh();
  setInterval(() => { if (!document.hidden) refresh(); }, REFRESH_MS);
})();
