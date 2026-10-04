// ส่วนกลางของทุกหน้า: Supabase client, Google login, เมนู, ตัวช่วยจัดรูปแบบ
import { createClient } from "https://cdn.jsdelivr.net/npm/@supabase/supabase-js@2/+esm";
import { SUPABASE_URL, SUPABASE_ANON_KEY, LAB_API } from "./config.js";

export const sb = createClient(SUPABASE_URL, SUPABASE_ANON_KEY);

const PAGES = [
  ["index.html", "ผลทดสอบ"],
  ["strategies.html", "คลังทฤษฎี"],
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
export async function labApi(path) {
  const { data: { session } } = await sb.auth.getSession();
  const res = await fetch(`${LAB_API}${path}`, {
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

export function showError(err) {
  console.error(err);
  $("main").append(h("p", { class: "error" }, `เกิดข้อผิดพลาด: ${err.message ?? err}`));
}
