// cBot Backtest Lab — Supabase Edge Function "lab" (ไฟล์เดียว วางใน Dashboard ได้เลย)
//
// เส้นทาง (base = https://<project-ref>.supabase.co/functions/v1/lab)
//   POST /ingest              cBot ส่งผล backtest มา  (header x-ingest-key)
//   GET  /runs?limit=50       รายการผลสรุป            (x-ingest-key หรือ Bearer JWT ของเจ้าของ)
//   GET  /runs/:id            ผลสรุป 1 run + ทฤษฎี
//   GET  /runs/:id/file       trades + equity เต็มจาก Google Drive
//   POST /progress            ความคืบหน้าของ backtest ที่กำลังรัน (header x-ingest-key) → ตาราง backtest_jobs
//   DELETE /runs/:id          ลบ run + ย้ายไฟล์ Drive ลงถังขยะ (ทฤษฎีที่ไม่เหลือ run และไม่มีโน้ตจะถูกลบด้วย)
//   GET  /health              เช็คว่าตั้งค่าครบ
//
// Secrets ที่ต้องตั้ง (Dashboard → Edge Functions → Secrets):
//   INGEST_KEY            รหัสยาวๆ ที่ cBot ใช้ส่งข้อมูล (สุ่มเอง)
//   OWNER_ID              user id ของเรา (ได้หลังล็อกอิน Google บนเว็บครั้งแรก)
//   GOOGLE_CLIENT_ID      OAuth client (Web) จาก Google Cloud
//   GOOGLE_CLIENT_SECRET
//   GOOGLE_REFRESH_TOKEN  ได้จาก OAuth Playground, scope drive.file
//   DRIVE_ROOT_FOLDER     (ไม่บังคับ) ชื่อโฟลเดอร์ใน Drive, ค่าเริ่มต้น "BacktestLab"
// SUPABASE_URL และ SUPABASE_SERVICE_ROLE_KEY ระบบใส่ให้อัตโนมัติ
//
// ต้องปิด "Verify JWT" ของฟังก์ชันนี้ (ฟังก์ชันตรวจสิทธิ์เอง)

import { createClient } from "npm:@supabase/supabase-js@2";

const env = (k: string, d = "") => Deno.env.get(k) ?? d;
const admin = createClient(env("SUPABASE_URL"), env("SUPABASE_SERVICE_ROLE_KEY"), {
  auth: { persistSession: false },
});

const CORS: Record<string, string> = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type, x-ingest-key",
  "Access-Control-Allow-Methods": "GET, POST, DELETE, OPTIONS",
};

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { ...CORS, "Content-Type": "application/json" } });

class HttpError extends Error {
  constructor(public status: number, message: string) { super(message); }
}

// ---------------------------------------------------------------- auth

function safeEqual(a: string, b: string): boolean {
  if (!a || !b || a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

/** คืน owner id ถ้าผ่าน: ingest key (เครื่อง/Claude) หรือ JWT ของเจ้าของ (หน้าเว็บ) */
async function authorize(req: Request, allowJwt: boolean): Promise<string> {
  const owner = env("OWNER_ID");
  if (!owner) throw new HttpError(500, "OWNER_ID secret is not set");

  const key = req.headers.get("x-ingest-key") ?? "";
  if (key && safeEqual(key, env("INGEST_KEY"))) return owner;

  if (allowJwt) {
    const jwt = (req.headers.get("authorization") ?? "").replace(/^Bearer\s+/i, "");
    if (jwt) {
      const { data, error } = await admin.auth.getUser(jwt);
      if (!error && data.user?.id === owner) return owner;
    }
  }
  throw new HttpError(401, "unauthorized");
}

// ---------------------------------------------------------------- Google Drive

let tokenCache: { token: string; exp: number } | null = null;
const folderCache = new Map<string, string>();

async function driveToken(): Promise<string> {
  if (tokenCache && tokenCache.exp > Date.now() + 60_000) return tokenCache.token;
  const res = await fetch("https://oauth2.googleapis.com/token", {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      client_id: env("GOOGLE_CLIENT_ID"),
      client_secret: env("GOOGLE_CLIENT_SECRET"),
      refresh_token: env("GOOGLE_REFRESH_TOKEN"),
      grant_type: "refresh_token",
    }),
  });
  const body = await res.json();
  if (!res.ok) throw new Error(`google token: ${body.error ?? res.status} ${body.error_description ?? ""}`);
  tokenCache = { token: body.access_token, exp: Date.now() + body.expires_in * 1000 };
  return tokenCache.token;
}

async function driveFetch(url: string, init: RequestInit = {}): Promise<Response> {
  const token = await driveToken();
  const res = await fetch(url, { ...init, headers: { ...(init.headers ?? {}), Authorization: `Bearer ${token}` } });
  if (!res.ok) throw new Error(`drive ${res.status}: ${(await res.text()).slice(0, 300)}`);
  return res;
}

/** หา/สร้างโฟลเดอร์ (scope drive.file เห็นเฉพาะไฟล์ที่แอปนี้สร้าง จึงต้องสร้างเอง) */
async function ensureFolder(name: string, parent?: string): Promise<string> {
  const cacheKey = `${parent ?? "root"}/${name}`;
  const hit = folderCache.get(cacheKey);
  if (hit) return hit;

  const q = [
    `name = '${name.replace(/'/g, "\\'")}'`,
    "mimeType = 'application/vnd.google-apps.folder'",
    "trashed = false",
    parent ? `'${parent}' in parents` : "'root' in parents",
  ].join(" and ");
  const found = await (await driveFetch(
    `https://www.googleapis.com/drive/v3/files?fields=files(id)&q=${encodeURIComponent(q)}`,
  )).json();

  let id: string = found.files?.[0]?.id;
  if (!id) {
    const created = await (await driveFetch("https://www.googleapis.com/drive/v3/files?fields=id", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        name, mimeType: "application/vnd.google-apps.folder", parents: parent ? [parent] : undefined,
      }),
    })).json();
    id = created.id;
  }
  folderCache.set(cacheKey, id);
  return id;
}

async function gzip(text: string): Promise<ArrayBuffer> {
  const stream = new Blob([text]).stream().pipeThrough(new CompressionStream("gzip"));
  return await new Response(stream).arrayBuffer();
}

async function uploadRunFile(runId: number, content: unknown): Promise<string> {
  const root = await ensureFolder(env("DRIVE_ROOT_FOLDER", "BacktestLab"));
  const runs = await ensureFolder("runs", root);
  const data = await gzip(JSON.stringify(content));

  const boundary = "lab" + crypto.randomUUID();
  const meta = { name: `${runId}.json.gz`, parents: [runs], mimeType: "application/gzip" };
  const body = new Blob([
    `--${boundary}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n${JSON.stringify(meta)}\r\n`,
    `--${boundary}\r\nContent-Type: application/gzip\r\n\r\n`,
    data,
    `\r\n--${boundary}--`,
  ]);
  const res = await driveFetch("https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id", {
    method: "POST",
    headers: { "Content-Type": `multipart/related; boundary=${boundary}` },
    body,
  });
  return (await res.json()).id;
}

async function downloadRunFile(fileId: string): Promise<unknown> {
  const res = await driveFetch(`https://www.googleapis.com/drive/v3/files/${encodeURIComponent(fileId)}?alt=media`);
  const bytes = new Uint8Array(await res.arrayBuffer());
  const isGzip = bytes[0] === 0x1f && bytes[1] === 0x8b;
  const text = isGzip
    ? await new Response(new Blob([bytes]).stream().pipeThrough(new DecompressionStream("gzip"))).text()
    : new TextDecoder().decode(bytes);
  return JSON.parse(text);
}

// ---------------------------------------------------------------- handlers

type Ingest = {
  strategy: {
    code: string; version?: number; name: string; theory?: string;
    entryRules?: string[]; exitRules?: string[]; paramNames?: string[];
  };
  run: {
    symbol: string; timeframe: string; dateFrom?: string; dateTo?: string;
    params?: Record<string, unknown>; runningMode?: string;
    startBalance?: number; endBalance?: number; netProfit?: number; totalTrades?: number;
    winRate?: number; profitFactor?: number; maxDdPct?: number; machine?: string; note?: string;
  };
  trades?: unknown[];
  equity?: unknown[];
};

const num = (v: unknown) => (typeof v === "number" && Number.isFinite(v) ? v : null);

async function ingest(req: Request, owner: string) {
  let p: Ingest;
  try { p = await req.json(); } catch { throw new HttpError(400, "body must be JSON"); }
  if (!p?.strategy?.code || !p?.strategy?.name || !p?.run?.symbol || !p?.run?.timeframe) {
    throw new HttpError(400, "required: strategy.code, strategy.name, run.symbol, run.timeframe");
  }
  const s = p.strategy, r = p.run;

  const { data: strat, error: e1 } = await admin.from("strategies").upsert({
    owner, code: s.code, version: s.version ?? 1, name: s.name, theory: s.theory ?? null,
    entry_rules: s.entryRules ?? [], exit_rules: s.exitRules ?? [], param_names: s.paramNames ?? [],
  }, { onConflict: "owner,code,version" }).select("id").single();
  if (e1) throw new Error(`strategies: ${e1.message}`);

  const { data: run, error: e2 } = await admin.from("backtest_runs").insert({
    owner, strategy_id: strat.id, symbol: r.symbol, timeframe: r.timeframe,
    date_from: r.dateFrom ?? null, date_to: r.dateTo ?? null, params: r.params ?? {},
    running_mode: r.runningMode ?? null,
    start_balance: num(r.startBalance), end_balance: num(r.endBalance), net_profit: num(r.netProfit),
    total_trades: num(r.totalTrades), win_rate: num(r.winRate), profit_factor: num(r.profitFactor),
    max_dd_pct: num(r.maxDdPct), machine: r.machine ?? null, note: r.note ?? null,
  }).select("id").single();
  if (e2) throw new Error(`backtest_runs: ${e2.message}`);

  // ไฟล์หนักขึ้น Drive; ถ้าพังยังเก็บผลสรุปไว้ และบันทึก error ให้เห็นบนเว็บ
  let driveFileId: string | null = null, driveError: string | null = null;
  try {
    driveFileId = await uploadRunFile(run.id, {
      runId: run.id, strategy: { code: s.code, version: s.version ?? 1 },
      symbol: r.symbol, timeframe: r.timeframe, trades: p.trades ?? [], equity: p.equity ?? [],
    });
  } catch (err) {
    driveError = String((err as Error).message ?? err).slice(0, 500);
  }
  await admin.from("backtest_runs").update({ drive_file_id: driveFileId, drive_error: driveError }).eq("id", run.id);

  return json({ ok: true, runId: run.id, strategyId: strat.id, driveFileId, driveError });
}

/** tools\backtest.ps1 เรียกทุกไม่กี่วินาทีระหว่างรัน — เก็บสถานะล่าสุดของงานนั้น (หน้าเว็บอ่านตารางเองผ่าน RLS) */
async function progress(req: Request, owner: string) {
  // deno-lint-ignore no-explicit-any
  let p: any;
  try { p = await req.json(); } catch { throw new HttpError(400, "body must be JSON"); }
  if (!p?.id || !p?.strategyCode) throw new HttpError(400, "required: id, strategyCode");

  const text = (v: unknown, max = 200) => (v == null || v === "" ? null : String(v).slice(0, max));
  const status = ["running", "done", "failed"].includes(p.status) ? p.status : "running";
  const { error } = await admin.from("backtest_jobs").upsert({
    id: String(p.id).slice(0, 200), owner, strategy_code: String(p.strategyCode).slice(0, 100),
    symbol: text(p.symbol), timeframe: text(p.timeframe), date_from: text(p.dateFrom), date_to: text(p.dateTo),
    phase: text(p.phase), percent: num(p.percent), status, run_id: num(p.runId),
    machine: text(p.machine), note: text(p.note, 500), updated_at: new Date().toISOString(),
  }, { onConflict: "id" });
  if (error) throw new Error(`backtest_jobs: ${error.message}`);
  return json({ ok: true });
}

const RUN_FIELDS ="*, strategy:strategies(id, code, version, name, theory, entry_rules, exit_rules, param_names, notes)";

async function listRuns(url: URL, owner: string) {
  const limit = Math.min(Number(url.searchParams.get("limit") ?? 50) || 50, 1000);
  let q = admin.from("backtest_runs").select(RUN_FIELDS).eq("owner", owner)
    .order("created_at", { ascending: false }).limit(limit);
  const code = url.searchParams.get("strategy");
  const symbol = url.searchParams.get("symbol");
  if (symbol) q = q.eq("symbol", symbol);
  const { data, error } = await q;
  if (error) throw new Error(error.message);
  return json(code ? data.filter((r: { strategy?: { code?: string } }) => r.strategy?.code === code) : data);
}

async function getRun(id: number, owner: string) {
  const { data, error } = await admin.from("backtest_runs").select(RUN_FIELDS)
    .eq("owner", owner).eq("id", id).maybeSingle();
  if (error) throw new Error(error.message);
  if (!data) throw new HttpError(404, "run not found");
  return data;
}

async function deleteRun(id: number, owner: string) {
  const run = await getRun(id, owner);

  // ไฟล์ Drive ลงถังขยะ (กู้คืนได้ 30 วัน); ถ้าพังยังลบแถวต่อ แล้วแจ้ง error กลับ
  let driveError: string | null = null;
  if (run.drive_file_id) {
    try {
      await driveFetch(`https://www.googleapis.com/drive/v3/files/${encodeURIComponent(run.drive_file_id)}`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ trashed: true }),
      });
    } catch (err) {
      driveError = String((err as Error).message ?? err).slice(0, 500);
    }
  }

  const { error } = await admin.from("backtest_runs").delete().eq("owner", owner).eq("id", id);
  if (error) throw new Error(`backtest_runs: ${error.message}`);

  let strategyRemoved = false;
  const { count } = await admin.from("backtest_runs").select("id", { count: "exact", head: true })
    .eq("strategy_id", run.strategy_id);
  if (count === 0 && !run.strategy?.notes) {
    const { error: e2 } = await admin.from("strategies").delete().eq("owner", owner).eq("id", run.strategy_id);
    strategyRemoved = !e2;
  }
  return json({ ok: true, runId: id, driveTrashed: !!run.drive_file_id && !driveError, driveError, strategyRemoved });
}

// ---------------------------------------------------------------- router

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") return new Response("ok", { headers: CORS });
  const url = new URL(req.url);
  // pathname เป็น /lab/... (บางสภาพแวดล้อมเป็น /functions/v1/lab/...)
  const parts = url.pathname.split("/").filter(Boolean);
  const at = parts.indexOf("lab");
  const route = at >= 0 ? parts.slice(at + 1) : parts;

  try {
    if (req.method === "GET" && route[0] === "health") {
      const need = ["INGEST_KEY", "OWNER_ID", "GOOGLE_CLIENT_ID", "GOOGLE_CLIENT_SECRET", "GOOGLE_REFRESH_TOKEN"];
      return json({ ok: true, missing: need.filter((k) => !env(k)) });
    }
    if (req.method === "POST" && route[0] === "ingest") {
      return await ingest(req, await authorize(req, false));
    }
    if (req.method === "POST" && route[0] === "progress") {
      return await progress(req, await authorize(req, false));
    }
    if (req.method === "DELETE" && route[0] === "runs" && route.length === 2) {
      const owner = await authorize(req, true);
      const id = Number(route[1]);
      if (!Number.isInteger(id)) throw new HttpError(400, "bad run id");
      return await deleteRun(id, owner);
    }
    if (req.method === "GET" && route[0] === "runs") {
      const owner = await authorize(req, true);
      if (route.length === 1) return await listRuns(url, owner);
      const id = Number(route[1]);
      if (!Number.isInteger(id)) throw new HttpError(400, "bad run id");
      const run = await getRun(id, owner);
      if (route[2] === "file") {
        if (!run.drive_file_id) throw new HttpError(404, run.drive_error ?? "no drive file for this run");
        return json(await downloadRunFile(run.drive_file_id));
      }
      return json(run);
    }
    throw new HttpError(404, "not found");
  } catch (err) {
    const status = err instanceof HttpError ? err.status : 500;
    if (status >= 500) console.error(err);
    return json({ ok: false, error: String((err as Error).message ?? err) }, status);
  }
});
