-- cBot Backtest Lab — Supabase schema
-- รันครั้งเดียวใน Supabase Dashboard → SQL Editor
-- Supabase เก็บเฉพาะ "สรุป" (ค้น/กรอง/เรียงได้) ส่วน trades + equity เต็มอยู่ใน Google Drive (drive_file_id)

create table if not exists public.strategies (
  id          bigint generated always as identity primary key,
  owner       uuid not null references auth.users on delete cascade,
  code        text not null,                 -- เช่น EMA_CROSS
  version     int  not null default 1,
  name        text not null,
  theory      text,                          -- แนวคิด/ทฤษฎีเบื้องหลัง
  entry_rules jsonb not null default '[]',   -- ["EMA {Fast} ตัดขึ้น EMA {Slow} → Buy", ...]
  exit_rules  jsonb not null default '[]',
  param_names jsonb not null default '[]',   -- ชื่อของ P1..P4
  notes       text,                          -- โน้ตของเราเอง (แก้จากหน้าเว็บได้)
  created_at  timestamptz not null default now(),
  unique (owner, code, version)
);

create table if not exists public.backtest_runs (
  id             bigint generated always as identity primary key,
  owner          uuid not null references auth.users on delete cascade,
  strategy_id    bigint not null references public.strategies on delete cascade,
  symbol         text not null,
  timeframe      text not null,
  date_from      timestamptz,
  date_to        timestamptz,
  params         jsonb not null default '{}', -- {"Fast":9,"Slow":21,"Lots":0.01,"SL":20,"TP":40}
  running_mode   text,                        -- SilentBacktesting / VisualBacktesting / Optimization
  start_balance  numeric,
  end_balance    numeric,
  net_profit     numeric,
  total_trades   int,
  win_rate       numeric,                     -- 0..100
  profit_factor  numeric,
  max_dd_pct     numeric,                     -- 0..100
  machine        text,                        -- ชื่อเครื่องที่รัน
  note           text,                        -- RunNote จากพารามิเตอร์ cBot
  drive_file_id  text,                        -- ไฟล์ runs/<id>.json.gz ใน Google Drive
  drive_error    text,                        -- ถ้าอัปโหลด Drive ไม่สำเร็จ
  created_at     timestamptz not null default now()
);

create index if not exists backtest_runs_owner_created on public.backtest_runs (owner, created_at desc);
create index if not exists backtest_runs_strategy on public.backtest_runs (strategy_id);

-- ---------- Row Level Security ----------
alter table public.strategies    enable row level security;
alter table public.backtest_runs enable row level security;

drop policy if exists "owner reads strategies" on public.strategies;
create policy "owner reads strategies" on public.strategies
  for select to authenticated using (owner = (select auth.uid()));

drop policy if exists "owner edits strategy notes" on public.strategies;
create policy "owner edits strategy notes" on public.strategies
  for update to authenticated using (owner = (select auth.uid())) with check (owner = (select auth.uid()));

drop policy if exists "owner reads runs" on public.backtest_runs;
create policy "owner reads runs" on public.backtest_runs
  for select to authenticated using (owner = (select auth.uid()));

drop policy if exists "owner edits run note" on public.backtest_runs;
create policy "owner edits run note" on public.backtest_runs
  for update to authenticated using (owner = (select auth.uid())) with check (owner = (select auth.uid()));

drop policy if exists "owner deletes runs" on public.backtest_runs;
create policy "owner deletes runs" on public.backtest_runs
  for delete to authenticated using (owner = (select auth.uid()));

-- เว็บแก้ได้แค่คอลัมน์โน้ต (ตัวเลขผลทดสอบแก้ไม่ได้)
revoke update on public.strategies    from authenticated;
revoke update on public.backtest_runs from authenticated;
grant  update (notes) on public.strategies    to authenticated;
grant  update (note)  on public.backtest_runs to authenticated;

-- ไม่มี policy insert: ข้อมูลเข้าได้ทาง Edge Function `lab` (service role) เท่านั้น

-- ---------- View สรุปต่อทฤษฎี (หน้า strategies) ----------
create or replace view public.strategy_stats with (security_invoker = true) as
select s.id, s.code, s.version, s.name,
       count(r.id)                    as runs,
       round(avg(r.net_profit), 2)    as avg_net_profit,
       max(r.net_profit)              as best_net_profit,
       round(avg(r.win_rate), 1)      as avg_win_rate,
       round(avg(r.max_dd_pct), 1)    as avg_max_dd_pct,
       max(r.created_at)              as last_run_at
from public.strategies s
left join public.backtest_runs r on r.strategy_id = s.id
group by s.id;
