// ฐานของทุกทฤษฎี — เพิ่มทฤษฎีใหม่ = สร้างไฟล์ใหม่ใน Strategies/ ที่ inherit StrategyBase
// ระบบหาคลาสเองอัตโนมัติจาก Code ไม่ต้องลงทะเบียนที่ไหน
//
// ตัวอย่างขั้นต่ำ:
//
//   public class MyIdea : StrategyBase
//   {
//       public override string Code => "MY_IDEA";          // ใส่ในพารามิเตอร์ "Strategy code"
//       public override int Version => 1;                  // เปลี่ยนตรรกะเมื่อไหร่ ให้ +1 (ผลเก่าจะไม่ปนกัน)
//       public override string Name => "ชื่อที่อ่านง่าย";
//       public override string Theory => "แนวคิด: ทำไมทฤษฎีนี้ควรได้ผล";
//       public override string[] ParamNames => new[] { "Period", "Level" };   // ความหมายของ P1, P2
//       public override string[] EntryRules => new[] { "ราคาปิด > ... {Period} แท่ง → Buy" };
//       public override string[] ExitRules => new[] { "SL/TP หรือสัญญาณตรงข้าม" };
//       protected override void OnInit() { /* สร้าง indicator จาก Bot.Indicators */ }
//       public override TradeType? Signal() { return null; /* Buy / Sell / null */ }
//   }

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using cAlgo.API;

namespace cAlgo.Robots
{
    public abstract class StrategyBase
    {
        public abstract string Code { get; }
        public virtual int Version => 1;
        public abstract string Name { get; }
        public abstract string Theory { get; }
        public abstract string[] EntryRules { get; }
        public abstract string[] ExitRules { get; }

        /// <summary>ชื่อของ P1..P4 ตามลำดับ (ไม่ใช้ก็ไม่ต้องใส่)</summary>
        public virtual string[] ParamNames => new string[0];

        /// <summary>เจอสัญญาณตรงข้ามแล้วปิดออเดอร์เดิมไหม</summary>
        public virtual bool ExitOnOppositeSignal => true;

        protected Robot Bot { get; private set; }
        protected double[] P { get; private set; }

        public void Attach(Robot bot, double[] p)
        {
            Bot = bot;
            P = p;
            OnInit();
        }

        protected abstract void OnInit();

        /// <summary>เรียกทุกครั้งที่แท่งใหม่เปิด — ใช้ค่าแท่งที่ปิดแล้ว (.Last(1))</summary>
        public abstract TradeType? Signal();

        /// <summary>เงื่อนไขออกเพิ่มเติมนอกจาก SL/TP และสัญญาณตรงข้าม</summary>
        public virtual bool ShouldExit(Position position) => false;

        /// <summary>
        /// ทฤษฎีกราฟ: วาง SL ตามโครงสร้างราคา — คืนระยะจากราคาเข้าถึง SL (หน่วยราคา) ของสัญญาณล่าสุด
        /// null = ใช้ Stop loss / Take profit (pips) ของ robot ตามเดิม
        /// robot จะบีบระยะให้อยู่ใน Min/Max stop (% ของราคา) แล้วตั้ง TP = ระยะ SL × RewardRisk
        /// </summary>
        public virtual double? StopDistance(TradeType side) => null;
        public virtual double RewardRisk => 2;

        /// <summary>
        /// การจัดการออเดอร์หลังเข้า ของสัญญาณล่าสุด (หน่วย R = ระยะ SL ตอนเข้า) — robot จำไว้ต่อ position
        ///   BreakevenAtR   กำไรถึงกี่ R แล้วเลื่อน SL มาที่ราคาเข้า (0 = ไม่ใช้)
        ///   CutAfterMinutes / CutBelowR   ผ่านไปกี่นาทีแล้ว ถ้ายังต่ำกว่า CutBelowR ให้ปิด (0 = ไม่ใช้)
        /// </summary>
        public virtual double BreakevenAtR => 0;
        public virtual int CutAfterMinutes => 0;
        public virtual double CutBelowR => 0;

        /// <summary>ป้ายของสัญญาณล่าสุด — robot ใส่เป็น Comment ของ position (ทฤษฎีรวมใช้แยกว่าออเดอร์มาจากทฤษฎีย่อยไหน)</summary>
        public virtual string SignalTag => null;

        /// <summary>true = เครื่องมือ (เช่นส่งออกข้อมูล) ไม่ใช่ทฤษฎี — ไม่ส่งผลขึ้น lab</summary>
        public virtual bool IsUtility => false;

        /// <summary>เรียกตอน cBot หยุด (ปิดไฟล์ ฯลฯ)</summary>
        public virtual void OnStop() { }

        public Dictionary<string, object> NamedParams()
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < ParamNames.Length && i < P.Length; i++)
                if (!string.IsNullOrWhiteSpace(ParamNames[i])) d[ParamNames[i]] = P[i];
            return d;
        }

        /// <summary>แทน {ชื่อพารามิเตอร์} ในกฎด้วยค่าจริง</summary>
        public string Describe(string rule)
        {
            foreach (var kv in NamedParams()) rule = rule.Replace("{" + kv.Key + "}", kv.Value.ToString());
            return rule;
        }

        public static StrategyBase Create(string code)
        {
            var all = Assembly.GetExecutingAssembly().GetTypes()
                .Where(t => typeof(StrategyBase).IsAssignableFrom(t) && !t.IsAbstract)
                .Select(t => (StrategyBase)Activator.CreateInstance(t))
                .ToList();

            var found = all.FirstOrDefault(s => string.Equals(s.Code, (code ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (found == null)
                throw new ArgumentException("Unknown strategy code '" + code + "'. Available: " + string.Join(", ", all.Select(s => s.Code)));
            return found;
        }

        // ---- helper สำหรับทฤษฎีกราฟ: i = จำนวนแท่งย้อนหลัง (1 = แท่งล่าสุดที่ปิดแล้ว) ----
        protected double O(int i) => Bot.Bars.OpenPrices.Last(i);
        protected double H(int i) => Bot.Bars.HighPrices.Last(i);
        protected double L(int i) => Bot.Bars.LowPrices.Last(i);
        protected double C(int i) => Bot.Bars.ClosePrices.Last(i);
        protected double Body(int i) => Math.Abs(C(i) - O(i));
        protected double Range(int i) => H(i) - L(i);
        protected bool HasBars(int n) => Bot.Bars.Count > n + 2;

        /// <summary>เรียกใน OnInit: P ตัวไหน ≤ 0 ให้ใช้ค่าเริ่มต้นของทฤษฎี (ผลที่ส่งขึ้น lab จะเห็นค่าที่ใช้จริง)</summary>
        protected void Defaults(params double[] values)
        {
            for (int i = 0; i < values.Length && i < P.Length; i++)
                if (P[i] <= 0) P[i] = values[i];
        }

        /// <summary>วันเทรด (UTC): แท่งคืนวันอาทิตย์นับเป็นวันจันทร์</summary>
        protected static DateTime TradingDay(DateTime t) =>
            t.DayOfWeek == DayOfWeek.Sunday ? t.Date.AddDays(1) : t.Date;

        /// <summary>เวลาเปิด (UTC) ของแท่งล่าสุดที่ปิดแล้ว</summary>
        protected DateTime BarTime => Bot.Bars.OpenTimes.Last(1);

        /// <summary>High สูงสุดของ count แท่ง เริ่มจากแท่งที่ from (ย้อนหลัง)</summary>
        protected double HighestHigh(int from, int count)
        {
            var v = double.MinValue;
            for (int i = from; i < from + count; i++) v = Math.Max(v, H(i));
            return v;
        }

        protected double LowestLow(int from, int count)
        {
            var v = double.MaxValue;
            for (int i = from; i < from + count; i++) v = Math.Min(v, L(i));
            return v;
        }

        /// <summary>ค่าเฉลี่ยความยาวแท่ง (High-Low) ของ count แท่ง เริ่มจากแท่งที่ from</summary>
        protected double AvgRange(int from, int count)
        {
            double s = 0;
            for (int i = from; i < from + count; i++) s += Range(i);
            return s / count;
        }

        // ---- helper สำหรับทฤษฎี ----
        protected static bool CrossedAbove(DataSeries a, DataSeries b) => a.Last(2) <= b.Last(2) && a.Last(1) > b.Last(1);
        protected static bool CrossedBelow(DataSeries a, DataSeries b) => a.Last(2) >= b.Last(2) && a.Last(1) < b.Last(1);
        protected static bool CrossedAbove(DataSeries a, double level) => a.Last(2) <= level && a.Last(1) > level;
        protected static bool CrossedBelow(DataSeries a, double level) => a.Last(2) >= level && a.Last(1) < level;
    }
}
