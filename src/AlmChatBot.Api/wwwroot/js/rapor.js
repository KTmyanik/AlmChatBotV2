const state = {
  filters: null,
  cashflow: null,
  duration: null,
  view: "table",
  expanded: new Set(),
  compact: false,
  search: "",
  abort: null
};

const els = {
  date: document.getElementById("flt-date"),
  depth: document.getElementById("flt-depth"),
  ccy: document.getElementById("flt-ccy"),
  balance: document.getElementById("flt-balance"),
  search: document.getElementById("search"),
  compact: document.getElementById("flt-compact"),
  kpis: document.getElementById("kpis"),
  meta: document.getElementById("meta"),
  error: document.getElementById("error"),
  table: document.getElementById("table-view"),
  chart: document.getElementById("chart-view"),
  duration: document.getElementById("duration-view"),
  svg: document.getElementById("chart-svg")
};

bindSeg("flt-approach", false);
bindSeg("flt-pool", true);
bindSeg("flt-tenor", false);

document.querySelectorAll(".tabs button").forEach((btn) => {
  btn.addEventListener("click", () => {
    state.view = btn.dataset.view;
    document.querySelectorAll(".tabs button").forEach((b) => {
      b.classList.toggle("on", b === btn);
      b.setAttribute("aria-selected", b === btn ? "true" : "false");
    });
    render();
    if (state.view === "duration") loadDuration();
  });
});

els.date.addEventListener("change", loadCashflow);
els.depth.addEventListener("change", loadCashflow);
els.ccy.addEventListener("change", loadCashflow);
els.balance.addEventListener("change", loadCashflow);
els.search.addEventListener("input", () => {
  state.search = els.search.value.trim().toLocaleLowerCase("tr-TR");
  renderTable();
});
els.compact.addEventListener("change", () => {
  state.compact = els.compact.checked;
  render();
});
document.getElementById("btn-expand").addEventListener("click", () => {
  collectIds(state.cashflow?.tree).forEach((id) => state.expanded.add(id));
  renderTable();
});
document.getElementById("btn-collapse").addEventListener("click", () => {
  state.expanded.clear();
  (state.cashflow?.tree || []).forEach((n) => { if (n.children?.length) state.expanded.add(n.id); });
  renderTable();
});

init();

async function init() {
  try {
    state.filters = await getJson("/api/alm/report/filters");
    fillSelect(els.date, state.filters.dates, state.filters.defaultDate);
    fillSelect(els.ccy, ["Tümü", ...(state.filters.currencies || [])], "Tümü");
    fillSelect(els.balance, state.filters.balanceTypes || ["TOTAL"], "TOTAL");
    await loadCashflow();
  } catch (err) {
    showError(err.message);
  }
}

function bindSeg(id, multi) {
  const root = document.getElementById(id);
  root.addEventListener("click", (event) => {
    const btn = event.target.closest("button");
    if (!btn) return;
    if (multi) {
      btn.classList.toggle("on");
      if (![...root.querySelectorAll("button.on")].length) btn.classList.add("on");
    } else {
      root.querySelectorAll("button").forEach((b) => b.classList.toggle("on", b === btn));
    }
    loadCashflow();
  });
}

function selected(id) {
  return [...document.getElementById(id).querySelectorAll("button.on")].map((b) => b.dataset.value);
}

async function loadCashflow() {
  if (!state.filters) return;
  if (state.abort) state.abort.abort();
  state.abort = new AbortController();
  showError("");
  els.meta.textContent = "Yükleniyor…";
  const params = new URLSearchParams({
    date: els.date.value,
    approach: selected("flt-approach")[0] || "Liquidity",
    balanceType: els.balance.value || "TOTAL",
    depth: els.depth.value,
    coreDeposits: selected("flt-tenor")[0] === "core" ? "true" : "false"
  });
  const ccy = els.ccy.value;
  if (ccy && ccy !== "Tümü") params.set("ccy", ccy);
  const pools = selected("flt-pool");
  if (pools.length === 1) params.set("pools", pools[0]);
  try {
    state.cashflow = await getJson(`/api/alm/report/cashflow?${params}`, state.abort.signal);
    state.expanded = new Set((state.cashflow.tree || []).filter((n) => n.children?.length).map((n) => n.id));
    render();
    if (state.view === "duration") await loadDuration();
  } catch (err) {
    if (err.name !== "AbortError") showError(err.message);
  }
}

async function loadDuration() {
  try {
    state.duration = await getJson(`/api/alm/report/duration?date=${encodeURIComponent(els.date.value)}`);
    renderDuration();
  } catch (err) {
    showError(err.message);
  }
}

function render() {
  if (!state.cashflow) return;
  renderKpis();
  const ccy = state.cashflow.ccy || "Tümü";
  const pools = (state.cashflow.pools || []).join(", ") || "Katılma + Özkaynak";
  els.meta.textContent = `${fmtDate(state.cashflow.reportingDate)} · ${state.cashflow.approach} · ${ccy} · ${pools} · ${state.cashflow.coreDeposits ? "Çekirdek mevduat" : "Sözleşmesel vade"}`;
  els.table.hidden = state.view !== "table";
  els.chart.hidden = state.view !== "chart";
  els.duration.hidden = state.view !== "duration";
  if (state.view === "table") renderTable();
  if (state.view === "chart") renderChart();
  if (state.view === "duration") renderDuration();
}

function renderKpis() {
  const k = state.cashflow.kpis;
  els.kpis.innerHTML = [
    card("Varlıklar", k.assets),
    card("Yükümlülükler", k.liabilities),
    card("Bil. içi net", k.onBalanceGap),
    card("Toplam net açık/fazla", k.totalGap)
  ].join("");
}

function card(label, value) {
  const cls = value < 0 ? "neg" : value > 0 ? "pos" : "";
  return `<article class="kpi ${cls}"><span>${label}</span><strong>${fmt(value)}</strong></article>`;
}

function renderTable() {
  const data = state.cashflow;
  if (!data) return;
  const tenors = data.tenors || [];
  const rows = [];
  walk(data.tree || [], true, rows);
  const head = `<thead><tr><th class="item">Kalem</th><th>Toplam</th>${tenors.map((t) => `<th>${t.label}</th>`).join("")}</tr></thead>`;
  const body = rows.map((r) => {
    const n = r.node;
    const match = !state.search || n.label.toLocaleLowerCase("tr-TR").includes(state.search);
    const hidden = !r.visible || !match ? "hidden" : "";
    const hasKids = n.children?.length;
    const open = state.expanded.has(n.id);
    const toggle = hasKids
      ? `<button class="tree-btn" data-id="${n.id}" aria-expanded="${open}">${open ? "−" : "+"}</button>`
      : `<span class="tree-btn"></span>`;
    const cells = [n.total, ...(n.amounts || [])].map((v) => `<td class="num ${v < 0 ? "neg" : ""}">${fmtCell(v)}</td>`).join("");
    return `<tr class="${n.kind} ${hidden}"><td class="item pad-${n.level}">${toggle}${escapeHtml(n.label)}</td>${cells}</tr>`;
  }).join("");
  els.table.innerHTML = `<table>${head}<tbody>${body}</tbody></table>`;
  els.table.querySelectorAll(".tree-btn[data-id]").forEach((btn) => {
    btn.addEventListener("click", () => {
      const id = btn.dataset.id;
      if (state.expanded.has(id)) state.expanded.delete(id);
      else state.expanded.add(id);
      renderTable();
    });
  });
}

function walk(nodes, parentOpen, acc) {
  for (const node of nodes || []) {
    acc.push({ node, visible: parentOpen });
    const open = parentOpen && state.expanded.has(node.id);
    walk(node.children, open, acc);
  }
}

function collectIds(nodes, acc = []) {
  for (const n of nodes || []) {
    if (n.children?.length) acc.push(n.id);
    collectIds(n.children, acc);
  }
  return acc;
}

function walkAll(nodes, acc) {
  for (const node of nodes || []) {
    acc.push({ node, visible: true });
    walkAll(node.children, acc);
  }
}

function renderChart() {
  const data = state.cashflow;
  const svg = els.svg;
  const w = svg.clientWidth || 960;
  const h = 430;
  svg.setAttribute("viewBox", `0 0 ${w} ${h}`);
  const pad = { l: 58, r: 16, t: 18, b: 78 };
  const tenors = data.tenors || [];
  const series = [
    { key: "assets", color: "#1d3b5a", values: data.chart.assets },
    { key: "liabilities", color: "#c45c3e", values: data.chart.liabilities },
    { key: "derivativesNet", color: "#3d9aa3", values: data.chart.derivativesNet }
  ];
  const gap = data.chart.gap || [];
  const cum = data.chart.cumulativeGap || [];
  const all = [...series.flatMap((s) => s.values), ...gap, ...cum, 0];
  const min = Math.min(...all);
  const max = Math.max(...all);
  const span = max - min || 1;
  const innerW = w - pad.l - pad.r;
  const innerH = h - pad.t - pad.b;
  const n = tenors.length;
  const groupW = innerW / n;
  const barW = Math.max(3, (groupW - 6) / 3);
  const y = (v) => pad.t + (max - v) / span * innerH;
  const x = (i) => pad.l + i * groupW + 4;
  const zero = y(0);
  const ticks = [max, 0, min];
  const bars = series.flatMap((s, si) => s.values.map((v, i) => {
    const x0 = x(i) + si * barW;
    const y0 = Math.min(zero, y(v));
    const height = Math.max(1, Math.abs(y(v) - zero));
    return `<rect x="${x0}" y="${y0}" width="${barW}" height="${height}" fill="${s.color}" opacity="0.9"><title>${tenors[i].label}: ${fmt(v)}</title></rect>`;
  })).join("");
  const line = (values, color, dash = "") => {
    const d = values.map((v, i) => `${i ? "L" : "M"} ${x(i) + groupW / 2} ${y(v)}`).join(" ");
    return `<path d="${d}" fill="none" stroke="${color}" stroke-width="2.2" stroke-dasharray="${dash}" />`;
  };
  const axis = ticks.map((t) => `<text x="${pad.l - 8}" y="${y(t) + 4}" text-anchor="end" fill="#5d6e69" font-size="11">${fmt(t)}</text>`).join("");
  const labels = tenors.map((t, i) => `<text transform="translate(${x(i) + groupW / 2},${h - 10}) rotate(-48)" font-size="10" fill="#5d6e69">${t.label}</text>`).join("");
  svg.innerHTML = `<rect x="0" y="0" width="${w}" height="${h}" fill="white"/>
    <line x1="${pad.l}" x2="${w - pad.r}" y1="${zero}" y2="${zero}" stroke="#d5e0dc"/>
    ${axis}${bars}${line(gap, "#7eb8c4")}${line(cum, "#2f7d5b")}${labels}`;
}

function renderDuration() {
  const data = state.duration;
  if (!data) {
    els.duration.innerHTML = "<p class='meta'>Durasyon yükleniyor…</p>";
    return;
  }
  const cols = [
    ["toplamBakiye", "Bakiye"],
    ["toplamPv01Try", "PV01 TRY"],
    ["agirlikliModDuration", "Mod. duration"],
    ["agirlikliMacDuration", "Mac. duration"],
    ["agirlikliYtm", "YTM"],
    ["agirlikliConvexity", "Convexity"],
    ["agirlikliKalanOmur", "Kalan ömür"],
    ["agirlikliGostergeGetiri", "Gösterge getiri"]
  ];
  const rows = [];
  walkAll(data.tree || [], rows);
  const head = `<thead><tr><th class="item">Kalem</th>${cols.map((c) => `<th>${c[1]}</th>`).join("")}</tr></thead>`;
  const body = rows.map((r) => {
    const n = r.node;
    const cells = cols.map(([key]) => {
      const v = n[key];
      const metric = key !== "toplamBakiye" && key !== "toplamPv01Try";
      return `<td class="num">${v == null ? "" : metric ? Number(v).toLocaleString("tr-TR", { maximumFractionDigits: 4 }) : fmt(v)}</td>`;
    }).join("");
    return `<tr class="${n.kind} ${r.visible ? "" : "hidden"}"><td class="item pad-${n.level}">${escapeHtml(n.label)}</td>${cells}</tr>`;
  }).join("");
  els.duration.innerHTML = `<table>${head}<tbody>${body}</tbody></table>`;
}

function fmt(value) {
  const n = Number(value) || 0;
  if (state.compact) {
    const abs = Math.abs(n);
    if (abs >= 1e9) return `${(n / 1e9).toLocaleString("tr-TR", { maximumFractionDigits: 1 })} mr`;
    if (abs >= 1e6) return `${(n / 1e6).toLocaleString("tr-TR", { maximumFractionDigits: 1 })} mn`;
  }
  return n.toLocaleString("tr-TR", { maximumFractionDigits: 0 });
}

function fmtCell(value) {
  const n = Number(value) || 0;
  if (Math.abs(n) < 0.5) return "";
  return fmt(n);
}

function fmtDate(value) {
  if (!value) return "";
  const [y, m, d] = value.split("-");
  return `${d}.${m}.${y}`;
}

function fillSelect(select, values, current) {
  select.innerHTML = values.map((v) => `<option ${v === current ? "selected" : ""}>${v}</option>`).join("");
}

function showError(message) {
  els.error.hidden = !message;
  els.error.textContent = message || "";
}

async function getJson(url, signal) {
  const res = await fetch(url, { signal });
  if (!res.ok) {
    let detail = res.statusText;
    try { detail = (await res.json()).error || detail; } catch {}
    throw new Error(detail);
  }
  return res.json();
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}
