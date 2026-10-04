using System;
using cAlgo.API;

namespace cAlgo.Robots
{
    public class PinBar : StrategyBase
    {
        public override string Code => "CH_PINBAR";
        public override string Name => "แท่งไส้ยาว (Pin bar)";
        public override string Theory =>
            "ทฤษฎีกราฟ (แท่งเทียนกลับตัว): ไส้ยาวที่จุดต่ำ/สูงของกรอบล่าสุด คือราคาถูกดันไปแล้วโดนปฏิเสธกลับมาในแท่งเดียว แสดงว่ามีแรงรับ/แรงขายรออยู่";
        public override string[] ParamNames => new[] { "Lookback", "WickRatio", "RR" };
        public override string[] EntryRules => new[]
        {
            "ไส้ล่างยาวอย่างน้อย {WickRatio} เท่าของเนื้อเทียน ไส้บนสั้น และทำ Low ต่ำสุดของ {Lookback} แท่ง → Buy",
            "ไส้บนยาวอย่างน้อย {WickRatio} เท่าของเนื้อเทียน ไส้ล่างสั้น และทำ High สูงสุดของ {Lookback} แท่ง → Sell",
            "แท่งต้องยาวอย่างน้อย 1.5 เท่าของความยาวแท่งเฉลี่ย",
        };
        public override string[] ExitRules => new[] { "SL = หลังปลายไส้", "TP = {RR} เท่าของ SL", "เกิดสัญญาณตรงข้าม → ปิดแล้วกลับฝั่ง" };
        public override double RewardRisk => P[2];

        private double _stop;

        protected override void OnInit() { Defaults(30, 2.5, 2); }

        public override double? StopDistance(TradeType side) => _stop;

        public override TradeType? Signal()
        {
            var n = (int)P[0];
            if (!HasBars(n + 2)) return null;

            var range = Range(1);
            if (range <= 0 || range < 1.5 * AvgRange(2, n)) return null;

            var body = Math.Max(Body(1), range * 0.05);
            var lower = Math.Min(O(1), C(1)) - L(1);
            var upper = H(1) - Math.Max(O(1), C(1));

            if (lower >= P[1] * body && upper <= 0.25 * range && L(1) <= LowestLow(2, n))
            {
                _stop = C(1) - L(1);
                return TradeType.Buy;
            }
            if (upper >= P[1] * body && lower <= 0.25 * range && H(1) >= HighestHigh(2, n))
            {
                _stop = H(1) - C(1);
                return TradeType.Sell;
            }
            return null;
        }
    }
}
