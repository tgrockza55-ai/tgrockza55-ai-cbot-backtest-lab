using cAlgo.API;

namespace cAlgo.Robots
{
    public class NBarBreakout : StrategyBase
    {
        public override string Code => "CH_NBAR_BREAK";
        public override string Name => "เบรก High/Low ย้อนหลัง N แท่ง";
        public override string Theory =>
            "ทฤษฎีกราฟ (เบรกกรอบ): ราคาที่ปิดทะลุจุดสูงสุด/ต่ำสุดของ N แท่งล่าสุด คือการหลุดออกจากกรอบสะสม มักเป็นจุดเริ่มของการวิ่งรอบใหม่";
        public override string[] ParamNames => new[] { "Lookback", "StopFrac", "RR" };
        public override string[] EntryRules => new[]
        {
            "แท่งปิดเหนือ High สูงสุดของ {Lookback} แท่งก่อนหน้า → Buy",
            "แท่งปิดใต้ Low ต่ำสุดของ {Lookback} แท่งก่อนหน้า → Sell",
        };
        public override string[] ExitRules => new[]
        {
            "SL = {StopFrac} เท่าของความสูงกรอบ", "TP = {RR} เท่าของ SL", "เกิดสัญญาณตรงข้าม → ปิดแล้วกลับฝั่ง",
        };
        public override double RewardRisk => P[2];

        private double _stop;

        protected override void OnInit() { Defaults(120, 0.5, 2); }

        public override double? StopDistance(TradeType side) => _stop;

        public override TradeType? Signal()
        {
            var n = (int)P[0];
            if (!HasBars(n + 2)) return null;

            var hi = HighestHigh(2, n);
            var lo = LowestLow(2, n);
            _stop = (hi - lo) * P[1];

            if (C(1) > hi) return TradeType.Buy;
            if (C(1) < lo) return TradeType.Sell;
            return null;
        }
    }
}
