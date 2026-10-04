using cAlgo.API;

namespace cAlgo.Robots
{
    public class InsideBar : StrategyBase
    {
        public override string Code => "CH_INSIDE_BAR";
        public override string Name => "Inside bar แล้วเบรก";
        public override string Theory =>
            "ทฤษฎีกราฟ (แท่งเทียน): แท่งยาว (แท่งแม่) ตามด้วยแท่งที่อยู่ในกรอบของแท่งแม่ทั้งแท่ง คือตลาดพักตัวหลังวิ่งแรง เมื่อราคาปิดทะลุกรอบแท่งแม่ มักวิ่งต่อในทิศที่ทะลุ";
        public override string[] ParamNames => new[] { "Lookback", "MotherMult", "RR" };
        public override string[] EntryRules => new[]
        {
            "แท่งแม่ยาวอย่างน้อย {MotherMult} เท่าของความยาวแท่งเฉลี่ย {Lookback} แท่ง และแท่งถัดมาอยู่ในกรอบแท่งแม่",
            "แท่งถัดไปปิดเหนือ High ของแท่งแม่ → Buy",
            "แท่งถัดไปปิดใต้ Low ของแท่งแม่ → Sell",
        };
        public override string[] ExitRules => new[] { "SL = อีกฝั่งของแท่งแม่", "TP = {RR} เท่าของ SL", "เกิดสัญญาณตรงข้าม → ปิดแล้วกลับฝั่ง" };
        public override double RewardRisk => P[2];

        private double _stop;

        protected override void OnInit() { Defaults(30, 1.5, 2); }

        public override double? StopDistance(TradeType side) => _stop;

        public override TradeType? Signal()
        {
            var n = (int)P[0];
            if (!HasBars(n + 4)) return null;

            // แท่ง 3 = แท่งแม่, แท่ง 2 = inside bar, แท่ง 1 = แท่งเบรก
            if (!(H(2) <= H(3) && L(2) >= L(3))) return null;
            if (Range(3) < P[1] * AvgRange(4, n)) return null;

            if (C(1) > H(3)) { _stop = C(1) - L(3); return TradeType.Buy; }
            if (C(1) < L(3)) { _stop = H(3) - C(1); return TradeType.Sell; }
            return null;
        }
    }
}
