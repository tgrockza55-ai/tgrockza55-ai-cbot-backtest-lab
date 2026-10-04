using System;
using cAlgo.API;

namespace cAlgo.Robots
{
    public class AsianRangeBreakout : StrategyBase
    {
        public override string Code => "CH_ASIA_BREAK";
        public override string Name => "เบรกกรอบช่วงเอเชีย";
        public override string Theory =>
            "ทฤษฎีกราฟ (เบรกกรอบ): ช่วงเอเชียทองมักแกว่งในกรอบแคบ เมื่อลอนดอนเปิดมีสภาพคล่องเข้ามา ราคาที่ปิดทะลุกรอบเอเชียมักบอกทิศของวัน";
        public override string[] ParamNames => new[] { "AsiaEndHour", "StopFrac", "RR", "CloseHour" };
        public override string[] EntryRules => new[]
        {
            "กรอบเอเชีย = High/Low ตั้งแต่เปิดวันถึง {AsiaEndHour}:00 UTC",
            "หลัง {AsiaEndHour}:00 แท่งปิดเหนือกรอบ (ครั้งแรกของวัน) → Buy",
            "หลัง {AsiaEndHour}:00 แท่งปิดใต้กรอบ (ครั้งแรกของวัน) → Sell",
        };
        public override string[] ExitRules => new[]
        {
            "SL = {StopFrac} เท่าของกรอบเอเชีย", "TP = {RR} เท่าของ SL", "ถึง {CloseHour}:00 UTC → ปิด", "เกิดสัญญาณตรงข้าม → ปิดแล้วกลับฝั่ง",
        };
        public override double RewardRisk => P[2];

        private DateTime _day;
        private double _high, _low, _stop;
        private bool _hasRange, _bought, _sold;

        protected override void OnInit() { Defaults(7, 0.5, 2, 20); }

        public override double? StopDistance(TradeType side) => _stop;

        private static bool InAsia(DateTime t, double endHour) => t.DayOfWeek == DayOfWeek.Sunday || t.Hour < endHour;

        public override TradeType? Signal()
        {
            if (!HasBars(3)) return null;
            var t = BarTime;
            var day = TradingDay(t);
            if (day != _day) { _day = day; _hasRange = false; _bought = _sold = false; }

            if (InAsia(t, P[0]))
            {
                if (!_hasRange) { _high = H(1); _low = L(1); _hasRange = true; }
                else { _high = Math.Max(_high, H(1)); _low = Math.Min(_low, L(1)); }
                return null;
            }
            if (!_hasRange || t.Hour >= P[3]) return null;

            var range = _high - _low;
            if (range <= 0) return null;
            _stop = range * P[1];

            if (!_bought && C(1) > _high) { _bought = true; return TradeType.Buy; }
            if (!_sold && C(1) < _low) { _sold = true; return TradeType.Sell; }
            return null;
        }

        public override bool ShouldExit(Position position)
        {
            var t = Bot.Server.Time;
            return t.DayOfWeek != DayOfWeek.Sunday && t.Hour >= P[3];
        }
    }
}
