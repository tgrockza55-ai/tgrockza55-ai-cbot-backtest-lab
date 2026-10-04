using System;
using cAlgo.API;

namespace cAlgo.Robots
{
    public class PrevDayBreakout : StrategyBase
    {
        public override string Code => "CH_PDAY_BREAK";
        public override string Name => "เบรก High/Low ของวันก่อน";
        public override string Theory =>
            "ทฤษฎีกราฟ (เบรกกรอบ): High และ Low ของวันก่อนเป็นระดับที่ตลาดจับตา เมื่อราคาปิดทะลุออกไปได้ แสดงว่าแรงฝั่งนั้นชนะ มักวิ่งต่อ";
        public override string[] ParamNames => new[] { "StopFrac", "RR" };
        public override string[] EntryRules => new[]
        {
            "แท่งปิดเหนือ High ของวันก่อน (ครั้งแรกของวัน) → Buy",
            "แท่งปิดใต้ Low ของวันก่อน (ครั้งแรกของวัน) → Sell",
        };
        public override string[] ExitRules => new[]
        {
            "SL = {StopFrac} เท่าของกรอบวันก่อน", "TP = {RR} เท่าของ SL", "เกิดสัญญาณตรงข้าม → ปิดแล้วกลับฝั่ง",
        };
        public override double RewardRisk => P[1];

        private DateTime _day;
        private double _dayHigh, _dayLow, _prevHigh, _prevLow, _stop;
        private bool _hasPrev, _bought, _sold;

        protected override void OnInit() { Defaults(0.25, 2); }

        public override double? StopDistance(TradeType side) => _stop;

        public override TradeType? Signal()
        {
            if (!HasBars(3)) return null;
            var day = TradingDay(BarTime);
            if (day != _day)
            {
                if (_day != default(DateTime)) { _prevHigh = _dayHigh; _prevLow = _dayLow; _hasPrev = true; }
                _day = day; _dayHigh = H(1); _dayLow = L(1); _bought = _sold = false;
            }
            else
            {
                _dayHigh = Math.Max(_dayHigh, H(1));
                _dayLow = Math.Min(_dayLow, L(1));
            }
            if (!_hasPrev) return null;

            var range = _prevHigh - _prevLow;
            if (range <= 0) return null;
            _stop = range * P[0];

            if (!_bought && C(1) > _prevHigh && C(2) <= _prevHigh) { _bought = true; return TradeType.Buy; }
            if (!_sold && C(1) < _prevLow && C(2) >= _prevLow) { _sold = true; return TradeType.Sell; }
            return null;
        }
    }
}
