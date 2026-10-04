// ทฤษฎีรวม: เอาเฉพาะทฤษฎีกราฟที่ผ่านการวิจัยบนช่วงหากฎ (01/2020–02/2026) มาใช้ร่วมกัน ถือได้ทีละ 1 ออเดอร์
//   ขา B (แกนหลัก)  เบรก High/Low ของวันก่อน (CH_PDAY_BREAK) — ปกติใช้เฉพาะฝั่ง Buy, TP ไกล, ตัดทิ้งเมื่อครบเวลาแล้วยังติดลบ
//   ขา A (เสริม)    เบรกกรอบช่วงเอเชีย (CH_ASIA_BREAK)        — เลือกวันในสัปดาห์ได้, TP 3R, เลื่อน SL มากันทุนเมื่อกำไรถึง 0.5R
// จุดเข้าของทฤษฎีย่อยเหมือนตอนรันเดี่ยวทุกอย่าง ทฤษฎีรวมคัดว่าจะรับสัญญาณไหน และกำหนดวิธีออก
// ค่าการออกมาจาก tools\exit-study.ps1 บนช่วงหากฎเท่านั้น

using System;
using System.Globalization;
using cAlgo.API;

namespace cAlgo.Robots
{
    public class ChartCombo : StrategyBase
    {
        public override string Code => "CH_COMBO";
        public override string Name => "ทฤษฎีรวม: เบรก High วันก่อน + กรอบเอเชีย";
        public override string Theory =>
            "ทฤษฎีรวม: จากทฤษฎีกราฟ 10 ตัวบนทอง 6 ปี มีแค่การเบรก High ของวันก่อน (ฝั่ง Buy) ที่ได้ผลกับทุกแผนการออกที่ลอง จึงใช้เป็นแกนหลัก และปล่อยกำไรให้วิ่งไกลแต่ตัดทิ้งเร็วเมื่อครบเวลาแล้วยังติดลบ ส่วนเบรกกรอบช่วงเอเชียได้ผลเฉพาะบางวัน จึงเป็นขาเสริมที่เลือกวันได้ ทฤษฎีที่ดูทรงบนแท่ง M1 ขาดทุนทุกตัวจึงไม่นำมาใช้";
        public override string[] ParamNames => new[] { "AsiaDays", "PdayMode", "PdayTP", "PdayCutMin" };
        public override string[] EntryRules => new[]
        {
            "ขา B (PdayMode {PdayMode}: 1 = ไม่ใช้, 2 = เฉพาะ Buy, 3 = ทั้งสองฝั่ง): แท่ง M1 ปิดเหนือ High ของวันก่อน → Buy, ปิดใต้ Low ของวันก่อน → Sell (ฝั่งละครั้งต่อวัน)",
            "ขา A (วันที่อยู่ใน {AsiaDays}; 1 = จันทร์ … 5 = ศุกร์, 9 = ไม่ใช้): หลัง 07:00 UTC แท่ง M1 ปิดเหนือกรอบช่วงเอเชีย → Buy, ปิดใต้กรอบ → Sell (ฝั่งละครั้งต่อวัน)",
            "ถือได้ทีละ 1 ออเดอร์: ถ้ามีออเดอร์ฝั่งเดียวกันอยู่แล้ว สัญญาณใหม่ถูกข้าม",
        };
        public override string[] ExitRules => new[]
        {
            "ขา B: SL = 0.25 เท่าของกรอบวันก่อน, TP = {PdayTP} เท่าของ SL, ครบ {PdayCutMin} นาทีแล้วยังติดลบ → ปิด",
            "ขา A: SL = 0.5 เท่าของกรอบเอเชีย, TP = 3 เท่าของ SL, กำไรถึง 0.5 เท่าของ SL → เลื่อน SL มาที่ราคาเข้า, ถึง 20:00 UTC → ปิด",
            "เกิดสัญญาณตรงข้าม → ปิดแล้วกลับฝั่ง",
            "ทุกออเดอร์ปิดก่อนหมดวัน (กำหนดที่ robot: Flat at)",
        };

        private const string TagAsia = "ASIA", TagPday = "PDAY";
        private const double AsiaTp = 3, AsiaBreakevenAt = 0.5;

        private AsianRangeBreakout _asia;
        private PrevDayBreakout _pday;
        private bool[] _asiaDay;
        private string _tag;
        private double _stop;

        public override double RewardRisk => _tag == TagAsia ? AsiaTp : P[2];
        public override double BreakevenAtR => _tag == TagAsia ? AsiaBreakevenAt : 0;
        public override int CutAfterMinutes => _tag == TagPday && P[3] < 9000 ? (int)P[3] : 0;
        public override double CutBelowR => 0;
        public override string SignalTag => _tag;

        protected override void OnInit()
        {
            Defaults(9, 2, 3, 240);

            _asiaDay = new bool[7];
            foreach (var ch in ((long)P[0]).ToString(CultureInfo.InvariantCulture))
                if (ch >= '1' && ch <= '5') _asiaDay[ch - '0'] = true;   // DayOfWeek: จันทร์ = 1

            _asia = new AsianRangeBreakout();
            _asia.Attach(Bot, new[] { 7.0, 0.5, AsiaTp, 20.0 });
            _pday = new PrevDayBreakout();
            _pday.Attach(Bot, new[] { 0.25, P[2], 0.0, 0.0 });
        }

        public override double? StopDistance(TradeType side) => _stop;

        public override TradeType? Signal()
        {
            // เรียกทั้งสองขาทุกแท่ง เพื่อให้สถานะภายใน (กรอบ, ใช้สัญญาณไปแล้วหรือยัง) เดินเหมือนตอนรันเดี่ยว
            var a = _asia.Signal();
            var b = _pday.Signal();

            var mode = (int)P[1];
            if (b.HasValue && (mode == 3 || (mode == 2 && b.Value == TradeType.Buy)))
            {
                _tag = TagPday;
                _stop = _pday.StopDistance(b.Value) ?? 0;
                return b;
            }
            if (a.HasValue && _asiaDay[(int)TradingDay(BarTime).DayOfWeek])
            {
                _tag = TagAsia;
                _stop = _asia.StopDistance(a.Value) ?? 0;
                return a;
            }
            return null;
        }

        public override bool ShouldExit(Position position) =>
            position.Comment == TagAsia && _asia.ShouldExit(position);
    }
}
