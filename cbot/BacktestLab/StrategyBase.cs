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

        // ---- helper สำหรับทฤษฎี ----
        protected static bool CrossedAbove(DataSeries a, DataSeries b) => a.Last(2) <= b.Last(2) && a.Last(1) > b.Last(1);
        protected static bool CrossedBelow(DataSeries a, DataSeries b) => a.Last(2) >= b.Last(2) && a.Last(1) < b.Last(1);
        protected static bool CrossedAbove(DataSeries a, double level) => a.Last(2) <= level && a.Last(1) > level;
        protected static bool CrossedBelow(DataSeries a, double level) => a.Last(2) >= level && a.Last(1) < level;
    }
}
