using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    public class EmaCross : StrategyBase
    {
        public override string Code => "EMA_CROSS";
        public override string Name => "EMA Crossover";
        public override string Theory =>
            "ตามเทรนด์ (trend following): เมื่อค่าเฉลี่ยระยะสั้นตัดค่าเฉลี่ยระยะยาว แสดงว่าโมเมนตัมเปลี่ยนทิศ จึงเข้าตามทิศใหม่";
        public override string[] ParamNames => new[] { "Fast", "Slow" };
        public override string[] EntryRules => new[]
        {
            "EMA {Fast} ตัดขึ้นเหนือ EMA {Slow} (แท่งที่ปิดแล้ว) → Buy",
            "EMA {Fast} ตัดลงใต้ EMA {Slow} → Sell",
        };
        public override string[] ExitRules => new[] { "ชน SL/TP", "เกิดสัญญาณตรงข้าม → ปิดแล้วกลับฝั่ง" };

        private ExponentialMovingAverage _fast, _slow;

        protected override void OnInit()
        {
            _fast = Bot.Indicators.ExponentialMovingAverage(Bot.Bars.ClosePrices, (int)P[0]);
            _slow = Bot.Indicators.ExponentialMovingAverage(Bot.Bars.ClosePrices, (int)P[1]);
        }

        public override TradeType? Signal()
        {
            if (CrossedAbove(_fast.Result, _slow.Result)) return TradeType.Buy;
            if (CrossedBelow(_fast.Result, _slow.Result)) return TradeType.Sell;
            return null;
        }
    }
}
