using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    public class RsiReversal : StrategyBase
    {
        public override string Code => "RSI_REVERSAL";
        public override string Name => "RSI Mean Reversion";
        public override string Theory =>
            "กลับสู่ค่าเฉลี่ย (mean reversion): ราคาที่ถูกขายมากเกิน (oversold) หรือซื้อมากเกิน (overbought) มักเด้งกลับ เข้าเมื่อ RSI เริ่มหันกลับออกจากโซน";
        public override string[] ParamNames => new[] { "Period", "Oversold", "Overbought" };
        public override string[] EntryRules => new[]
        {
            "RSI({Period}) ตัดขึ้นเหนือ {Oversold} → Buy",
            "RSI({Period}) ตัดลงใต้ {Overbought} → Sell",
        };
        public override string[] ExitRules => new[] { "ชน SL/TP", "RSI กลับถึง 50 → ปิด", "เกิดสัญญาณตรงข้าม → ปิด" };

        private RelativeStrengthIndex _rsi;

        protected override void OnInit()
        {
            var period = P[0] > 0 ? (int)P[0] : 14;
            _rsi = Bot.Indicators.RelativeStrengthIndex(Bot.Bars.ClosePrices, period);
        }

        private double Oversold => P[1] > 0 ? P[1] : 30;
        private double Overbought => P[2] > 0 ? P[2] : 70;

        public override TradeType? Signal()
        {
            if (CrossedAbove(_rsi.Result, Oversold)) return TradeType.Buy;
            if (CrossedBelow(_rsi.Result, Overbought)) return TradeType.Sell;
            return null;
        }

        public override bool ShouldExit(Position position)
        {
            var r = _rsi.Result.Last(1);
            return position.TradeType == TradeType.Buy ? r >= 50 : r <= 50;
        }
    }
}
