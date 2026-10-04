using System;
using cAlgo.API;

namespace cAlgo.Robots
{
    public class Engulfing : StrategyBase
    {
        public override string Code => "CH_ENGULF";
        public override string Name => "แท่งกลืนกิน (Engulfing)";
        public override string Theory =>
            "ทฤษฎีกราฟ (แท่งเทียนกลับตัว): ที่จุดต่ำ/สูงของกรอบล่าสุด แท่งใหม่ที่เนื้อเทียนกลืนแท่งก่อนหน้าทั้งแท่งในทิศตรงข้าม แสดงว่าอีกฝั่งเข้าคุมตลาดแล้ว";
        public override string[] ParamNames => new[] { "Lookback", "MinBodyMult", "RR" };
        public override string[] EntryRules => new[]
        {
            "แท่งแดงตามด้วยแท่งเขียวที่เนื้อเทียนกลืนแท่งแดง และทำ Low ต่ำสุดของ {Lookback} แท่ง → Buy",
            "แท่งเขียวตามด้วยแท่งแดงที่เนื้อเทียนกลืนแท่งเขียว และทำ High สูงสุดของ {Lookback} แท่ง → Sell",
            "เนื้อเทียนแท่งกลืนต้องยาวอย่างน้อย {MinBodyMult} เท่าของความยาวแท่งเฉลี่ย",
        };
        public override string[] ExitRules => new[] { "SL = หลังปลายไส้ของรูปแบบ", "TP = {RR} เท่าของ SL", "เกิดสัญญาณตรงข้าม → ปิดแล้วกลับฝั่ง" };
        public override double RewardRisk => P[2];

        private double _stop;

        protected override void OnInit() { Defaults(30, 1, 2); }

        public override double? StopDistance(TradeType side) => _stop;

        public override TradeType? Signal()
        {
            var n = (int)P[0];
            if (!HasBars(n + 3)) return null;
            if (Body(1) < P[1] * AvgRange(3, n)) return null;

            var low = Math.Min(L(1), L(2));
            var high = Math.Max(H(1), H(2));

            if (C(2) < O(2) && C(1) > O(1) && C(1) >= O(2) && O(1) <= C(2) && low <= LowestLow(3, n))
            {
                _stop = C(1) - low;
                return TradeType.Buy;
            }
            if (C(2) > O(2) && C(1) < O(1) && C(1) <= O(2) && O(1) >= C(2) && high >= HighestHigh(3, n))
            {
                _stop = high - C(1);
                return TradeType.Sell;
            }
            return null;
        }
    }
}
