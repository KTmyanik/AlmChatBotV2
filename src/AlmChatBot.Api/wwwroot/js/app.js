const examples = [
  "Türev finansal araçların son rapor tarihindeki toplam bakiyesi nedir?",
  "Vade dilimlerine göre kredi bakiyesi dağılımı nasıl?",
  "Likidite TRY krediler tutarının en yüksek olduğu tarih ne?",
  "Son rapor tarihinde kredilerin modified duration'ı nedir?"
];

const chips = document.getElementById("chips");
const form = document.getElementById("ask-form");
const question = document.getElementById("question");
const askBtn = document.getElementById("ask-btn");
const analyzeBtn = document.getElementById("analyze-btn");
const bulletinBtn = document.getElementById("bulletin-btn");
const busy = document.getElementById("busy");
const errorBox = document.getElementById("error");
const result = document.getElementById("result");
const insight = document.getElementById("insight");
const meta = document.getElementById("meta");
const table = document.getElementById("data-table");
const sql = document.getElementById("sql");
const explanation = document.getElementById("explanation");
const assumptions = document.getElementById("assumptions");
const modelStatus = document.getElementById("model-status");
const confirmBox = document.getElementById("confirm");
const confirmSummary = document.getElementById("confirm-summary");
const confirmQuestion = document.getElementById("confirm-question");
const confirmCorrections = document.getElementById("confirm-corrections");
const confirmYes = document.getElementById("confirm-yes");
const confirmNo = document.getElementById("confirm-no");
const bulletinBox = document.getElementById("bulletin");
const bulletinMeta = document.getElementById("bulletin-meta");
const bulletinSituation = document.getElementById("bulletin-situation");
const bulletinRisks = document.getElementById("bulletin-risks");
const chartBtn = document.getElementById("chart-btn");
const chartBarBtn = document.getElementById("chart-bar");
const chartLineBtn = document.getElementById("chart-line");
const chartPanel = document.getElementById("chart-panel");
const chartSvg = document.getElementById("chart-svg");
const chartCaption = document.getElementById("chart-caption");
const dataSplit = document.querySelector(".data-split");
let busyTimer;
let pendingSuggestion = "";
let lastQuestion = "";
let lastResult = null;
let chartModel = null;
let chartKind = "bar";
let chartVisible = false;

examples.forEach((text) => {
  const btn = document.createElement("button");
  btn.type = "button";
  btn.textContent = text;
  btn.addEventListener("click", () => {
    question.value = text;
    question.focus();
  });
  chips.appendChild(btn);
});

question.addEventListener("keydown", (event) => {
  if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
    event.preventDefault();
    form.requestSubmit();
  }
});

loadStatus();

confirmYes.addEventListener("click", async () => {
  const q = pendingSuggestion || confirmQuestion.textContent.trim();
  if (!q) return;
  question.value = q;
  hideConfirm();
  setBusy(true);
  hideError();
  try {
    const payload = await ask(q, true);
    renderResult(payload, q);
    setBusy(false);
  } catch (err) {
    showError(err.message || String(err));
    setBusy(false);
  }
});

confirmNo.addEventListener("click", () => {
  hideConfirm();
  question.focus();
});

bulletinBtn.addEventListener("click", async () => {
  hideError();
  hideConfirm();
  setBusy(true, "Bülten derleniyor…");
  try {
    const response = await fetch("/api/alm/bulletin");
    const payload = await response.json();
    if (!response.ok) {
      throw new Error(payload.error || `Bülten alınamadı (${response.status})`);
    }
    renderBulletin(payload);
  } catch (err) {
    showError(err.message || String(err));
  } finally {
    setBusy(false);
  }
});

analyzeBtn.addEventListener("click", async () => {
  if (!lastResult?.generatedSql) {
    showError("Önce Sor ile tabloyu çekin, ardından Analiz’e basın.");
    return;
  }

  hideError();
  setBusy(true, "Analiz yazılıyor…");
  insight.textContent = "Analiz yazılıyor…";
  result.hidden = false;
  try {
    await loadInsight(lastQuestion, lastResult);
  } finally {
    setBusy(false);
  }
});

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  const q = question.value.trim();
  if (!q) return;

  setBusy(true);
  hideError();
  hideConfirm();
  lastResult = null;
  lastQuestion = "";
  analyzeBtn.disabled = true;
  result.hidden = true;
  chartModel = null;
  setChartVisible(false);

  try {
    const payload = await ask(q, false);
    if (payload.needsConfirmation) {
      showConfirm(payload);
      setBusy(false);
      return;
    }
    renderResult(payload, q);
    setBusy(false);
  } catch (err) {
    showError(err.message || String(err));
    setBusy(false);
  }
});

async function loadStatus() {
  try {
    const response = await fetch("/api/alm/status");
    const data = await response.json();
    const planner = data.sqlGeneration === "rules-first" ? "kural + " : "";
    modelStatus.textContent = data.modelName
      ? `${planner}${data.modelName} · ${shortHost(data.baseUrl)}`
      : "API'ye bağlanılamadı";
  } catch {
    modelStatus.textContent = "API'ye bağlanılamadı";
  }
}

async function ask(q, confirmed) {
  const response = await fetch("/api/alm/ask", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ question: q, confirmed })
  });
  const payload = await response.json();
  if (!response.ok) {
    throw new Error(payload.error || `İstek başarısız (${response.status})`);
  }
  return payload;
}

function showConfirm(payload) {
  pendingSuggestion = payload.suggestedQuestion || "";
  confirmSummary.textContent = payload.interpretationSummary || "";
  confirmQuestion.textContent = pendingSuggestion;
  confirmCorrections.replaceChildren();
  (payload.corrections || []).forEach((item) => {
    const li = document.createElement("li");
    li.textContent = item;
    confirmCorrections.appendChild(li);
  });
  confirmBox.hidden = false;
}

function hideConfirm() {
  confirmBox.hidden = true;
  pendingSuggestion = "";
}

function shortHost(url) {
  try {
    return new URL(url).host;
  } catch {
    return url || "";
  }
}

function setBusy(on, label) {
  askBtn.disabled = on;
  analyzeBtn.disabled = on || !lastResult?.generatedSql;
  bulletinBtn.disabled = on;
  chartBtn.disabled = on || !chartModel;
  chartBarBtn.disabled = on || !chartVisible;
  chartLineBtn.disabled = on || !chartVisible;
  busy.hidden = !on;
  if (busyTimer) {
    clearInterval(busyTimer);
    busyTimer = null;
  }
  const prefix = label || "Sorgu çalıştırılıyor…";
  if (on) {
    const started = Date.now();
    const tick = () => {
      const seconds = Math.floor((Date.now() - started) / 1000);
      busy.textContent = `${prefix} ${seconds} sn`;
    };
    tick();
    busyTimer = setInterval(tick, 1000);
  } else {
    busy.textContent = "Sorgu çalıştırılıyor…";
  }
}

function fillList(el, items) {
  el.replaceChildren();
  (items || []).forEach((item) => {
    const li = document.createElement("li");
    li.textContent = item;
    el.appendChild(li);
  });
}

function renderBulletin(data) {
  bulletinBox.hidden = false;
  const bits = [];
  if (data.date) bits.push(`Rapor ${data.date}`);
  if (data.durationDate) bits.push(`Duration ${data.durationDate}`);
  bits.push("Liquidity · TOTAL · InternalReports + InternalDurationReports");
  bulletinMeta.textContent = bits.join(" · ");
  fillList(bulletinSituation, data.situation);
  fillList(bulletinRisks, data.risks);
}

function showError(message) {
  errorBox.hidden = false;
  errorBox.textContent = message;
}

function hideError() {
  errorBox.hidden = true;
  errorBox.textContent = "";
}

function renderResult(data, questionText) {
  lastResult = data;
  lastQuestion = questionText || lastQuestion;
  analyzeBtn.disabled = !data.generatedSql;
  result.hidden = false;
  insight.textContent = data.generatedSql
    ? "Tablo hazır. Yorum için Analiz’e basın."
    : "Tablo geldikten sonra Analiz’e basın.";
  const bits = [];
  if (typeof data.rowCount === "number") bits.push(`${data.rowCount} satır`);
  if (data.truncated) bits.push("sonuç kesildi (TOP 200)");
  if (typeof data.executionDurationMs === "number") bits.push(`${data.executionDurationMs} ms`);
  if (data.sqlSource === "rules") bits.push("SQL kural ile");
  else if (data.sqlSource === "llm") bits.push("SQL model ile");
  meta.textContent = bits.join(" · ");
  sql.textContent = data.generatedSql || "";
  explanation.textContent = data.explanation || "";
  assumptions.replaceChildren();
  (data.assumptions || []).forEach((item) => {
    const li = document.createElement("li");
    li.textContent = item;
    assumptions.appendChild(li);
  });
  renderTable(data.data || []);
  prepareChart(data.data || []);
}

chartBtn.addEventListener("click", () => {
  if (!chartModel) {
    showError("Bu sonuç grafiğe çevrilecek kırılım veya zaman serisi taşımıyor.");
    return;
  }
  hideError();
  setChartVisible(!chartVisible);
});

chartBarBtn.addEventListener("click", () => {
  chartKind = "bar";
  renderChart();
});

chartLineBtn.addEventListener("click", () => {
  chartKind = "line";
  renderChart();
});

async function loadInsight(questionText, payload) {
  if (!payload.generatedSql) {
    insight.textContent = "Tablo geldikten sonra Analiz’e basın.";
    return;
  }
  try {
    const response = await fetch("/api/alm/insight", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        question: questionText,
        sql: payload.generatedSql,
        data: (payload.data || []).slice(0, 10),
        rowCount: payload.rowCount ?? 0
      })
    });
    const body = await response.json();
    if (!response.ok) {
      throw new Error(body.error || "Özet alınamadı");
    }
    insight.textContent = body.insightSummary || "Özet üretilmedi.";
  } catch (err) {
    insight.textContent = `Analiz yazılamadı: ${err.message || err}`;
  }
}

function renderTable(rows) {
  table.replaceChildren();
  if (!rows.length) {
    const empty = document.createElement("tbody");
    empty.innerHTML = "<tr><td>Satır yok.</td></tr>";
    table.appendChild(empty);
    return;
  }

  const columns = [...new Set(rows.flatMap((row) => Object.keys(row)))];
  const thead = document.createElement("thead");
  const headRow = document.createElement("tr");
  columns.forEach((col) => {
    const th = document.createElement("th");
    th.textContent = col;
    headRow.appendChild(th);
  });
  thead.appendChild(headRow);

  const tbody = document.createElement("tbody");
  rows.forEach((row) => {
    const tr = document.createElement("tr");
    columns.forEach((col) => {
      const td = document.createElement("td");
      const value = row[col];
      if (typeof value === "number") {
        td.className = "num";
        td.textContent = new Intl.NumberFormat("tr-TR", { maximumFractionDigits: 4 }).format(value);
      } else {
        td.textContent = value == null ? "" : String(value);
      }
      tr.appendChild(td);
    });
    tbody.appendChild(tr);
  });

  table.appendChild(thead);
  table.appendChild(tbody);
}

const NS = "http://www.w3.org/2000/svg";
const TENOR_ORDER = [
  "DAY_1", "DAY_2", "DAY_3", "DAY_4", "DAY_5", "DAY_6", "DAY_7",
  "DAY_8_15", "DAY_16_30",
  "MONTH_1_2", "MONTH_2_3", "MONTH_3_4", "MONTH_4_5", "MONTH_5_6",
  "MONTH_6_9", "MONTH_9_12", "MONTH_12_18", "MONTH_18_24",
  "YEAR_2_3", "YEAR_3_4", "YEAR_4_5", "YEAR_5_6", "YEAR_6_7", "YEAR_7_8",
  "YEAR_8_9", "YEAR_9_10", "YEAR_10_15", "YEAR_15_20", "YEAR_20_PLUS"
];
const TENOR_LABELS = {
  DAY_1: "1. gün", DAY_2: "2. gün", DAY_3: "3. gün", DAY_4: "4. gün",
  DAY_5: "5. gün", DAY_6: "6. gün", DAY_7: "7. gün",
  DAY_8_15: "8–15 gün", DAY_16_30: "16–30 gün",
  MONTH_1_2: "1–2 ay", MONTH_2_3: "2–3 ay", MONTH_3_4: "3–4 ay",
  MONTH_4_5: "4–5 ay", MONTH_5_6: "5–6 ay", MONTH_6_9: "6–9 ay",
  MONTH_9_12: "9–12 ay", MONTH_12_18: "12–18 ay", MONTH_18_24: "18–24 ay",
  YEAR_2_3: "2–3 yıl", YEAR_3_4: "3–4 yıl", YEAR_4_5: "4–5 yıl",
  YEAR_5_6: "5–6 yıl", YEAR_6_7: "6–7 yıl", YEAR_7_8: "7–8 yıl",
  YEAR_8_9: "8–9 yıl", YEAR_9_10: "9–10 yıl", YEAR_10_15: "10–15 yıl",
  YEAR_15_20: "15–20 yıl", YEAR_20_PLUS: "20+ yıl"
};
const CHART_COLORS = ["#1f4d45", "#8a5a2f", "#3d6e8a", "#8a2f2f", "#5a6b3a", "#6b4d7a", "#2f6b5a", "#a67c4e"];
const MEASURE_HINTS = [
  "Tutar", "Toplam_Bakiye", "OutstandingBalance", "KisaVade",
  "Agirlikli_Mod_Duration", "ModifiedDuration", "MODIFIED_DURATION",
  "Toplam_PV01_TRY", "Pv01", "PV01_REPORTING_CCY", "PV01_DEAL_CCY",
  "MACAULAY_DURATION", "CONVEXITY", "YIELD_TO_MATURITY"
];

function prepareChart(rows) {
  chartModel = inferChart(rows);
  chartBtn.disabled = !chartModel;
  if (!chartModel) {
    setChartVisible(false);
    return;
  }
  chartKind = chartModel.preferred;
  setChartVisible(true);
}

function setChartVisible(on) {
  chartVisible = on && !!chartModel;
  chartPanel.hidden = !chartVisible;
  chartBarBtn.hidden = !chartVisible;
  chartLineBtn.hidden = !chartVisible;
  dataSplit.classList.toggle("with-chart", chartVisible);
  chartBtn.textContent = chartVisible ? "Gizle" : "Grafik";
  if (chartVisible) {
    renderChart();
  } else {
    chartSvg.replaceChildren();
    chartCaption.textContent = "";
  }
}

function inferChart(rows) {
  if (!rows || rows.length < 2) return null;
  const columns = [...new Set(rows.flatMap((row) => Object.keys(row)))];
  const measure = pickMeasure(columns, rows);
  if (!measure) return null;

  const dimCols = columns.filter((c) =>
    c !== measure && c !== "Sira" && c !== "BALANCE_TYPE" && !isMostlyNumeric(rows, c)
  );
  if (dimCols.length === 0) return null;

  const tenorCol = dimCols.find((c) => /^vade/i.test(c) || c === "VadeDilimi");
  const dateCol = dimCols.find((c) => /date|tarih/i.test(c));
  const uniqueDates = dateCol ? uniqueValues(rows, dateCol) : [];
  const xCol = tenorCol || (uniqueDates.length > 1 ? dateCol : pickCategory(dimCols, rows, dateCol));
  if (!xCol) return null;

  const seriesCols = dimCols.filter((c) => c !== xCol && c !== "Kalem");
  const kalemValues = uniqueValues(rows, "Kalem");
  if (kalemValues.length > 1 && !seriesCols.includes("Kalem")) {
    seriesCols.unshift("Kalem");
  }

  let xValues = uniqueValues(rows, xCol);
  if (tenorCol && xCol === tenorCol) {
    xValues = [...TENOR_ORDER.filter((t) => xValues.includes(t)), ...xValues.filter((t) => !TENOR_ORDER.includes(t))];
  } else if (dateCol && xCol === dateCol) {
    xValues = [...xValues].sort();
  }

  const seriesMap = new Map();
  rows.forEach((row) => {
    const x = stringify(row[xCol]);
    if (!x) return;
    const series = seriesCols.map((c) => stringify(row[c])).filter(Boolean).join(" · ") || "Toplam";
    const value = Number(row[measure]) || 0;
    if (!seriesMap.has(series)) seriesMap.set(series, new Map());
    const bucket = seriesMap.get(series);
    bucket.set(x, (bucket.get(x) || 0) + value);
  });

  let series = [...seriesMap.entries()].map(([name, map]) => ({
    name,
    total: [...map.values()].reduce((s, v) => s + Math.abs(v), 0),
    points: xValues.map((x) => map.get(x) || 0)
  }));
  series.sort((a, b) => b.total - a.total);
  if (series.length > 8) {
    const rest = series.slice(8);
    const other = rest.reduce((acc, s) => acc.map((v, i) => v + s.points[i]), series[0].points.map(() => 0));
    series = series.slice(0, 8);
    series.push({ name: "Diğer", total: other.reduce((s, v) => s + Math.abs(v), 0), points: other });
  }

  const hasSignal = series.some((s) => s.points.some((v) => v !== 0));
  if (!hasSignal || xValues.length < 2) return null;

  return {
    measure,
    xCol,
    xLabels: xValues.map(labelFor),
    series,
    preferred: uniqueDates.length > 1 && !tenorCol ? "line" : "bar"
  };
}

function pickMeasure(columns, rows) {
  const hinted = MEASURE_HINTS.find((name) => columns.some((c) => c.toLowerCase() === name.toLowerCase()));
  if (hinted) return columns.find((c) => c.toLowerCase() === hinted.toLowerCase());
  return columns.find((c) => isMostlyNumeric(rows, c) && c !== "Sira");
}

function pickCategory(dimCols, rows, dateCol) {
  const scored = dimCols
    .filter((c) => c !== dateCol)
    .map((c) => ({ c, n: uniqueValues(rows, c).length }))
    .filter((x) => x.n > 1)
    .sort((a, b) => b.n - a.n);
  return scored[0]?.c || dimCols[0];
}

function uniqueValues(rows, col) {
  return [...new Set(rows.map((r) => stringify(r[col])).filter(Boolean))];
}

function isMostlyNumeric(rows, col) {
  const vals = rows.map((r) => r[col]).filter((v) => v != null && v !== "");
  if (!vals.length) return false;
  return vals.filter((v) => typeof v === "number" || (typeof v === "string" && v.trim() !== "" && !Number.isNaN(Number(v)))).length >= vals.length * 0.8;
}

function stringify(value) {
  if (value == null) return "";
  return String(value).trim();
}

function labelFor(value) {
  return TENOR_LABELS[value] || value;
}

function compactNumber(value) {
  const abs = Math.abs(value);
  const sign = value < 0 ? "−" : "";
  if (abs >= 1e9) return `${sign}${(abs / 1e9).toLocaleString("tr-TR", { maximumFractionDigits: 1 })} mr`;
  if (abs >= 1e6) return `${sign}${(abs / 1e6).toLocaleString("tr-TR", { maximumFractionDigits: 1 })} mn`;
  return `${sign}${abs.toLocaleString("tr-TR", { maximumFractionDigits: 0 })}`;
}

function renderChart() {
  if (!chartModel) return;
  chartBarBtn.classList.toggle("active", chartKind === "bar");
  chartLineBtn.classList.toggle("active", chartKind === "line");
  const { xLabels, series, measure, xCol } = chartModel;
  const width = 640;
  const height = 280;
  const pad = { top: 36, right: 12, bottom: 56, left: 54 };
  const innerW = width - pad.left - pad.right;
  const innerH = height - pad.top - pad.bottom;
  const all = series.flatMap((s) => s.points);
  const min = Math.min(0, ...all);
  const max = Math.max(0, ...all);
  const span = max - min || 1;
  const yOf = (v) => pad.top + innerH - ((v - min) / span) * innerH;
  const zero = yOf(0);

  chartSvg.setAttribute("viewBox", `0 0 ${width} ${height}`);
  chartSvg.replaceChildren();

  const axis = svgEl("g", { stroke: "#ddd4c6", "stroke-width": "1" });
  axis.appendChild(svgEl("line", { x1: pad.left, y1: pad.top, x2: pad.left, y2: pad.top + innerH }));
  axis.appendChild(svgEl("line", { x1: pad.left, y1: zero, x2: pad.left + innerW, y2: zero }));
  chartSvg.appendChild(axis);

  [min, 0, max].filter((v, i, a) => a.indexOf(v) === i).forEach((tick) => {
    const y = yOf(tick);
    const text = svgEl("text", {
      x: pad.left - 6,
      y: y + 3,
      fill: "#6b6258",
      "font-size": "10",
      "text-anchor": "end"
    });
    text.textContent = compactNumber(tick);
    chartSvg.appendChild(text);
  });

  const groupCount = xLabels.length;
  const groupW = innerW / groupCount;
  const barW = Math.max(2, (groupW * 0.72) / series.length);

  if (chartKind === "bar") {
    series.forEach((s, si) => {
      s.points.forEach((v, xi) => {
        const x = pad.left + xi * groupW + groupW * 0.14 + si * barW;
        const y = Math.min(zero, yOf(v));
        const h = Math.max(1, Math.abs(yOf(v) - zero));
        const rect = svgEl("rect", {
          x,
          y,
          width: barW,
          height: h,
          fill: CHART_COLORS[si % CHART_COLORS.length]
        });
        rect.appendChild(svgTitle(`${s.name} · ${xLabels[xi]}: ${compactNumber(v)}`));
        chartSvg.appendChild(rect);
      });
    });
  } else {
    series.forEach((s, si) => {
      const color = CHART_COLORS[si % CHART_COLORS.length];
      const d = s.points.map((v, xi) => {
        const x = pad.left + xi * groupW + groupW / 2;
        const y = yOf(v);
        return `${xi === 0 ? "M" : "L"}${x} ${y}`;
      }).join(" ");
      const path = svgEl("path", { d, fill: "none", stroke: color, "stroke-width": "2" });
      chartSvg.appendChild(path);
      s.points.forEach((v, xi) => {
        const x = pad.left + xi * groupW + groupW / 2;
        const y = yOf(v);
        const dot = svgEl("circle", { cx: x, cy: y, r: 3, fill: color });
        dot.appendChild(svgTitle(`${s.name} · ${xLabels[xi]}: ${compactNumber(v)}`));
        chartSvg.appendChild(dot);
      });
    });
  }

  const step = groupCount > 12 ? Math.ceil(groupCount / 10) : 1;
  xLabels.forEach((label, i) => {
    if (i % step !== 0 && i !== groupCount - 1) return;
    const text = svgEl("text", {
      x: pad.left + i * groupW + groupW / 2,
      y: height - 8,
      fill: "#6b6258",
      "font-size": "9",
      "text-anchor": "end",
      transform: `rotate(-40 ${pad.left + i * groupW + groupW / 2} ${height - 8})`
    });
    text.textContent = label;
    chartSvg.appendChild(text);
  });

  const legend = svgEl("g", {});
  series.forEach((s, si) => {
    const x = pad.left + (si % 4) * 150;
    const y = 12 + Math.floor(si / 4) * 12;
    legend.appendChild(svgEl("rect", {
      x,
      y: y - 7,
      width: 8,
      height: 8,
      fill: CHART_COLORS[si % CHART_COLORS.length]
    }));
    const t = svgEl("text", { x: x + 12, y, fill: "#1c1916", "font-size": "10" });
    t.textContent = s.name;
    legend.appendChild(t);
  });
  chartSvg.appendChild(legend);

  chartCaption.textContent = `${measure} · ${labelFor(xCol)} · ${chartKind === "line" ? "çizgi" : "çubuk"}`;
}

function svgEl(name, attrs) {
  const el = document.createElementNS(NS, name);
  Object.entries(attrs).forEach(([k, v]) => el.setAttribute(k, String(v)));
  return el;
}

function svgTitle(text) {
  const t = document.createElementNS(NS, "title");
  t.textContent = text;
  return t;
}
