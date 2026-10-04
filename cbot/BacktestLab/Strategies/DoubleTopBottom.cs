using System;
using cAlgo.API;

namespace cAlgo.Robots
{
    public class DoubleTopBottom : StrategyBase
    {
        public override string Code => "CH_DOUBLE_TB";
        public override string Name => "Double top / Double bottom";
        public override string Theory =>
            "ทฤษฎีกราฟ (ทรงกลับตัว): ราคาขึ้นไปทดสอบยอดเดิมสองครั้งแล้วไม่ผ่าน (หรือลงทดสอบก้นเดิมสองครั้งแล้วไม่หลุด) เมื่อปิดทะลุเส้นคอ (neckline) ถือว่าทรงสมบูรณ์และเทรนด์กลับตัว";
        public override string[] ParamNames => new[] { "SwingK", "TolPct", "RR", "MinHeightPct" };
        public override string[] EntryRules => new[]
        {
            "จุดสวิง = แท่งที่สูง/ต่ำกว่าแท่งข้างเคียงฝั่งละ {SwingK} แท่ง",
            "ยอดสองยอดล่าสุดสูงต่างกันไม่เกิน {TolPct}% ของราคา แล้วแท่งปิดใต้เส้นคอ → Sell",
            "ก้นสองก้นล่าสุดต่ำต่างกันไม่เกิน {TolPct}% ของราคา แล้วแท่งปิดเหนือเส้นคอ → Buy",
            "ความสูงของทรงต้องไม่น้อยกว่า {MinHeightPct}% ของราคา และต้องเบรกภายใน 120 แท่งหลังยอด/ก้นที่สอง",
        };
        public override string[] ExitRules => new[] { "SL = หลังยอด/ก้นของทรง", "TP = {RR} เท่าของ SL", "เกิดสัญญาณตรงข้าม → ปิดแล้วกลับฝั่ง" };
        public override double RewardRisk => P[2];

        private const int MaxWait = 120;
        private SwingTracker _swings;
        private double _stop;
        private double _topNeck, _topPeak, _botNeck, _botTrough;
        private int _topAt = -1, _botAt = -1, _topUsed = -1, _botUsed = -1;

        protected override void OnInit()
        {
            Defaults(10, 0.03, 2, 0.1);
            _swings = new SwingTracker(Bot.Bars, (int)P[0]);
        }

        public override double? StopDistance(TradeType side) => _stop;

        public override TradeType? Signal()
        {
            if (_swings.Update()) FindPatterns();

            var now = _swings.LastClosedIndex;

            if (_topAt >= 0 && _topAt != _topUsed && now - _topAt <= MaxWait && C(1) < _topNeck && C(2) >= _topNeck)
            {
                _topUsed = _topAt;
                _stop = _topPeak - C(1);
                return TradeType.Sell;
            }
            if (_botAt >= 0 && _botAt != _botUsed && now - _botAt <= MaxWait && C(1) > _botNeck && C(2) <= _botNeck)
            {
                _botUsed = _botAt;
                _stop = C(1) - _botTrough;
                return TradeType.Buy;
            }
            return null;
        }

        private void FindPatterns()
        {
            var price = C(1);
            var tol = price * P[1] / 100;
            var minHeight = price * P[3] / 100;

            var hs = _swings.Highs;
            if (hs.Count >= 2)
            {
                var a = hs[hs.Count - 2];
                var b = hs[hs.Count - 1];
                var peak = Math.Max(a.Price, b.Price);
                var neck = _swings.LowestBetween(a.Index, b.Index);
                _topAt = Math.Abs(a.Price - b.Price) <= tol && peak - neck >= minHeight ? b.Index : -1;
                _topNeck = neck; _topPeak = peak;
            }

            var ls = _swings.Lows;
            if (ls.Count >= 2)
            {
                var a = ls[ls.Count - 2];
                var b = ls[ls.Count - 1];
                var trough = Math.Min(a.Price, b.Price);
                var neck = _swings.HighestBetween(a.Index, b.Index);
                _botAt = Math.Abs(a.Price - b.Price) <= tol && neck - trough >= minHeight ? b.Index : -1;
                _botNeck = neck; _botTrough = trough;
            }
        }
    }
}
