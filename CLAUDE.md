# cBot Backtest Lab — คู่มือสำหรับ Claude

โปรเจกต์ส่วนตัว: cBot ของ cTrader รัน backtest หลายทฤษฎี ส่งผลขึ้น Supabase (สรุป) + Google Drive (trades/equity เต็ม)
และแสดงบนเว็บ GitHub Pages ผู้ใช้สื่อสารภาษาไทย ตอบเป็นภาษาไทย

## โครงสร้าง

```
cbot/BacktestLab/            ซอร์ส cBot (C#, cTrader Automate .NET 6)
  BacktestLab.cs             Robot หลัก: พารามิเตอร์, เปิด/ปิดออเดอร์, สร้างรายงาน, EquityRecorder
  StrategyBase.cs            ฐานของทุกทฤษฎี + ค้นหาคลาสด้วย reflection จาก Code
  Strategies/*.cs            หนึ่งไฟล์ = หนึ่งทฤษฎี
  Reporter.cs                POST ไป /ingest, เก็บ pending เมื่อส่งไม่ได้
supabase/schema.sql          ตาราง strategies, backtest_runs, view strategy_stats + RLS
supabase/functions/lab/      Edge Function: /ingest, /runs, /runs/:id, /runs/:id/file, /health
docs/                        เว็บ (GitHub Pages): index, run, strategies + app.js, config.js
tools/sync-cbot.ps1          ก๊อปซอร์สไป Documents\cAlgo\Sources\Robots\BacktestLab\BacktestLab + สร้าง config
tools/backtest.ps1           รัน backtest 1 รอบผ่าน cTrader CLI (เพิ่ม -Build เพื่อ sync + build ก่อน)
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
7. แนะนำผู้ใช้ให้ `git commit` + `git push` เพื่อใช้บนเครื่องอื่น (ถามผู้ใช้ก่อนทุกครั้ง)
8. cAlgo.API มีชนิดชื่อซ้ำกับ .NET (`File`, `HttpMethod`) — ถ้าใช้ `System.IO` / `System.Net.Http` ให้ใส่ `using X = ...;` กำกับ
9. จัดรูปแบบวันที่/ตัวเลขเป็นข้อความต้องใส่ `CultureInfo.InvariantCulture` เสมอ (เครื่องภาษาไทยจะได้ปี พ.ศ.)

## รัน backtest ผ่าน cTrader CLI

ต้องมี `ctrader-cli` ใน PATH และไฟล์ตั้งค่าต่อเครื่อง `Documents\BacktestLab\cli.json` (ไม่อยู่ใน repo; ครั้งแรกสคริปต์จะถามแล้วสร้างให้):

```json
{ "ctidFile": "<ไฟล์ที่เก็บ cTID>", "pwdFile": "<ไฟล์รหัสผ่าน>", "account": "<เลขบัญชี Demo>" }
```

```powershell
powershell -ExecutionPolicy Bypass -File tools\backtest.ps1 -Strategy EMA_CROSS -Symbol EURUSD -Period h1 `
    -Start "01/01/2025 00:00" -End "30/06/2025 00:00" -P1 9 -P2 21
# ตัวเลือก: -P3 -P4 -Lots -StopLossPips -TakeProfitPips -Balance -DataMode (ticks|m1|open) -Note -TimeoutMinutes
# -Build = sync ซอร์สจาก repo + build .algo ก่อนรัน (ใช้ทุกครั้งที่แก้ไฟล์ใน cbot/)
```

- สำเร็จเมื่อเห็น `Sent to lab: {"ok":true,"runId":N,...,"driveError":null}` (exit code 0; 2 = ไม่ยืนยันว่าส่งถึง lab)
- log เต็มอยู่ที่ `Documents\BacktestLab\logs\`
- วันที่เป็น `dd/MM/yyyy HH:mm` (UTC) และวัน `-End` ถูกนับรวมทั้งวัน
- ค่าเริ่มต้นของ CLI คือ commission = 0 ผลจึงดีกว่าความจริงเล็กน้อย
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
```

ฟิลด์สำคัญของ run: `net_profit`, `win_rate` (%), `profit_factor`, `max_dd_pct` (%), `total_trades`, `params`,
`date_from`/`date_to`, `strategy.{code,version,theory,entry_rules}`
ไฟล์ Drive: `trades[]` = `{id, side, entryTime, exitTime, entryPrice, exitPrice, volume, pips, gross, commissions, swap, net}`,
`equity[]` = `[unixSeconds, equity]`

เวลาวิเคราะห์ให้เทียบ: ผลต่อ symbol/timeframe, ความไวต่อพารามิเตอร์, จำนวนเทรดพอมีนัยสำคัญไหม (< 30 เทรด = เชื่อถือน้อย),
in-sample vs out-of-sample (แนะนำให้ผู้ใช้แยกช่วงวัน), และเตือนเรื่อง overfitting เสมอ

## ข้อห้าม

- ห้ามใส่ `INGEST_KEY`, service role key, Google client secret หรือ refresh token ลงใน repo
- `docs/config.js` มีได้แค่ Supabase URL + anon/publishable key (ค่าสาธารณะ)
- ข้อมูลเข้า DB ได้ทาง Edge Function เท่านั้น (RLS ไม่มี policy insert) — แก้ schema ให้เพิ่มไฟล์ migration ใหม่ใน `supabase/` แล้วบอกผู้ใช้ให้รันใน SQL Editor
- Edge Function แก้แล้วผู้ใช้ต้องวางโค้ดใหม่ใน Dashboard (หรือ `supabase functions deploy lab --no-verify-jwt`)
