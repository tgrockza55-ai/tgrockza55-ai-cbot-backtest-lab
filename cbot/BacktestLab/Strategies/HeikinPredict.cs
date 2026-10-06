// ทายทิศของแท่ง Heikin Ashi ถัดไป (research\JOURNAL.md: "ทักษะการทำนายบนกราฟ Heikin Ashi") — นับอย่างเดียวว่าทายถูกกี่แท่ง ไม่เปิดออเดอร์
//
// ต้องรันบนกราฟ Heikin Ashi (เช่น Hm1): บนกราฟชนิดนี้ Bars ให้ค่าของแท่ง HA
//   ราคาปิด HA = (เปิด + สูง + ต่ำ + ปิด) / 4 ของแท่งจริง,  ราคาเปิด HA = (เปิด HA ก่อนหน้า + ปิด HA ก่อนหน้า) / 2
// ตอนแท่งใหม่เปิด ราคาเปิด HA ของมันถูกกำหนดตายตัวแล้ว และราคาจริงก็รู้แล้ว กฎจึงถามแค่ว่าราคาจริงอยู่ฝั่งไหนของระดับนั้น
//
// ผลอยู่ในโน้ตของรอบทดสอบ และในบรรทัด "HA_PREDICT result" / "HA_PREDICT month" ของ Log
// ตัวเลข "ราคาจริง" ที่รายงานคู่กัน = การทายชุดเดียวกัน วัดกับราคาจริงของนาทีนั้น — ต่างจากแท่ง HA เพราะแท่ง HA เป็นค่าเฉลี่ย ไม่ใช่ราคาที่เทรดได้

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;

namespace cAlgo.Robots
{
    public class HeikinPredict : StrategyBase
    {
        public override string Code => "HA_PREDICT";
        public override string Name => "ทายทิศแท่ง Heikin Ashi ถัดไป";
        public override string Theory =>
            "แท่ง Heikin Ashi ถูกทำให้เรียบด้วยการเฉลี่ย: ราคาเปิดของแท่งใหม่คือจุดกึ่งกลางของแท่งก่อน และราคาปิดคือค่าเฉลี่ยของนาทีนั้น " +
            "เมื่อแท่งใหม่เปิด ถ้าราคาจริงอยู่เหนือราคาเปิดของแท่ง HA อยู่แล้ว ค่าเฉลี่ยของนาทีนั้นก็มักจบเหนือระดับนั้นด้วย แท่งจึงจบเป็นแท่งขึ้น";
        public override string[] EntryRules => new[]
        {
            "ไม่เปิดออเดอร์: นับอย่างเดียวว่าทายถูกกี่แท่ง",
            "ตอนแท่ง HA ใหม่เปิด: ราคาจริงสูงกว่าราคาเปิดของแท่ง HA → ทายว่าแท่งนี้จบเป็นแท่งขึ้น, ต่ำกว่า → แท่งลง",
        };
        public override string[] ExitRules => new[] { "ไม่มีออเดอร์" };

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const double FarDistance = 0.2;                                  // "ชัดเจน" = ราคาห่างจากราคาเปิด HA อย่างน้อยเท่านี้ของกรอบแท่งเฉลี่ย

        private class Count { public long Bars, Right, Far, FarRight, Real, RealRight; }
        private readonly Count _all = new Count();
        private readonly SortedDictionary<string, Count> _months = new SortedDictionary<string, Count>();
        private readonly Queue<double> _ranges = new Queue<double>();
        private double _rangeSum, _calledAtBid;
        private int _call;                                                       // +1 = ทายว่าแท่งที่กำลังวิ่งจบเป็นแท่งขึ้น, -1 = ลง, 0 = ยังไม่ได้ทาย
        private bool _far;

        protected override void OnInit()
        {
            if (!Bot.TimeFrame.ToString().StartsWith("Heikin"))
                Bot.Print("HA_PREDICT: the chart is {0}, not a Heikin-Ashi one (use Hm1): the result will not mean what it says", Bot.TimeFrame);
        }

        public override TradeType? Signal()
        {
            var b = Bot.Bars; int closed = b.Count - 2, running = b.Count - 1;
            double bid = Bot.Symbol.Bid;

            // ให้คะแนนการทายของแท่งที่เพิ่งปิด
            if (_call != 0)
            {
                double open = b.OpenPrices[closed], close = b.ClosePrices[closed];
                string key = b.OpenTimes[closed].ToString("yyyy-MM", Inv);
                Count month; if (!_months.TryGetValue(key, out month)) _months[key] = month = new Count();
                foreach (var c in new[] { _all, month })
                {
                    if (close != open)
                    {
                        bool right = (_call > 0) == (close > open);
                        c.Bars++; if (right) c.Right++;
                        if (_far) { c.Far++; if (right) c.FarRight++; }
                    }
                    if (bid != _calledAtBid) { c.Real++; if ((_call > 0) == (bid > _calledAtBid)) c.RealRight++; }      // ราคาจริง: ต้นนาทีนั้นถึงต้นนาทีนี้
                }
            }

            double range = b.HighPrices[closed] - b.LowPrices[closed];
            _ranges.Enqueue(range); _rangeSum += range;
            if (_ranges.Count > 60) _rangeSum -= _ranges.Dequeue();
            double usual = _rangeSum / _ranges.Count;

            // ทายแท่งที่เพิ่งเปิด
            double haOpen = b.OpenPrices[running];
            _call = bid > haOpen ? 1 : bid < haOpen ? -1 : b.ClosePrices[closed] >= b.OpenPrices[closed] ? 1 : -1;
            _far = usual > 0 && Math.Abs(bid - haOpen) >= FarDistance * usual;
            _calledAtBid = bid;
            return null;
        }

        private static string Pct(long right, long n) => n > 0 ? (100.0 * right / n).ToString("0.00", Inv) : "-";

        public override string ReportNote =>
            _all.Bars == 0 ? null :
            "ทายทิศแท่ง HA ถัดไปถูก " + Pct(_all.Right, _all.Bars) + "% จาก " + _all.Bars.ToString("N0", Inv) + " แท่ง" +
            " · เฉพาะตอนราคาห่างชัดเจน: " + Pct(_all.FarRight, _all.Far) + "% (" + Pct(_all.Far, _all.Bars) + "% ของแท่ง)" +
            " · การทายชุดเดียวกันเทียบกับราคาจริงของนาทีนั้น: ถูก " + Pct(_all.RealRight, _all.Real) + "%";

        public override void OnStop()
        {
            foreach (var kv in _months)
                Bot.Print("HA_PREDICT month {0} bars {1} right {2} far {3} farRight {4} real {5} realRight {6}", kv.Key, kv.Value.Bars, kv.Value.Right, kv.Value.Far, kv.Value.FarRight, kv.Value.Real, kv.Value.RealRight);
            Bot.Print("HA_PREDICT result bars {0} right {1} pct {2} far {3} farRight {4} farPct {5} real {6} realRight {7} realPct {8}", _all.Bars, _all.Right, Pct(_all.Right, _all.Bars),
                _all.Far, _all.FarRight, Pct(_all.FarRight, _all.Far), _all.Real, _all.RealRight, Pct(_all.RealRight, _all.Real));
        }
    }
}
