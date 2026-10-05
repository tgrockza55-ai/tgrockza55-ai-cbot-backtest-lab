// NYV (research\JOURNAL.md ลูปที่ 11): เปิดช่วงนิวยอร์กนอก value area ของวันก่อน → ไปต่อทางนั้น
//
// volume profile ของวันก่อนคำนวณจากทุก tick ด้วย FlowTracker (นิยามเดียวกับ research\Phase1\Flow.cs ส่วนที่ 6)
// จึงต้อง backtest ด้วยข้อมูล tick (ค่าเริ่มต้นของ tools\backtest.ps1) และรันบนกราฟ M1
// ต้องมี Documents\BacktestLab\data\news\events.csv (tools\news-fetch.ps1): วันที่มีข่าวแรงใกล้เวลาตัดสินใจจะไม่เข้า
// วันแรกของแต่ละรอบ backtest ยังไม่มี profile ของวันก่อน จึงไม่มีสัญญาณ

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using cAlgo.API;
using File = System.IO.File;

namespace cAlgo.Robots
{
    public class NyValue : StrategyBase
    {
        public override string Code => "NY_VALUE";
        public override string Name => "นิวยอร์กเปิดนอก value area ของเมื่อวาน";
        public override string Theory =>
            "value area คือช่วงราคาที่ตลาดใช้เวลาอยู่ 70% ของเมื่อวาน (วัดจากจำนวน tick ต่อระดับราคา) ถ้าถึงเวลาเปิดช่วงนิวยอร์กแล้วราคายังอยู่นอกช่วงนั้น " +
            "แปลว่าเอเชียและลอนดอนพาราคาออกจากจุดที่ตลาดเคยยอมรับ และช่วงนิวยอร์กซึ่งเป็นช่วงที่ราคาวิ่งเป็นแนวโน้มมากที่สุดของวันมักพาไปต่อทางเดิม";
        public override string[] ParamNames => new[] { "HoldMinutes", "StopUsd" };
        public override string[] EntryRules => new[]
        {
            "เวลา 13:00 UTC (20:00 น. ไทย): ราคาอยู่เหนือขอบบนของ value area ของวันก่อน (VAH) → Buy, ต่ำกว่าขอบล่าง (VAL) → Sell, อยู่ข้างใน → ไม่เข้า",
            "ไม่เข้าถ้ามีข่าวระดับสูงใน 30 นาทีข้างหน้า หรือเพิ่งออกมาไม่ถึง 120 นาที",
            "วันละไม่เกิน 1 ไม้",
        };
        public override string[] ExitRules => new[] { "ครบ {HoldMinutes} นาที → ปิด", "SL {StopUsd} ดอลลาร์", "ไม่มี TP" };
        public override bool ExitOnOppositeSignal => false;
        public override double RewardRisk => 0;                                  // ไม่มี TP
        public override double? StopDistance(TradeType side) => P[1];

        private const int DecisionHour = 12, DecisionMinute = 59;                // แท่งที่ปิดตอน 13:00 UTC
        private readonly FlowTracker _flow = new FlowTracker();
        private long[] _events = new long[0];
        private bool _live;

        private static long Unix(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeSeconds();

        protected override void OnInit()
        {
            Defaults(60, 12);
            _live = Bot.RunningMode == RunningMode.RealTime;
            var eventFile = Path.Combine(Reporter.Folder, "data", "news", "events.csv");
            if (File.Exists(eventFile))
                _events = File.ReadAllLines(eventFile).Skip(1).Select(l => l.Split(','))
                    .Where(p => p.Length >= 3 && (p[2] == "HIGH" || p[2] == "EXTREME"))
                    .Select(p => long.Parse(p[0], CultureInfo.InvariantCulture)).OrderBy(t => t).ToArray();
            else Bot.Print("NY_VALUE: missing {0} (run tools\\news-fetch.ps1) — trading without the news filter", eventFile);

            if (_live)
            {
                try
                {
                    int fed = _flow.WarmFromHistory(Bot.MarketData.GetTicks(), Bot.Server.Time);
                    Bot.Print("NY_VALUE: {0} historical ticks; yesterday's value area {1} .. {2} ({3})", fed, _flow.PrevVal, _flow.PrevVah,
                        _flow.PrevWarm ? "complete" : "not complete: no trade until a full day has been seen");
                }
                catch (Exception e) { Bot.Print("NY_VALUE: tick history not available: {0}", e.Message); }
            }
        }

        public override void OnTick() => _flow.Feed(Unix(Bot.Server.Time), Bot.Symbol.Bid, Bot.Symbol.Ask);

        /// <summary>ใกล้ข่าวระดับสูง: ภายใน 30 นาทีก่อนข่าว หรือยังไม่ครบ 120 นาทีหลังข่าว</summary>
        private bool NearNews(long now)
        {
            int k = Array.BinarySearch(_events, now); if (k < 0) k = ~k;
            if (k < _events.Length && _events[k] - now <= 30 * 60) return true;
            return k > 0 && now - _events[k - 1] < 120 * 60;
        }

        public override TradeType? Signal()
        {
            int i = Bot.Bars.Count - 2;
            var t = Bot.Bars.OpenTimes[i];
            if (t.Hour != DecisionHour || t.Minute != DecisionMinute) return null;
            if (double.IsNaN(_flow.PrevVah) || (_live && !_flow.PrevWarm)) return null;     // รันสดต้องเห็นวันก่อนครบทั้งวัน
            if (NearNews(Unix(t) + 60)) return null;

            double close = Bot.Bars.ClosePrices[i];
            if (close > _flow.PrevVah) return TradeType.Buy;
            if (close < _flow.PrevVal) return TradeType.Sell;
            return null;
        }

        // เผื่อครึ่งนาทีเหมือน SCALP_S3D: บน tick แท่งใหม่เริ่มหลังนาทีเต็มเล็กน้อย
        public override bool ShouldExit(Position position) => (Bot.Server.Time - position.EntryTime).TotalMinutes >= P[0] - 0.5;
    }
}
