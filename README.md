# cBot Backtest Lab

ทดสอบหลายทฤษฎีเทรดด้วย cTrader cBot แล้วบันทึกผลพร้อม "ทฤษฎีที่ใช้ + ค่าที่ตั้ง" ขึ้นคลาวด์ ดูผลได้จากทุกเครื่อง

- **cTrader cBot** (`cbot/`) — หนึ่งไฟล์ต่อหนึ่งทฤษฎี เลือกด้วยพารามิเตอร์ `Strategy code`
- **Supabase** (`supabase/`) — Postgres เก็บผลสรุป + Edge Function `lab` รับข้อมูล
- **Google Drive** — เก็บรายการเทรดและเส้น equity เต็มของทุกรอบ (อัปโหลดผ่านโค้ด ไม่ต้องลงโปรแกรม Drive)
- **GitHub Pages** (`docs/`) — เว็บดูผล ล็อกอินด้วย Google

เริ่มที่ [SETUP.md](SETUP.md) · ทฤษฎีที่มีตอนนี้: `EMA_CROSS`, `RSI_REVERSAL`
