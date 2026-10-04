// โมเมนตัมข้าม session (กฎ R1 จาก research\JOURNAL.md) — มาจากการศึกษาชั้น 0 (research\Phase1 study:h6)
// ไม่มี SL/TP ในตัวทฤษฎี: ออกเมื่อถึงเวลา Flat ของ robot (ต้องรันด้วย -FlatTime 2045) และควรตั้ง Stop loss / Take profit (pips) = 0

using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;

namespace cAlgo.Robots
{
    public class SessionMomentum : StrategyBase
    {
        public override string Code => "CH_SESSION_MOM";
        public override string Name => "โมเมนตัมข้าม session";
        public override string Theory =>
            "โมเมนตัมระหว่างวัน: เมื่อทองขยับจากต้นวัน (00:00 UTC) ไปทางใดทางหนึ่งมากกว่าปกติจนถึงช่วงเช้าของลอนดอน ทิศนั้นมักไปต่อจนถึงปลายวัน โดยผลส่วนใหญ่เกิดในช่วงนิวยอร์ก ถือหลายชั่วโมงจึงทำให้ต้นทุนเล็กมากเมื่อเทียบกับการขยับ";
        public override string[] ParamNames => new[] { "Threshold", "Lookback", "Hour1", "Hour2" };
        public override string[] EntryRules => new[]
        {
            "เวลา {Hour1}:00 UTC: วัดการขยับตั้งแต่ 00:00 UTC เทียบกับขนาดปกติของ {Lookback} วันที่ผ่านมา ถ้าเกิน {Threshold} เท่า → เข้าตามทิศนั้น (ไม้ที่ 1)",
            "เวลา {Hour2}:00 UTC: วัดแบบเดียวกัน ถ้าเกิน {Threshold} เท่า → เข้าตามทิศนั้น (ไม้ที่ 2)",
            "ถือพร้อมกันได้สูงสุด 2 ไม้ แต่ละไม้เป็นอิสระต่อกัน",
        };
        public override string[] ExitRules => new[] { "ปิดทุกไม้เมื่อถึงเวลา Flat ของวัน (20:45 UTC)", "ไม่มี SL/TP ในรุ่นนี้" };
        public override bool ExitOnOppositeSignal => false;
        public override int MaxPositions => 2;

        private DateTime _day;
        private double _open;
        private bool _hasOpen;
        private bool[] _done;
        private int[] _hours;
        private Queue<double>[] _history;

        protected override void OnInit()
        {
            Defaults(0.75, 60, 7, 10);
            _hours = new[] { (int)P[2], (int)P[3] };
            _done = new bool[_hours.Length];
            _history = _hours.Select(_ => new Queue<double>()).ToArray();
        }

        public override TradeType? Signal()
        {
            var now = Bot.Server.Time;                       // เวลาเปิดของแท่งใหม่ = เวลาปิดของแท่งล่าสุด
            if (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday) return null;
            if (now.Date != _day) { _day = now.Date; _hasOpen = false; Array.Clear(_done, 0, _done.Length); }
            if (!_hasOpen)
            {
                if (now.Hour != 0 || now.Minute >= 30) return null;
                _open = Bot.Bars.OpenPrices.LastValue;       // ราคาเปิดของแท่งแรกของวัน (รู้แล้วตอนแท่งเปิด)
                _hasOpen = true;
            }

            for (int k = 0; k < _hours.Length; k++)
            {
                if (_done[k] || now.Hour != _hours[k] || now.Minute >= 5) continue;
                _done[k] = true;
                var move = C(1) - _open;
                var hist = _history[k];
                var usual = hist.Count >= 20 ? Math.Sqrt(hist.Average(v => v * v)) : 0;
                hist.Enqueue(move);
                if (hist.Count > (int)P[1]) hist.Dequeue();
                if (usual > 0 && Math.Abs(move) >= P[0] * usual) return move > 0 ? TradeType.Buy : TradeType.Sell;
            }
            return null;
        }
    }
}
