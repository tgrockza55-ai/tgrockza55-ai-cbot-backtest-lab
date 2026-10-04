# ตั้งค่า cBot Backtest Lab ทีละขั้น

ทำครั้งเดียว ใช้เวลาประมาณ 45–60 นาที หลังจากนี้เครื่องใหม่ใช้แค่ขั้น 8 (ประมาณ 5 นาที)

```
cTrader cBot ──POST──▶ Supabase Edge Function "lab" ──▶ Postgres (สรุปผล + ทฤษฎี)
                                   └──────────────────▶ Google Drive /BacktestLab/runs/<id>.json.gz (trades + equity)
GitHub Pages (เว็บ) ──อ่าน──▶ Postgres (RLS) + ไฟล์ Drive ผ่าน "lab"     ล็อกอินด้วย Google
```

สิ่งที่ต้องมี: บัญชี Google, GitHub, Supabase (ฟรีทั้งหมด), cTrader Desktop, Git บน Windows

---

## 1. GitHub — เอาโค้ดขึ้นและเปิดเว็บ

1. สร้าง repo ใหม่ เช่น `cbot-backtest-lab` แล้ว push ทั้งโฟลเดอร์นี้ขึ้นไป
2. Settings → Pages → Source: **Deploy from a branch** → Branch `main`, โฟลเดอร์ `/docs` → Save
3. จดที่อยู่เว็บ: `https://<username>.github.io/cbot-backtest-lab/`

> GitHub Pages ฟรีเฉพาะ repo **public** — โค้ดเว็บเปิดเผยได้ ไม่มีความลับ (ผลทดสอบป้องกันด้วยล็อกอิน)
> ถ้าไม่อยากให้คนเห็นกลยุทธ์ ให้แยกโฟลเดอร์ `cbot/` ไปไว้ใน repo private อีกอัน

## 2. Supabase — ฐานข้อมูล

1. [supabase.com](https://supabase.com) → New project (Region: Southeast Asia (Singapore)) ตั้งรหัส DB แล้วเก็บไว้
2. SQL Editor → New query → วางเนื้อหา `supabase/schema.sql` ทั้งไฟล์ → Run
3. Project Settings → API: คัดลอก **Project URL** และ **anon / publishable key** ไปใส่ใน `docs/config.js` แล้ว push

## 3. Google Cloud — OAuth client ตัวเดียวใช้ทั้งล็อกอินและ Drive

1. [console.cloud.google.com](https://console.cloud.google.com) → สร้างโปรเจกต์ใหม่ เช่น `backtest-lab`
2. APIs & Services → Library → ค้น **Google Drive API** → Enable
3. Google Auth Platform (OAuth consent screen) → External → ใส่ชื่อแอป + อีเมล
   - Data access / Scopes: เพิ่ม `.../auth/drive.file`, `openid`, `email`, `profile`
   - Audience → **Publish app** (เปลี่ยนเป็น In production)
     สำคัญ: ถ้าค้างไว้ที่ Testing, refresh token ของ Drive จะหมดอายุใน 7 วัน
     (`drive.file` เป็น scope ไม่อ่อนไหว จึงไม่ต้องรอ Google ตรวจแอป)
4. Clients → Create client → **Web application**
   - Authorized JavaScript origins: `https://<username>.github.io`
   - Authorized redirect URIs (ใส่ 2 อัน):
     - `https://<project-ref>.supabase.co/auth/v1/callback`
     - `https://developers.google.com/oauthplayground`
5. จด **Client ID** และ **Client secret**

## 4. Supabase Auth — เปิด Google login

1. Authentication → Sign In / Providers → Google → Enable → ใส่ Client ID / Client secret → Save
2. Authentication → URL Configuration
   - Site URL: `https://<username>.github.io/cbot-backtest-lab/`
   - Redirect URLs: เพิ่มที่อยู่เดียวกัน (และ `http://localhost:5500/**` ถ้าจะทดสอบในเครื่อง)

## 5. ล็อกอินครั้งแรก → ได้ OWNER_ID

1. เปิดเว็บ → เข้าสู่ระบบด้วย Google
2. หน้าแรกจะแสดง `user id ของคุณคือ xxxxxxxx-...` → คัดลอกเก็บไว้ (ใช้เป็น `OWNER_ID`)

## 6. Refresh token ของ Google Drive

ให้ระบบอัปโหลดไฟล์ขึ้น Drive ของเราได้โดยไม่ต้องลงโปรแกรม Drive ในเครื่องไหนเลย

1. เปิด [OAuth 2.0 Playground](https://developers.google.com/oauthplayground)
2. รูปเฟือง (ขวาบน) → ติ๊ก **Use your own OAuth credentials** → ใส่ Client ID / secret จากขั้น 3
3. Step 1: ช่อง "Input your own scopes" พิมพ์ `https://www.googleapis.com/auth/drive.file` → Authorize APIs → เลือกบัญชี Google ของเรา → Allow
4. Step 2: กด **Exchange authorization code for tokens** → คัดลอก **Refresh token**

`drive.file` = แอปเห็นเฉพาะไฟล์ที่แอปสร้างเอง ไม่แตะไฟล์อื่นใน Drive

## 7. Edge Function "lab"

1. Supabase → Edge Functions → **Deploy a new function** → Via Editor
2. ชื่อ `lab` → ลบโค้ดตัวอย่าง → วางเนื้อหา `supabase/functions/lab/index.ts` ทั้งไฟล์ → Deploy
3. เปิดฟังก์ชัน `lab` → Details → ปิด **Verify JWT** (Enforce JWT verification) → Save
   (ฟังก์ชันตรวจสิทธิ์เองด้วย ingest key หรือ Google login ของเจ้าของ)
4. Edge Functions → Secrets → เพิ่มทีละตัว:

| ชื่อ | ค่า |
| --- | --- |
| `INGEST_KEY` | รหัสสุ่มยาวๆ — PowerShell: `[guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')` |
| `OWNER_ID` | user id จากขั้น 5 |
| `GOOGLE_CLIENT_ID` | จากขั้น 3 |
| `GOOGLE_CLIENT_SECRET` | จากขั้น 3 |
| `GOOGLE_REFRESH_TOKEN` | จากขั้น 6 |

5. ทดสอบ: เปิด `https://<project-ref>.supabase.co/functions/v1/lab/health` ต้องเห็น `{"ok":true,"missing":[]}`

**เก็บ `INGEST_KEY` ไว้ในตัวจัดการรหัสผ่าน** — เครื่องใหม่ทุกเครื่องต้องใช้ค่านี้

## 8. cTrader — ทำทุกครั้งที่ตั้งเครื่องใหม่

1. ติดตั้ง cTrader Desktop และ Git → `git clone https://github.com/<username>/cbot-backtest-lab.git`
2. cTrader → Algo → cBots → **New** → ตั้งชื่อ `BacktestLab` ตรงตัว → Save (ให้ cTrader สร้างโปรเจกต์ไว้ก่อน)
3. ในโฟลเดอร์ repo รัน:
   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\sync-cbot.ps1
   ```
   ครั้งแรกจะถาม API URL (`https://<project-ref>.supabase.co/functions/v1/lab`) และ `INGEST_KEY`
   แล้วสร้าง `Documents\BacktestLab\config.json` + เช็คว่าเชื่อมต่อได้
4. cTrader → เลือก BacktestLab → **Build** (ครั้งแรก cTrader จะถามสิทธิ์ Full Access → อนุญาต)
5. Backtesting → เลือก symbol/timeframe/ช่วงวัน → Strategy code `EMA_CROSS` → Start
6. จบแล้วดู Log: `Sent to lab: {"ok":true,"runId":1,...}` → เปิดเว็บ ผลขึ้นแล้ว

ทุกครั้งที่ `git pull` โค้ดใหม่ ให้รัน `tools\sync-cbot.ps1` แล้ว Build ใหม่

## 9. เพิ่มทฤษฎีใหม่

1. สร้างไฟล์ใน `cbot/BacktestLab/Strategies/` (ก๊อปจาก `EmaCross.cs`) เปลี่ยน `Code`, `Name`, `Theory`, `ParamNames`, กฎ และ `Signal()`
2. รัน `tools\sync-cbot.ps1` → Build → backtest โดยใส่ Strategy code ใหม่
3. ทฤษฎีใหม่ขึ้นในหน้า "คลังทฤษฎี" อัตโนมัติ → `git commit` + `git push` เพื่อให้เครื่องอื่นได้ด้วย
4. แก้ตรรกะของทฤษฎีเดิมเมื่อไหร่ ให้เพิ่ม `Version` (+1) เพื่อไม่ให้ผลเก่ากับใหม่ปนกัน

หรือเปิด Claude ในโฟลเดอร์ repo แล้วบอกไอเดีย — `CLAUDE.md` บอกวิธีทำให้ครบ

## แก้ปัญหา

| อาการ | สาเหตุ / วิธีแก้ |
| --- | --- |
| Log: `Missing config` | ยังไม่มี `Documents\BacktestLab\config.json` → รัน `tools\sync-cbot.ps1` |
| Log: `Lab responded 401` | `INGEST_KEY` ในเครื่องไม่ตรงกับ secret |
| Log: `Send failed` | เน็ตหลุด / Supabase ถูก pause → ไฟล์ถูกเก็บใน `Documents\BacktestLab\pending` และส่งซ้ำตอนรันรอบหน้า |
| เว็บขึ้น "ไม่มีไฟล์รายละเอียดใน Drive: google token: invalid_grant" | refresh token หมดอายุ (แอปยังเป็น Testing) → Publish app แล้วทำขั้น 6 ใหม่ |
| ล็อกอินแล้วเด้งกลับหน้าแรกของ Supabase | Site URL / Redirect URLs ในขั้น 4 ไม่ตรงกับที่อยู่เว็บ |
| เว็บว่างทั้งที่ส่งผลแล้ว | `OWNER_ID` ไม่ตรงกับ user id ที่ล็อกอิน |
| Supabase ไม่ตอบหลังไม่ได้ใช้นาน | โปรเจกต์ฟรีถูก pause เมื่อไม่มีการใช้งานราว 1 สัปดาห์ → เข้า Dashboard กด Restore |
