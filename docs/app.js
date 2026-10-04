// ส่วนกลางของทุกหน้า: Supabase client, Google login, เมนู, ตัวช่วยจัดรูปแบบ
import { createClient } from "https://cdn.jsdelivr.net/npm/@supabase/supabase-js@2/+esm";
import { SUPABASE_URL, SUPABASE_ANON_KEY, LAB_API } from "./config.js";

export const sb = createClient(SUPABASE_URL, SUPABASE_ANON_KEY);

const PAGES = [
  ["summary.html", "สรุป"],
  ["index.html", "ผลทดสอบ"],
  ["strategies.html", "คลังทฤษฎี"],
  ["chart.html", "ทฤษฎีกราฟ"],
  ["daystudy.html", "สถิติรายวัน"],
];

export const $ = (sel, root = document) => root.querySelector(sel);

export function h(tag, attrs = {}, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs ?? {})) {
    if (v == null || v === false) continue;
    if (k === "class") el.className = v;
    else if (k.startsWith("on")) el.addEventListener(k.slice(2), v);
    else el.setAttribute(k, v === true ? "" : v);
  }
  for (const c of children.flat()) if (c != null && c !== false) el.append(c instanceof Node ? c : String(c));
  return el;
}

function renderHeader(session) {
  const here = location.pathname.split("/").pop() || "index.html";
  const header = h("header", { class: "top" },
    h("a", { class: "brand", href: "index.html" }, "Backtest Lab"),
    h("nav", {}, PAGES.map(([href, label]) =>
      h("a", { href, class: href === here ? "active" : null }, label))),
    session && h("div", { class: "who" },
      h("span", { class: "muted" }, session.user.email),
      h("button", { class: "ghost", onclick: async () => { await sb.auth.signOut(); location.reload(); } }, "ออกจากระบบ")),
  );
  document.body.prepend(header);
}

/** คืน session ถ้าล็อกอินแล้ว; ถ้ายัง แสดงปุ่ม Google แล้วคืน null */
export async function requireSession() {
  const { data: { session } } = await sb.auth.getSession();
  renderHeader(session);
  if (session) return session;

  const main = $("main");
  main.replaceChildren(h("section", { class: "login" },
    h("h1", {}, "เข้าสู่ระบบ"),
    h("p", { class: "muted" }, "ผลทดสอบเป็นข้อมูลส่วนตัว เข้าได้เฉพาะบัญชี Google ของเจ้าของ"),
    h("button", {
      class: "primary",
      onclick: () => sb.auth.signInWithOAuth({
        provider: "google",
        options: { redirectTo: location.origin + location.pathname + location.search },
      }),
    }, "เข้าสู่ระบบด้วย Google"),
  ));
  return null;
}

/** เรียก Edge Function "lab" ด้วย JWT ของเรา */
export async function labApi(path, method = "GET") {
  const { data: { session } } = await sb.auth.getSession();
  const res = await fetch(`${LAB_API}${path}`, {
    method,
    headers: { Authorization: `Bearer ${session?.access_token}`, apikey: SUPABASE_ANON_KEY },
  });
  const body = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(body.error ?? `HTTP ${res.status}`);
  return body;
}

// ---------- จัดรูปแบบ ----------
const nf2 = new Intl.NumberFormat("th-TH", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
export const money = (v) => (v == null ? "–" : nf2.format(v));
export const pct = (v) => (v == null ? "–" : `${nf2.format(v)}%`);
export const num = (v) => (v == null ? "–" : nf2.format(v));
export const date = (v) => (v ? new Date(v).toLocaleDateString("th-TH", { year: "2-digit", month: "short", day: "numeric" }) : "–");
export const dateTime = (v) => (v ? new Date(v).toLocaleString("th-TH", { dateStyle: "short", timeStyle: "short" }) : "–");
export const signClass = (v) => (v == null ? "" : v > 0 ? "pos" : v < 0 ? "neg" : "");

/** แทน {ชื่อพารามิเตอร์} ในกฎด้วยค่าจริงของ run นั้น */
export const describe = (rule, params = {}) =>
  String(rule).replace(/\{(\w+)\}/g, (m, k) => (k in params ? params[k] : m));

// ---------- สถิติแยกตามวันในสัปดาห์ (ตามเวลาเข้าเทรด UTC; คืนวันอาทิตย์นับเป็นวันจันทร์) ----------
export const WEEKDAYS = ["จันทร์", "อังคาร", "พุธ", "พฤหัสบดี", "ศุกร์"];
export const tradeWeekday = (t) => {
  const d = new Date(t.entryTime).getUTCDay();
  return d === 0 ? 0 : Math.min(d, 5) - 1;
};

export function tradeStats(trades) {
  const wins = trades.filter((t) => t.net > 0), losses = trades.filter((t) => t.net < 0);
  const sum = (xs) => xs.reduce((a, t) => a + t.net, 0);
  const grossWin = sum(wins), grossLoss = -sum(losses), decided = wins.length + losses.length;
  const minutes = trades.map((t) => (new Date(t.exitTime) - new Date(t.entryTime)) / 60000);
  return {
    n: trades.length, wins: wins.length, losses: losses.length,
    winPct: decided ? (100 * wins.length) / decided : null,
    lossPct: decided ? (100 * losses.length) / decided : null,
    pf: grossLoss > 0 ? grossWin / grossLoss : null,
    net: trades.length ? grossWin - grossLoss : null,
    avgWin: wins.length ? grossWin / wins.length : null,
    avgLoss: losses.length ? -grossLoss / losses.length : null,
    avgMinutes: minutes.length ? minutes.reduce((a, b) => a + b, 0) / minutes.length : null,
  };
}

/** สถิติของแต่ละวัน จันทร์..ศุกร์ */
export const weekdayStats = (trades) => WEEKDAYS.map((_, i) => tradeStats(trades.filter((t) => tradeWeekday(t) === i)));

/** แถบแบ่งสัดส่วน ชนะ / แพ้ (ตัวเลขต้องแสดงในคอลัมน์ข้างๆ เสมอ) */
export function splitBar(a, b, labelA = "ชนะ", labelB = "แพ้") {
  if (a == null) return "";
  return h("div", { class: "split", role: "img", "aria-label": `${labelA} ${a.toFixed(1)}% ${labelB} ${b.toFixed(1)}%` },
    h("span", { class: "up", style: `width:${a}%` }), h("span", { class: "down", style: `width:${b}%` }));
}

// ---------- ความคืบหน้าของ backtest ที่กำลังรัน (ตาราง backtest_jobs; tools\backtest.ps1 ส่งมาผ่าน lab) ----------
const PHASES = { Starting: "กำลังเริ่ม", Backtesting: "กำลังทดสอบ", Finished: "เสร็จ" };
const phaseLabel = (p) => PHASES[p] ?? (p?.startsWith("Loading") ? `โหลดข้อมูล ${p.replace(/^Loading\s*/, "")}` : p ?? "");

/** แสดงงานที่กำลังรัน + งานที่เพิ่งจบ ไว้บนสุดของ <main>; เงียบถ้ายังไม่มีตาราง */
export function mountJobs() {
  const box = h("section", { class: "stack jobs", "aria-live": "polite" });
  $("main").prepend(box);

  async function refresh() {
    const since = new Date(Date.now() - 30 * 60000).toISOString();
    const { data, error } = await sb.from("backtest_jobs").select("*").gte("updated_at", since)
      .order("started_at", { ascending: false }).limit(30);
    if (error) return;                                   // ยังไม่ได้สร้างตาราง → ไม่แสดงอะไร และเลิกถาม

    const now = Date.now();
    const rows = data.map((j) => ({ ...j, stale: j.status === "running" && now - new Date(j.updated_at) > 120000 }));
    const running = rows.filter((j) => j.status === "running" && !j.stale);
    box.replaceChildren(...(rows.length ? [
      h("h2", {}, running.length ? `กำลังทดสอบ (${running.length})` : "ทดสอบล่าสุด (30 นาที)"),
      h("div", { class: "table-wrap" }, h("table", {}, h("tbody", {}, rows.map((j) => h("tr", {},
        h("td", { class: "mono" }, j.strategy_code),
        h("td", {}, `${j.symbol ?? ""} · ${j.timeframe ?? ""}`),
        h("td", { class: "muted" }, `${j.date_from ?? ""} → ${j.date_to ?? ""}`),
        h("td", {}, j.status === "running" && !j.stale
          ? [h("progress", { max: 100, value: j.percent ?? 0, "aria-label": `${j.strategy_code} ${j.percent ?? 0}%` }),
             h("span", { class: "mono" }, ` ${Math.round(j.percent ?? 0)}%`), h("span", { class: "muted" }, ` ${phaseLabel(j.phase)}`)]
          : j.stale ? h("span", { class: "muted" }, `ไม่มีสัญญาณจากเครื่องที่รัน (ค้างที่ ${Math.round(j.percent ?? 0)}%)`)
          : j.status === "done" ? (j.run_id ? h("a", { href: `run.html?id=${j.run_id}` }, `เสร็จแล้ว · ดูผล #${j.run_id}`) : "เสร็จแล้ว")
          : h("span", { class: "neg" }, "ไม่สำเร็จ")),
        h("td", { class: "muted" }, j.note ?? ""),
        h("td", { class: "muted" }, j.machine ?? ""),
      ))))),
    ] : []));
    setTimeout(refresh, running.length ? 3000 : 20000);
  }
  refresh();
}

export function showError(err) {
  console.error(err);
  $("main").append(h("p", { class: "error" }, `เกิดข้อผิดพลาด: ${err.message ?? err}`));
}
