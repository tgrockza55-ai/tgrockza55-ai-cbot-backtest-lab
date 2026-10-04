using System;
using cAlgo.API;

namespace cAlgo.Robots
{
    public class SrBounce : StrategyBase
    {
        public override string Code => "CH_SR_BOUNCE";
        public override string Name => "เด้งจากแนวรับ/แนวต้าน";
        public override string Theory =>
            "ทฤษฎีกราฟ (แนวรับแนวต้าน): จุดสวิงก่อนหน้าคือระดับที่เคยมีแรงซื้อ/ขายชนะ เมื่อราคากลับมาแตะแล้วถูกปฏิเสธ (ไส้แทงแล้วปิดกลับ) ระดับนั้นยังใช้ได้ ราคามักเด้งออก";
        public override string[] ParamNames => new[] { "SwingK", "TolPct", "RR" };
        public override string[] EntryRules => new[]
        {
            "แนวรับ = จุดสวิงต่ำล่าสุด, แนวต้าน = จุดสวิงสูงล่าสุด (สวิงฝั่งละ {SwingK} แท่ง)",
            "ราคากลับมาแตะแนวรับ (ห่างไม่เกิน {TolPct}% ของราคา) แล้วปิดเป็นแท่งเขียวเหนือแนว → Buy",
            "ราคากลับมาแตะแนวต้าน แล้วปิดเป็นแท่งแดงใต้แนว → Sell",
            "แต่ละแนวใช้ครั้งเดียว และเลิกใช้เมื่อราคาปิดทะลุแนว",
        };
        public override string[] ExitRules => new[] { "SL = หลังแนว (เลยไส้ไป {TolPct}%)", "TP = {RR} เท่าของ SL", "เกิดสัญญาณตรงข้าม → ปิดแล้วกลับฝั่ง" };
        public override double RewardRisk => P[2];

        private SwingTracker _swings;
        private double _stop;
        private int _lowUsed = -1, _highUsed = -1;

        protected override void OnInit()
        {
            Defaults(15, 0.02, 2);
            _swings = new SwingTracker(Bot.Bars, (int)P[0]);
        }

        public override double? StopDistance(TradeType side) => _stop;

        public override TradeType? Signal()
        {
            _swings.Update();
            var now = _swings.LastClosedIndex;
            var k = (int)P[0];
            var tol = C(1) * P[1] / 100;

            if (_swings.Lows.Count > 0)
            {
                var s = _swings.Lows[_swings.Lows.Count - 1];
                if (s.Index != _lowUsed && now - s.Index >= 2 * k)
                {
                    if (C(1) < s.Price - tol) _lowUsed = s.Index;            // แนวรับแตก
                    else if (L(1) <= s.Price + tol && C(1) > s.Price && C(1) > O(1))
                    {
                        _lowUsed = s.Index;
                        _stop = C(1) - (Math.Min(L(1), s.Price) - tol);
                        return TradeType.Buy;
                    }
                }
            }

            if (_swings.Highs.Count > 0)
            {
                var s = _swings.Highs[_swings.Highs.Count - 1];
                if (s.Index != _highUsed && now - s.Index >= 2 * k)
                {
                    if (C(1) > s.Price + tol) _highUsed = s.Index;           // แนวต้านแตก
                    else if (H(1) >= s.Price - tol && C(1) < s.Price && C(1) < O(1))
                    {
                        _highUsed = s.Index;
                        _stop = (Math.Max(H(1), s.Price) + tol) - C(1);
                        return TradeType.Sell;
                    }
                }
            }
            return null;
        }
    }
}
