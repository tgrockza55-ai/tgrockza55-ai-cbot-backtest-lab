# cBot Backtest Lab — คู่มือสำหรับ Claude

โปรเจกต์ส่วนตัว: cBot ของ cTrader รัน backtest หลายทฤษฎี ส่งผลขึ้น Supabase (สรุป) + Google Drive (trades/equity เต็ม)
และแสดงบนเว็บ GitHub Pages ผู้ใช้สื่อสารภาษาไทย ตอบเป็นภาษาไทย

## โครงสร้าง

```
cbot/BacktestLab/            ซอร์ส cBot (C#, cTrader Automate .NET 6)
  BacktestLab.cs             Robot หลัก: พารามิเตอร์, เปิด/ปิดออเดอร์, สร้างรายงาน, EquityRecorder
  StrategyBase.cs            ฐานของทุกทฤษฎี + ค้นหาคลาสด้วย reflection จาก Code
  Strategies/*.cs            หนึ่งไฟล์ = หนึ่งทฤษฎี (Code ขึ้นต้น CH_ = ทฤษฎีกราฟ ใช้ราคาอย่างเดียว)
  Strategies/DataExport.cs   เครื่องมือ (ไม่ใช่ทฤษฎี): ส่งออกแท่งราคาเป็น CSV ไม่ส่งผลขึ้น lab
  SwingTracker.cs            หาจุดสวิงสูง/ต่ำแบบไม่มี look-ahead ใช้ร่วมกันในทฤษฎีกราฟ
  Reporter.cs                POST ไป /ingest, เก็บ pending เมื่อส่งไม่ได้
  LiveRecorder.cs            บันทึกตลาดสดตอนรันบน Demo (tick, DOM, สรุปรายนาที, ออเดอร์) — ดูหัวข้อ "รันสดบนบัญชี Demo"
supabase/schema.sql          ตาราง strategies, backtest_runs, view strategy_stats + RLS
supabase/migrations/*.sql    ส่วนเพิ่มของ schema (เช่น backtest_jobs) — ผู้ใช้รันใน SQL Editor ทีละไฟล์
supabase/functions/lab/      Edge Function: /ingest, /progress, /runs, /runs/:id (GET, DELETE), /runs/:id/file, /health
docs/                        เว็บ (GitHub Pages): summary (สรุป), index, run, strategies, chart (ทฤษฎีกราฟ), daystudy (สถิติรายวัน) + app.js, config.js
docs/data/daystudy-*.json    ผลสถิติรายวัน (สร้างโดย tools\day-study.ps1; เป็นไฟล์สาธารณะ)
tools/sync-cbot.ps1          ก๊อปซอร์สไป Documents\cAlgo\Sources\Robots\BacktestLab\BacktestLab + สร้าง config
tools/backtest.ps1           รัน backtest 1 รอบผ่าน cTrader CLI (เพิ่ม -Build เพื่อ sync + build ก่อน)
tools/day-study.ps1          สถิติ "วันนี้ของสัปดาห์ + ทรงนี้ → ขึ้น/ลงกี่ %" จาก CSV ของ DATA_EXPORT (-From/-To จำกัดช่วง)
tools/exit-study.ps1         วิจัยการออกจากเทรด: จำลองแผนการออกบนแท่ง M1 จากรายงานที่เก็บในเครื่อง
.mcp.json                    เชื่อม MCP ของ cTrader (project scope)
```

## เพิ่มทฤษฎีใหม่ (งานที่ผู้ใช้จะขอบ่อยที่สุด)

1. สร้าง `cbot/BacktestLab/Strategies/<ชื่อ>.cs` คลาส inherit `StrategyBase` (ดูตัวอย่าง `EmaCross.cs`, `RsiReversal.cs`)
2. ต้องกำหนด: `Code` (ตัวพิมพ์ใหญ่_ขีดล่าง ไม่ซ้ำ), `Name`, `Theory` (ภาษาไทย อธิบายว่าทำไมควรได้ผล),
   `ParamNames` (ความหมายของ P1..P4 สูงสุด 4 ตัว), `EntryRules`/`ExitRules` (ภาษาไทย ใช้ `{ชื่อพารามิเตอร์}` แทนค่า),
   `OnInit()` (สร้าง indicator จาก `Bot.Indicators`), `Signal()` (คืน Buy/Sell/null โดยดูแท่งที่ปิดแล้ว `.Last(1)`)
3. ทางเลือก: `ShouldExit(Position)`, `ExitOnOppositeSignal`
4. ห้ามแก้ตรรกะทฤษฎีเดิมโดยไม่เพิ่ม `Version` (ผลเก่าผูกกับเวอร์ชันเดิมในฐานข้อมูล)
5. หลีกเลี่ยง look-ahead: ห้ามใช้ `.LastValue` ของแท่งที่ยังไม่ปิดในการตัดสินใจ
6. Build + backtest: ถ้าเครื่องมี cTrader CLI ให้ใช้ `tools\backtest.ps1 -Build ...` (ดูหัวข้อถัดไป)
   ถ้าไม่มี ให้รัน `tools\sync-cbot.ps1` แล้วบอกผู้ใช้ให้กด Build + ตั้งค่า backtest ใน cTrader เอง
7. `git commit` + `git push` ได้เลยโดยไม่ต้องถาม (ผู้ใช้อนุญาตแล้ว: เว็บนี้เป็นข้อมูลแสดงอย่างเดียว) แล้วรายงานว่า push อะไรไป
   ยกเว้น: ห้ามมีความลับในไฟล์, การลบ run และการ deploy Edge Function ยังให้ผู้ใช้ทำเอง
8. cAlgo.API มีชนิดชื่อซ้ำกับ .NET (`File`, `HttpMethod`) — ถ้าใช้ `System.IO` / `System.Net.Http` ให้ใส่ `using X = ...;` กำกับ
9. จัดรูปแบบวันที่/ตัวเลขเป็นข้อความต้องใส่ `CultureInfo.InvariantCulture` เสมอ (เครื่องภาษาไทยจะได้ปี พ.ศ.)

### ทฤษฎีกราฟ (Code ขึ้นต้น `CH_`)

- ใช้ราคาอย่างเดียว ไม่สร้าง indicator; ใช้ helper ใน `StrategyBase`: `O/H/L/C(i)`, `Body`, `Range`, `HighestHigh`, `LowestLow`,
  `AvgRange`, `TradingDay`, `BarTime` (i = จำนวนแท่งย้อนหลัง, 1 = แท่งล่าสุดที่ปิดแล้ว) และ `SwingTracker` สำหรับจุดสวิง
- วาง SL ตามโครงสร้างราคา: override `StopDistance()` (ระยะเป็นราคา) + `RewardRisk`; robot บีบระยะให้อยู่ใน
  `MinStopPct`–`MaxStopPct` (% ของราคา) เพราะราคาทองเปลี่ยนเป็นเท่าตัวใน 3 ปี SL แบบ pips คงที่เทียบกันไม่ได้
- เรียก `Defaults(...)` ใน `OnInit()` เพื่อให้ P ที่ ≤ 0 ใช้ค่าเริ่มต้นของทฤษฎี และผลที่ส่งขึ้น lab แสดงค่าที่ใช้จริง
- **ต้องส่ง `-P1 -P2 -P3 -P4` ให้ครบทุกครั้ง** (ใส่ 0 = ใช้ค่าเริ่มต้นของทฤษฎี) เพราะค่าเริ่มต้นของ robot คือ P1=9, P2=21 (ของ EMA_CROSS)
- หน้า `chart.html` แสดงทุกทฤษฎีที่ Code ขึ้นต้น `CH_` อัตโนมัติ

### สถิติรายวัน

```powershell
# 1) ส่งออกแท่ง M1 (ไม่ส่งผลขึ้น lab จึงจบด้วย exit code 2 เป็นปกติ) → Documents\BacktestLab\data\XAUUSD_Minute.csv
powershell -ExecutionPolicy Bypass -File tools\backtest.ps1 -Strategy DATA_EXPORT -Symbol XAUUSD -Period m1 -Start "04/10/2023 00:00" -End "02/10/2026 00:00"
# 2) คำนวณ → docs\data\daystudy-XAUUSD.json แล้ว commit + push ให้หน้า daystudy.html อัปเดต
powershell -ExecutionPolicy Bypass -File tools\day-study.ps1 -Symbol XAUUSD
```

นิยาม (UTC): ช่วงเช้า = เปิดวันถึง 07:00, ช่วงที่เหลือ = 07:00 ถึงปิดวัน, ผล = ราคาปิดวันเทียบราคา ณ 07:00
กลุ่มที่มี < 30 วันเชื่อถือได้น้อย — ดูช่วงเชื่อมั่น 95% และ % แยกรายปีประกอบเสมอ ไม่มี Python ในเครื่องผู้ใช้ ใช้ PowerShell

### วิจัยหาส่วนผสมของทฤษฎี (ขั้นตอนที่ใช้กับ CH_COMBO)

กติกาของผู้ใช้: ถือทีละ 1 ออเดอร์ และปิดทุกออเดอร์ภายในวัน (`-FlatTime 2045` = 20:45 UTC ก่อนตลาดทองพักทั้งฤดูร้อน/หนาว)
ช่วงหากฎ 01/2020–02/2026, ช่วงทดสอบ 03/2026–09/2026 — **เลือกทฤษฎี/เงื่อนไข/วิธีออกจากช่วงหากฎเท่านั้น แล้วห้ามแก้กฎหลังเห็นผลช่วงทดสอบ**

1. รันทุกทฤษฎีบนช่วงหากฎแบบเก็บในเครื่อง: `tools\backtest.ps1 ... -Balance 100000 -NoSend -SaveLocal` → `Documents\BacktestLab\local\<code>-<symbol>-<tf>.json`
   (เงินต้นสูงเพื่อให้ได้รายการเทรดครบช่วง; ผลที่ขึ้นเว็บใช้เงินต้น 1,000 ตามที่ผู้ใช้กำหนด — หมดตัวแล้วหยุดเทรด)
2. ดูความสม่ำเสมอรายปีและเงื่อนไขย่อย (ฝั่ง, ช่วงเวลา, วัน) — ยิ่งดูหลายช่อง ยิ่งเจอช่องที่ดูดีโดยบังเอิญ ต้องบอกผู้ใช้
3. `tools\exit-study.ps1 -Report <ชื่อไฟล์ local> [-Side Buy] [-Weekdays 1] [-HourFrom/-HourTo]` — ตารางสถานะ ("ผ่านไป N นาที อยู่ระดับนี้ จบยังไง")
   และตารางแผนการออก (TP, เลื่อน SL กันทุน, ตัดตามเวลา) ในหน่วย R = ระยะ SL ตอนเข้า; เป็นการจำลอง ต้องยืนยันด้วย backtest จริง
4. ใส่กฎลงทฤษฎีรวม: `StrategyBase.BreakevenAtR` / `CutAfterMinutes` / `CutBelowR` / `SignalTag` (robot จัดการต่อ position)
5. ส่งผลช่วงหากฎและช่วงทดสอบเข้า lab (เงินต้น 1,000) → หน้า `summary.html` (ค่าคงที่ `FINAL`, `TEST_FROM`, `VERDICT` ในไฟล์)

ผลรอบแรก (04/10/2026): ทฤษฎีรวมที่ดีสุดในช่วงหากฎ (+1,140, PF 1.27) **ขาดทุนในช่วงทดสอบ** (-233.50, PF 0.77) — ทองร่วงจาก ~5,400 เป็น ~4,190
และแกนหลักเป็นฝั่ง Buy อย่างเดียว ทฤษฎีที่ดูทรงบนแท่ง M1 ทั้งหมดขาดทุนตลอด 6 ปี

### ลูปวิจัยปัจจุบัน — อ่าน `research/JOURNAL.md` ก่อนทำงานวิจัยต่อทุกครั้ง

กติกาการเทรดของผู้ใช้ (04/10/2026, แทนกติกา Fast/Slow ใน prompt): ปิดทุกไม้ก่อนหมดวัน · ไม่มีกลุ่ม Fast · ถือพร้อมกันได้สูงสุด 3 ไม้
และ 3 ไม้ต้องไม่ไปทางเดียวกันทั้งหมด (robot บังคับให้แล้ว; ทฤษฎีกำหนด `MaxPositions`) · เปิดปิดกี่ครั้งต่อวันก็ได้
ผู้ใช้ต้องการให้วนลูป "ตกผลึกทฤษฎี → ทดสอบ → วิเคราะห์ → ตกผลึกใหม่" จนได้กำไรจริง และให้ย่อยการทดสอบให้เร็ว:

- ชั้น 0 (2 วินาที): `research\Phase1\bin\Release\net6.0\Phase1.exe study:<h1..h10|s3|s3x|all> [period:<ชื่อช่วง>] [seed:<n>]` — เพิ่มทฤษฎีใหม่ใน `research\Phase1\Studies.cs`
- ชั้น 2: `tools\backtest.ps1 ... -Commission 8 -FlatTime 2045` — **ข้อมูล tick เป็นค่าเริ่มต้นแล้ว** (ผู้ใช้สั่ง 04/10/2026: ไม่ต้องรันโหมด M1 ซ้ำ)
  tick มีสเปรดจริงในตัว จึงไม่ใส่ `-Spread`; `-Commission 8` = Razor ทอง 3.50 USD/lot/ขา · **รันทีละไม่เกิน ~4 เดือน** (3 เดือน ≈ 1–10 นาที;
  14 เดือนค้างและกินแรม 5.5 GB บนเครื่อง 16 GB) เงินต้นจึงเริ่มใหม่ทุกช่วง · โหมด M1 (`-DataMode m1 -Spread 2.2`) ยังใช้ได้เมื่ออยากได้เส้น equity ต่อเนื่องหลายปี
- **ช่วงข้อมูล (ผู้ใช้กำหนด 04/10/2026): ทดสอบตั้งแต่ 01/2024 ถึงปัจจุบัน** — ไฟล์แท่ง M1 ถูกตัดเหลือ 2023-01.. โดยปี 2023 เป็นข้อมูลฝึกของโมเดลเท่านั้น
  `period:dev` 2023-01..2026-03 (พัฒนากฎ) → `period:test` 2026-04..2026-09 (ตรวจกฎที่ล็อกแล้ว จดทุกครั้งใน JOURNAL) · `all` = 2024-01.. · `y2024`
  · ช่วงผันผวนสูงเดิม: `hv-explore` (2025-01..2026-02), `hv-validate` (2026-03..06), `hv-holdout` (2026-07..09) — S3d เปิดดูครบทุกช่วงแล้ว
  **ข้อมูลที่ยังไม่เคยถูกใช้หากฎมีแค่ตั้งแต่ 10/2026 = สิ่งที่ cBot บันทึกบน Demo**
- **ทุนจริงของผู้ใช้ = 100 USD และเน้น scalp** (แจ้ง 04/10/2026): 0.01 lot = 1 ออนซ์ → ทองขยับ 1 USD = 1% ของบัญชี; ทุกกฎต้องรายงาน drawdown เป็น USD ต่อออนซ์ (= % ของทุน)
- กฎ scalp ที่มีใน cBot = **S3d** = `SCALP_S3D` (`Strategies\ScalpPullback.cs`): โมเดล 5 นาที ≥ 56% + ทิศเดียวกับวัน + เฉพาะลอนดอน + เลี่ยงข่าว, ถือ 5 นาที, SL 5 USD, ไม่มี TP
  รัน: `tools\backtest.ps1 -Strategy SCALP_S3D -Symbol XAUUSD -Period m1 -Balance 100 -Commission 8 -P1 0.56 -P2 5 -P3 5 -P4 1`
  - **สถานะจริง (JOURNAL ลูปที่ 7): ยังไม่มีหลักฐานว่ามี edge** — ปี 2024 ขาดทุน, กำไรกระจุกที่ 01–03/2026, และ 6 เดือนล่าสุดเฉลี่ย ≈ 0 เมื่อฝึกโมเดลด้วยการสุ่ม 6 ชุด
    (ผล +47 ของ run #55/#57 มาจากชุดที่ดีที่สุด) → ใช้บน Demo เพื่อเก็บข้อมูลเท่านั้น ห้ามเสนอให้ใช้เงินจริง
  - **กฎที่ใช้โมเดลต้องรายงานผลข้าม seed เสมอ:** `Phase1.exe predict seed:<n>` แล้ว `study:... seed:<n>` (seed 0 = ชุดที่ export ให้ cBot; ไฟล์ cache ของ seed อื่นลบได้)
  - โมเดลอยู่นอก repo: `Phase1.exe export` → `Documents\BacktestLab\model\XAUUSD-5m.json` (สร้างใหม่ทุกเดือนหลังอัปเดตไฟล์แท่ง M1; ต้องมี `data\news\events.csv` ด้วย)
    seed ของการฝึกผูกกับเดือนปฏิทิน โมเดลจึงไม่เปลี่ยนเมื่อไฟล์ข้อมูลเริ่มที่จุดอื่น
  - feature 35 ตัวมีสองที่ (`research\Phase1\Program.cs` และ `ScalpPullback.cs`) ต้องเหมือนกันทุกประการ — แก้แล้วตรวจด้วย `-Dump -NoSend` + `Phase1.exe study:parity`
  - การจำลองชั้น 0 ดีเกินจริงราว 0.1–0.3 USD ต่อไม้ในช่วงผันผวนสูง (ราคาเข้าจริงแย่กว่าราคาเปิดแท่ง, SL ถูกแตะด้วย bid, SL หลุดราคาได้) และ cTrader โหมด M1
    เติม SL ที่ปลายแท่ง (แย่เกินจริง) → ชั้น 0 ใช้คัดกรองเท่านั้น ตัวตัดสินคือ tick · `SCALP_S3D` v2 (04/10/2026) ถือครบ 5 แท่งพอดีบน tick/ตลาดสด (v1 มักถือ 6)
- **tick volume ของ feed ถูกจำกัดเพดาน ~550 ครั้ง/นาทีตั้งแต่ปี 2025** → เทียบ volume เป็น "เท่าของค่าเฉลี่ย" ไม่ได้ในตลาดปัจจุบัน ให้ใช้อันดับใน 24 ชม. และถือว่าหยาบ
  `study:h11` (`Volume.cs`) = พฤติกรรมราคา/volume บน M1: ไม่มีรูปแบบ volume ใดให้ระยะเกินต้นทุน; กฎ **LX** (สวนแท่งหมดแรงช่วงลอนดอน, `study:lx`) ผ่าน dev แต่ **ไม่ผ่าน test** (JOURNAL ลูปที่ 8)
- กฎ R1 = `CH_SESSION_MOM` (โมเมนตัมข้าม session, เข้า 07:00 และ 10:00 UTC, ปิด 20:45 UTC) กำไรครบ 3 ช่วงเดิม (run #49–#51)
  แต่ drawdown สูง (27–64% ของบัญชี 1,000) ใช้กับทุน 100 ไม่ได้ — หน้า `summary.html` แสดงกฎนี้

### รันสดบนบัญชี Demo + เก็บข้อมูลตลาดจริง (Phase 1B)

ผู้ใช้ต้องการศึกษาพฤติกรรมราคาจริง (ความเร็ว, ข่าว, volume ที่ backtest ไม่มี) ไม่ใช่แค่ทดสอบกฎที่ผ่านแล้ว — **ผู้ใช้เป็นคนกด Start cBot เองใน cTrader เท่านั้น**
(Claude ห้ามเริ่ม cBot / ส่งคำสั่งเทรดผ่าน MCP, CLI `run` หรือคุมหน้าจอ)

- ตั้งค่า: กราฟ XAUUSD **M1**, Strategy code `SCALP_S3D`, P1 0.56, P2 5, P3 5, P4 1, Lots 0.01 · กลุ่ม Live: `Record market` = Yes,
  `Place orders` = Yes (No = บันทึกอย่างเดียว), `Book snapshot every (s)` = 2 · cBot ต้องได้สิทธิ์ Full access และ cTrader ต้องเปิดค้างไว้
- robot ไม่ส่งคำสั่งบนบัญชีเงินจริงเด็ดขาด (`Account.IsLive` → บันทึกอย่างเดียว) และตอนเริ่มจะโหลดประวัติ ≥ 6,000 แท่งให้ทฤษฎี
- `LiveRecorder.cs` เขียนลง `%LOCALAPPDATA%\BacktestLab\live\<symbol>\` (นอก OneDrive): `tick-<วัน>.csv` (ทุก tick), `book-<วัน>.csv` (DOM: ราคา:ปริมาณ ทุกชั้น),
  `min-<เดือน>.csv` (สรุปรายนาที + ข่าว + P(ขึ้น) + สัญญาณ), `trades.csv` (ราคาที่เห็น vs ราคาที่ได้, latency), `runs.csv` — ไฟล์วันเก่าถูกบีบเป็น .gz เอง (~2 MB/วัน)
  ข่าวมาจาก `events.csv` + ปฏิทิน Forex Factory รายสัปดาห์ที่ตัวบันทึกดึงเองเมื่อขึ้นสัปดาห์ใหม่
- ตรวจว่ากำลังบันทึกอยู่: `tools\live-status.ps1` · วิเคราะห์: `Phase1.exe live` (6 ตาราง: tick ถี่/บาง → ราคาไปต่อ, DOM เอียง → ทิศ, สเปรดรายชั่วโมง, หลังข่าว, คำทำนายสด, slippage)
- ทดสอบตัวบันทึกโดยไม่ต้องรอตลาด: `tools\backtest.ps1 ... -Record -NoSend` → โฟลเดอร์ `live-test` (ไม่มี DOM) แล้ว `Phase1.exe live-test`
- ทองแบบ spot ไม่มี volume ซื้อขายจริงจากตลาดกลาง: สิ่งที่มีคือ tick volume (จำนวนครั้งที่ราคาเปลี่ยน) และ DOM ของผู้ให้สภาพคล่องของโบรกเกอร์ — บอกผู้ใช้ตรงๆ เมื่อสรุปผล

### ระบบทำนายรายนาที (ตาม `D:\C2\promt claude.txt` ของผู้ใช้ — ทำเป็น Phase)

เป้าหมายของผู้ใช้: cBot ทอง intraday ที่มี positive expectancy หลังหักต้นทุน โดย "NO EDGE = NO TRADE" และห้ามข้าม Phase
บัญชีเป็น **Pepperstone Razor**. ค่าธรรมเนียมทองที่ Pepperstone ประกาศ ("Razor Gold", ตรวจ 04/10/2026 จาก pepperstone.com/en/go/trade-gold-on-razor):
คอมมิชชั่น **3.50 USD ต่อ lot ต่อขา** (1 lot = 100 ออนซ์ → ไป-กลับ 0.07 USD/ออนซ์, คงที่ ไม่ขึ้นกับราคาทอง) + สเปรดดิบ "from 0.08"
(ค่าเฉลี่ยสเปรดดิบยังไม่ได้วัดจริง; หน้าเก่าปี 2024 ระบุเฉลี่ย 0.18 สมัยไม่มีคอมมิชชั่น) → ต้นทุนรวมโดยประมาณ **~0.20–0.25 USD/ออนซ์ ต่อรอบ**
- ค่า 30 USD ต่อล้านต่อขาที่เคยใช้ (0.25 USD/ออนซ์ ที่ทอง 4,100 เฉพาะคอมมิชชั่น) เป็นอัตรา FX ไม่ใช่ของทอง — **Phase 1 รอบที่รันด้วยต้นทุน 0.30–0.40 จึงมองแย่เกินจริง** ต้องรันใหม่ด้วยต้นทุนคงที่ ~0.22
- backtest ผ่าน CLI: `-Spread 2 -Commission 0` (0.20) ที่ใช้กับทฤษฎีกราฟใกล้เคียงของจริงแล้ว
- ยังไม่ได้ยืนยันกับ Symbol info ของบัญชีผู้ใช้ใน cTrader (CLI/MCP ไม่แสดงค่าคอมมิชชั่น)

- **Phase 1A (เสร็จ 04/10/2026):** `research/Phase1` (C# console) จำลอง "ทำนายทุกนาที → เทียบผลจริง" บนแท่ง M1 2020–09/2026 แบบ walk-forward
  build: `$env:MSBuildEnableWorkloadResolver = "false"; dotnet build -c Release research\Phase1` (SDK 6.0.100 ในเครื่องมีบั๊ก workload และ `dotnet run` ใช้ไม่ได้)
  run: `research\Phase1\bin\Release\net6.0\Phase1.exe` (~3.5 นาที) → `docs\data\phase1-XAUUSD.json` → หน้า `phase1.html`
  ผล: ทายถูก ~51.2% ทุกช่วงเวลา (1/3/5/15/60 นาที), ไม่ชนะ baseline "ตรงข้ามแท่งล่าสุด", ระยะที่ราคาไปตามทางที่ทายเล็กกว่าต้นทุน,
  การเทรดไม่ซ้อนไม้ที่เกณฑ์ 56–62% ส่วนใหญ่ PF < 1 และที่ > 1 กระจุกในปี 2025–2026 → **ยังไม่ผ่านเกณฑ์ไป Phase 2**
- **ชั้นข่าว (เสร็จ 04/10/2026):** `tools\news-fetch.ps1` → `Documents\BacktestLab\data\news\` (`events.csv` เวลาข่าวแรง 9 ประเภท, `macro.csv`, snapshot ปฏิทิน Forex Factory รายสัปดาห์)
  แหล่ง: FRED API (key อยู่ใน `config.json` → `fredApiKey`) + ตารางประชุม FOMC ที่ใส่ในสคริปต์ (FRED release 101 เป็นรายวัน ใช้ไม่ได้)
  Forex Factory ให้ได้แค่สัปดาห์ปัจจุบัน (หน้าย้อนหลัง 403), Investing.com 403 — ห้ามอ้อมระบบกันบอต จึงไม่มี forecast/actual ย้อนหลัง
  Phase1.exe อ่านไฟล์เหล่านี้เองและรันเทียบ "ไม่มีข่าว / มีข่าว" (~8 นาที) ผล: accuracy ไม่ดีขึ้นที่ 1–15 นาที (51.1–51.3%),
  60 นาทีดีขึ้นเล็กน้อย (≥62%: PF 1.13, 1,560 เทรด, กำไร 4 จาก 6 ปี) แต่ยังไม่ผ่านเกณฑ์; ข่าวทำให้กรอบ 5 นาทีใหญ่ขึ้น 2–8 เท่า
  → กฎที่ข้อมูลรองรับ: ไม่เปิดไม้ใหม่ 30 นาทีก่อนข่าว HIGH/EXTREME และ 5–30 นาทีหลังข่าว
- **Phase 1B (พร้อมใช้ 04/10/2026, รอข้อมูลจากตลาด):** `LiveRecorder.cs` บันทึกคำทำนายสด + สเปรดจริง + tick + DOM — ดูหัวข้อ "รันสดบนบัญชี Demo"
- **Phase 2:** เปิดออเดอร์เฉพาะเมื่อ Phase 1 แสดง edge ที่ชนะต้นทุนนอกช่วงฝึกและสม่ำเสมอรายปี

## รัน backtest ผ่าน cTrader CLI

ต้องมี `ctrader-cli` ใน PATH และไฟล์ตั้งค่าต่อเครื่อง `Documents\BacktestLab\cli.json` (ไม่อยู่ใน repo; ครั้งแรกสคริปต์จะถามแล้วสร้างให้):

```json
{ "ctidFile": "<ไฟล์ที่เก็บ cTID>", "pwdFile": "<ไฟล์รหัสผ่าน>", "account": "<เลขบัญชี Demo>" }
```

```powershell
powershell -ExecutionPolicy Bypass -File tools\backtest.ps1 -Strategy EMA_CROSS -Symbol EURUSD -Period h1 `
    -Start "01/01/2025 00:00" -End "30/06/2025 00:00" -P1 9 -P2 21
# ตัวเลือก: -P3 -P4 -Lots -StopLossPips -TakeProfitPips -MinStopPct -MaxStopPct -Spread -Commission -FlatTime (HHmm UTC)
#           -Balance -DataMode (ticks|m1|open; ค่าเริ่มต้น ticks) -Note -Step -TimeoutMinutes -NoSend -SaveLocal -CliReport -Dump -Record
# -Build = sync ซอร์สจาก repo + build .algo ก่อนรัน (ใช้ทุกครั้งที่แก้ไฟล์ใน cbot/)
```

- สำเร็จเมื่อเห็น `Sent to lab: {"ok":true,"runId":N,...,"driveError":null}` (exit code 0; 2 = ไม่ยืนยันว่าส่งถึง lab)
- ผลขึ้น lab/เว็บเองทันทีที่จบ (cBot ส่งเอง ไม่เกี่ยวกับ git) และระหว่างรันสคริปต์ส่งความคืบหน้า % ไป `POST /progress`
  → ตาราง `backtest_jobs` → แถบ "กำลังทดสอบ" บนหน้าแรกและหน้าทฤษฎีกราฟ; รันหลายตัวต่อกันให้ใส่ `-Step "3/10"`
- log เต็มอยู่ที่ `Documents\BacktestLab\logs\` (อยู่ใน OneDrive ของผู้ใช้ — อย่าเปิด `-CliReport` โดยไม่จำเป็น ไฟล์รายงานของ CLI ใหญ่ ~180 MB ต่อรอบ)
- วันที่เป็น `dd/MM/yyyy HH:mm` (UTC) และวัน `-End` ถูกนับรวมทั้งวัน
- ข้อมูล tick (ค่าเริ่มต้น) มีสเปรดจริง ใส่แค่ `-Commission` (ทอง Razor = 8) · **ถ้าใช้ `-DataMode m1` ต้องใส่ `-Spread` ทุกครั้ง** (หน่วย pips): ค่าเริ่มต้นของ CLI คือสเปรด 0 และ commission 0 ผลจะดีเกินจริงมาก
  ทอง (XAUUSD, 1 pip = 0.1) ใช้ `-Spread 2`; EURUSD ใช้ราว `-Spread 1`
- ครั้งแรกของแต่ละ symbol CLI ต้องโหลดข้อมูลย้อนหลัง (ทอง 3 ปีราว 7 นาที) ครั้งต่อไป M1 3 ปีจบในราว 1 นาที
- ห้ามเปิดอ่านไฟล์รหัสผ่าน ให้ส่งเป็น `--pwd-file=<ที่อยู่>` เท่านั้น และใช้บัญชี Demo เท่านั้น
- `ctrader-cli` บางครั้งไม่ปิดตัวเองหลัง backtest จบ สคริปต์จึงปิดให้เมื่อเห็นว่า cBot หยุดแล้ว — อย่าเรียก `ctrader-cli backtest` ตรงๆ โดยไม่มี timeout
- ห้ามใช้คำสั่งเทรดของ CLI (`order ...`, `position ...`, `run`) ใช้ได้แค่ `backtest`, `build`, `metadata`
- ชื่อพารามิเตอร์ของ cBot ดูได้จาก `ctrader-cli metadata <BacktestLab.algo>` (ไฟล์อยู่ที่ `Documents\cAlgo\Sources\Robots\BacktestLab.algo`)

## cTrader MCP

`.mcp.json` ใน repo เชื่อม MCP server ของ cTrader Desktop ที่ `http://127.0.0.1:9876/mcp/`
ใช้ได้เมื่อ: cTrader เปิดและล็อกอินอยู่, Settings → MCP Server → Enable (ไม่เปิด Allow trading), และเปิด Claude ในโฟลเดอร์ repo แล้วอนุมัติใน `/mcp`
ใช้สำหรับอ่านข้อมูล (บัญชี, symbol, ราคา) เท่านั้น ห้ามส่งคำสั่งเทรด — การรัน backtest ให้ใช้ `tools\backtest.ps1`

## วิเคราะห์ผลที่มีอยู่

ค่าเชื่อมต่ออยู่ใน `Documents\BacktestLab\config.json` บนเครื่องผู้ใช้ (`apiUrl`, `ingestKey`) — ห้าม commit หรือพิมพ์ key ลงแชท/ไฟล์ใน repo

```bash
# รายการผลสรุปล่าสุด (มีทฤษฎีและพารามิเตอร์ติดมาด้วย)
curl -s -H "x-ingest-key: $KEY" "$API/runs?limit=200"
# กรอง: ?strategy=EMA_CROSS  ?symbol=EURUSD
# รายละเอียด 1 run
curl -s -H "x-ingest-key: $KEY" "$API/runs/42"
# trades + equity เต็มจาก Drive
curl -s -H "x-ingest-key: $KEY" "$API/runs/42/file"
# ลบ run (แถวใน DB + ไฟล์ Drive ลงถังขยะ) — ถามผู้ใช้ก่อนทุกครั้ง; บนเว็บมีปุ่ม "ลบรอบทดสอบนี้" ในหน้า run
curl -s -X DELETE -H "x-ingest-key: $KEY" "$API/runs/42"
```

ฟิลด์สำคัญของ run: `net_profit`, `win_rate` (%), `profit_factor`, `max_dd_pct` (%), `total_trades`, `params`,
`date_from`/`date_to`, `strategy.{code,version,theory,entry_rules}`
ไฟล์ Drive: `trades[]` = `{id, side, entryTime, exitTime, entryPrice, exitPrice, volume, pips, gross, commissions, swap, net, sl?, tp?, tag?,
bars?: [[unix, o, h, l, c], ...], example?}` — `bars` แนบทุกเทรด (รวมไม่เกิน 50,000 แท่งต่อรอบ; เกินแล้วเทรดเก่าสุดไม่มี) ให้หน้า run กดแถวแล้วเห็นกราฟ,
`equity[]` = `[unixSeconds, equity]`

เวลาวิเคราะห์ให้เทียบ: ผลต่อ symbol/timeframe, ความไวต่อพารามิเตอร์, จำนวนเทรดพอมีนัยสำคัญไหม (< 30 เทรด = เชื่อถือน้อย),
in-sample vs out-of-sample (แนะนำให้ผู้ใช้แยกช่วงวัน), และเตือนเรื่อง overfitting เสมอ

## ข้อห้าม

- ห้ามใส่ `INGEST_KEY`, service role key, Google client secret หรือ refresh token ลงใน repo
- `docs/config.js` มีได้แค่ Supabase URL + anon/publishable key (ค่าสาธารณะ)
- ข้อมูลเข้า DB ได้ทาง Edge Function เท่านั้น (RLS ไม่มี policy insert) — แก้ schema ให้เพิ่มไฟล์ migration ใหม่ใน `supabase/` แล้วบอกผู้ใช้ให้รันใน SQL Editor
- Edge Function แก้แล้วผู้ใช้ต้องวางโค้ดใหม่ใน Dashboard (หรือ `supabase functions deploy lab --no-verify-jwt`)
