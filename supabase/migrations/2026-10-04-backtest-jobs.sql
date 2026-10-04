-- ความคืบหน้าของ backtest ที่กำลังรัน (tools\backtest.ps1 ส่งมาทาง POST /progress ของ Edge Function "lab")
-- รันครั้งเดียวใน Supabase Dashboard → SQL Editor

create table if not exists public.backtest_jobs (
  id            text primary key,              -- รหัสงานจากเครื่องที่รัน เช่น 20261004-150036-CH_ASIA_BREAK-XAUUSD-m1
  owner         uuid not null references auth.users on delete cascade,
  strategy_code text not null,
  symbol        text,
  timeframe     text,
  date_from     text,                          -- ตามที่สั่งรัน (dd/MM/yyyy HH:mm)
  date_to       text,
  phase         text,                          -- Loading ... / Backtesting
  percent       numeric,                       -- 0..100 ของ phase ปัจจุบัน
  status        text not null default 'running',  -- running / done / failed
  run_id        bigint,                        -- backtest_runs.id เมื่อเสร็จ
  machine       text,
  note          text,
  started_at    timestamptz not null default now(),
  updated_at    timestamptz not null default now()
);

create index if not exists backtest_jobs_owner_updated on public.backtest_jobs (owner, updated_at desc);

alter table public.backtest_jobs enable row level security;

drop policy if exists "owner reads jobs" on public.backtest_jobs;
create policy "owner reads jobs" on public.backtest_jobs
  for select to authenticated using (owner = (select auth.uid()));

drop policy if exists "owner deletes jobs" on public.backtest_jobs;
create policy "owner deletes jobs" on public.backtest_jobs
  for delete to authenticated using (owner = (select auth.uid()));

-- ไม่มี policy insert/update: เขียนได้ทาง Edge Function `lab` (service role) เท่านั้น
