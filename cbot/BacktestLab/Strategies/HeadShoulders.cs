using System;
using cAlgo.API;

namespace cAlgo.Robots
{
    public class HeadShoulders : StrategyBase
    {
        public override string Code => "CH_HEAD_SHOULDERS";
        public override string Name => "Head & Shoulders";
        public override string Theory =>
            "ทฤษฎีกราฟ (ทรงกลับตัว): ยอดสามยอดโดยยอดกลาง (หัว) สูงสุดและไหล่สองข้างสูงใกล้กัน แสดงว่าแรงซื้อทำยอดใหม่ไม่ได้แล้ว เมื่อปิดหลุดเส้นคอถือว่ากลับเป็นขาลง (ทรงกลับหัวใช้กับขาขึ้น)";
        public override string[] ParamNames => new[] { "SwingK", "ShoulderTolPct", "RR" };
        public override string[] EntryRules => new[]
        {
            "จุดสวิง = แท่งที่สูง/ต่ำกว่าแท่งข้างเคียงฝั่งละ {SwingK} แท่ง",
            "ยอดสามยอดล่าสุด: หัวสูงกว่าไหล่ทั้งสอง และไหล่สูงต่างกันไม่เกิน {ShoulderTolPct}% ของราคา แล้วแท่งปิดใต้เส้นคอ → Sell",
            "ก้นสามก้นล่าสุดแบบกลับหัว แล้วแท่งปิดเหนือเส้นคอ → Buy",
            "ต้องเบรกภายใน 120 แท่งหลังไหล่ขวา",
        };
        public override string[] ExitRules => new[] { "SL = หลังไหล่ขวา", "TP = {RR} เท่าของ SL", "เกิดสัญญาณตรงข้าม → ปิดแล้วกลับฝั่ง" };
        public override double RewardRisk => P[2];

        private const int MaxWait = 120;
        private SwingTracker _swings;
        private double _stop;
        private double _topNeck, _topShoulder, _botNeck, _botShoulder;
        private int _topAt = -1, _botAt = -1, _topUsed = -1, _botUsed = -1;

        protected override void OnInit()
        {
            Defaults(10, 0.05, 2);
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
                _stop = _topShoulder - C(1);
                return TradeType.Sell;
            }
            if (_botAt >= 0 && _botAt != _botUsed && now - _botAt <= MaxWait && C(1) > _botNeck && C(2) <= _botNeck)
            {
                _botUsed = _botAt;
                _stop = C(1) - _botShoulder;
                return TradeType.Buy;
            }
            return null;
        }

        private void FindPatterns()
        {
            var tol = C(1) * P[1] / 100;

            var hs = _swings.Highs;
            if (hs.Count >= 3)
            {
                var left = hs[hs.Count - 3];
                var head = hs[hs.Count - 2];
                var right = hs[hs.Count - 1];
                var shoulder = Math.Max(left.Price, right.Price);
                var ok = head.Price > shoulder + tol && Math.Abs(left.Price - right.Price) <= tol;
                _topAt = ok ? right.Index : -1;
                _topNeck = _swings.LowestBetween(left.Index, right.Index);
                _topShoulder = right.Price;
            }

            var ls = _swings.Lows;
            if (ls.Count >= 3)
            {
                var left = ls[ls.Count - 3];
                var head = ls[ls.Count - 2];
                var right = ls[ls.Count - 1];
                var shoulder = Math.Min(left.Price, right.Price);
                var ok = head.Price < shoulder - tol && Math.Abs(left.Price - right.Price) <= tol;
                _botAt = ok ? right.Index : -1;
                _botNeck = _swings.HighestBetween(left.Index, right.Index);
                _botShoulder = right.Price;
            }
        }
    }
}
