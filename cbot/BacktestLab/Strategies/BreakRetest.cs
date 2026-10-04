using System;
using cAlgo.API;

namespace cAlgo.Robots
{
    public class BreakRetest : StrategyBase
    {
        public override string Code => "CH_BREAK_RETEST";
        public override string Name => "เบรกแล้วกลับมาทดสอบ (Break & Retest)";
        public override string Theory =>
            "ทฤษฎีกราฟ (แนวรับแนวต้าน): แนวต้านที่ถูกทะลุแล้วจะกลายเป็นแนวรับ (และกลับกัน) การรอให้ราคาย่อกลับมาทดสอบแนวแล้วยืนได้ ช่วยกรองการเบรกหลอกและได้จุดเข้าที่ SL สั้น";
        public override string[] ParamNames => new[] { "SwingK", "TolPct", "MaxWaitBars", "RR" };
        public override string[] EntryRules => new[]
        {
            "แท่งปิดเหนือจุดสวิงสูงล่าสุด (สวิงฝั่งละ {SwingK} แท่ง) = เบรกขึ้น จำระดับไว้",
            "ภายใน {MaxWaitBars} แท่ง ราคาย่อกลับมาแตะระดับ (ห่างไม่เกิน {TolPct}% ของราคา) แล้วปิดเป็นแท่งเขียวเหนือระดับ → Buy",
            "กลับกันสำหรับการเบรกลงใต้จุดสวิงต่ำ → Sell",
            "ถ้าราคาปิดกลับทะลุระดับไปอีกฝั่ง ยกเลิกการรอ",
        };
        public override string[] ExitRules => new[] { "SL = หลังระดับที่ทดสอบ", "TP = {RR} เท่าของ SL", "เกิดสัญญาณตรงข้าม → ปิดแล้วกลับฝั่ง" };
        public override double RewardRisk => P[3];

        private SwingTracker _swings;
        private double _stop;
        private double _upLevel, _downLevel;
        private int _upAt = -1, _downAt = -1, _highBroken = -1, _lowBroken = -1;

        protected override void OnInit()
        {
            Defaults(15, 0.02, 60, 2);
            _swings = new SwingTracker(Bot.Bars, (int)P[0]);
        }

        public override double? StopDistance(TradeType side) => _stop;

        public override TradeType? Signal()
        {
            _swings.Update();
            var now = _swings.LastClosedIndex;
            var tol = C(1) * P[1] / 100;
            var maxWait = (int)P[2];

            // ---- รอทดสอบหลังเบรกขึ้น ----
            if (_upAt >= 0)
            {
                if (now - _upAt > maxWait || C(1) < _upLevel - tol) _upAt = -1;
                else if (now > _upAt && L(1) <= _upLevel + tol && C(1) > _upLevel && C(1) > O(1))
                {
                    _upAt = -1;
                    _stop = C(1) - (Math.Min(L(1), _upLevel) - tol);
                    return TradeType.Buy;
                }
            }
            if (_downAt >= 0)
            {
                if (now - _downAt > maxWait || C(1) > _downLevel + tol) _downAt = -1;
                else if (now > _downAt && H(1) >= _downLevel - tol && C(1) < _downLevel && C(1) < O(1))
                {
                    _downAt = -1;
                    _stop = (Math.Max(H(1), _downLevel) + tol) - C(1);
                    return TradeType.Sell;
                }
            }

            // ---- หาการเบรกใหม่ ----
            if (_swings.Highs.Count > 0)
            {
                var s = _swings.Highs[_swings.Highs.Count - 1];
                if (s.Index != _highBroken && C(1) > s.Price + tol)
                {
                    _highBroken = s.Index; _upLevel = s.Price; _upAt = now;
                }
            }
            if (_swings.Lows.Count > 0)
            {
                var s = _swings.Lows[_swings.Lows.Count - 1];
                if (s.Index != _lowBroken && C(1) < s.Price - tol)
                {
                    _lowBroken = s.Index; _downLevel = s.Price; _downAt = now;
                }
            }
            return null;
        }
    }
}
