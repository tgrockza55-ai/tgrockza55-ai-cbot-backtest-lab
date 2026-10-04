// cBot Backtest Lab — ตัว Robot หลัก
// เลือกทฤษฎีด้วยพารามิเตอร์ "Strategy code" แล้วตั้ง P1..P4 ตามที่ทฤษฎีนั้นกำหนด (ดูใน Log ตอนเริ่ม)
// จบ backtest แล้วส่งผลขึ้น Supabase (สรุป) + Google Drive (trades/equity เต็ม) ผ่าน Edge Function "lab"

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(AccessRights = AccessRights.FullAccess, TimeZone = TimeZones.UTC, AddIndicators = true)]
    public class BacktestLab : Robot
    {
        [Parameter("Strategy code", DefaultValue = "EMA_CROSS", Group = "Strategy")]
        public string StrategyCode { get; set; }

        [Parameter("P1", DefaultValue = 9, Group = "Strategy")]
        public double P1 { get; set; }

        [Parameter("P2", DefaultValue = 21, Group = "Strategy")]
        public double P2 { get; set; }

        [Parameter("P3", DefaultValue = 0, Group = "Strategy")]
        public double P3 { get; set; }

        [Parameter("P4", DefaultValue = 0, Group = "Strategy")]
        public double P4 { get; set; }

        [Parameter("Lots", DefaultValue = 0.01, MinValue = 0.01, Step = 0.01, Group = "Risk")]
        public double Lots { get; set; }

        [Parameter("Stop loss (pips, 0 = none)", DefaultValue = 20, MinValue = 0, Group = "Risk")]
        public double StopLossPips { get; set; }

        [Parameter("Take profit (pips, 0 = none)", DefaultValue = 40, MinValue = 0, Group = "Risk")]
        public double TakeProfitPips { get; set; }

        // ใช้กับทฤษฎีที่วาง SL ตามโครงสร้างราคา (StopDistance) — เป็น % ของราคา จึงเทียบกันได้ทุกช่วงราคา
        [Parameter("Min stop (% of price)", DefaultValue = 0.05, MinValue = 0, Group = "Risk")]
        public double MinStopPct { get; set; }

        [Parameter("Max stop (% of price)", DefaultValue = 0.5, MinValue = 0, Group = "Risk")]
        public double MaxStopPct { get; set; }

        // เทรดในวัน: ถึงเวลานี้ (UTC, รูปแบบ HHmm เช่น 2045) ปิดทุกออเดอร์ และไม่เปิดใหม่จนพ้นช่วงตลาดพัก (23:00 UTC); 0 = ไม่ใช้
        [Parameter("Flat at (UTC HHmm, 0 = off)", DefaultValue = 0, MinValue = 0, MaxValue = 2359, Group = "Risk")]
        public int FlatTime { get; set; }

        [Parameter("Send results", DefaultValue = true, Group = "Report")]
        public bool SendResults { get; set; }

        // งานวิจัยในเครื่อง: เขียนรายงานเป็นไฟล์ Documents\BacktestLab\local\<code>-<symbol>-<timeframe>.json
        [Parameter("Save local copy", DefaultValue = false, Group = "Report")]
        public bool SaveLocal { get; set; }

        [Parameter("Max equity points", DefaultValue = 5000, MinValue = 200, Group = "Report")]
        public int MaxEquityPoints { get; set; }

        [Parameter("Run note", DefaultValue = "", Group = "Report")]
        public string RunNote { get; set; }

        public const string Label = "BacktestLab";

        private StrategyBase _strategy;
        private EquityRecorder _equity;
        private DateTime _startTime;
        private double _startBalance;
        private bool _structureStops;
        // ราคา SL / TP ตอนเปิดของแต่ละ position (History ไม่เก็บไว้ให้)
        private readonly Dictionary<int, double?[]> _stops = new Dictionary<int, double?[]>();
        private readonly Dictionary<int, TradePlan> _plans = new Dictionary<int, TradePlan>();

        // เทรดตัวอย่างที่แนบแท่งราคาไปให้หน้าเว็บวาดกราฟ: ต่อวันในสัปดาห์ เอาชนะ/แพ้อย่างละเท่านี้
        private const int ExamplesPerGroup = 2;
        private const int ExampleBarsBefore = 60, ExampleBarsAfter = 20, ExampleMaxBars = 300;

        protected override void OnStart()
        {
            _strategy = StrategyBase.Create(StrategyCode);
            _strategy.Attach(this, new[] { P1, P2, P3, P4 });
            _equity = new EquityRecorder(MaxEquityPoints);
            _startTime = Server.Time;
            _startBalance = Account.Balance;

            Print("Strategy: {0} v{1} — {2}", _strategy.Name, _strategy.Version, _strategy.Code);
            Print("Theory: {0}", _strategy.Theory);
            foreach (var kv in _strategy.NamedParams()) Print("  {0} = {1}", kv.Key, kv.Value);
            foreach (var rule in _strategy.EntryRules) Print("  Entry: {0}", _strategy.Describe(rule));
            foreach (var rule in _strategy.ExitRules) Print("  Exit:  {0}", _strategy.Describe(rule));

            if (RunningMode == RunningMode.RealTime)
                Print("WARNING: running live/demo — results are only sent in backtest mode.");
            else if (RunningMode != RunningMode.Optimization && SendResults)
                Reporter.RetryPending(this);
        }

        protected override void OnBar()
        {
            _equity.Add(Server.Time, Account.Equity);

            var flat = InFlatWindow(Server.Time);
            foreach (var pos in Positions.FindAll(Label, SymbolName))
            {
                if (flat || _strategy.ShouldExit(pos)) ClosePosition(pos);
                else Manage(pos);
            }

            var signal = _strategy.Signal();   // เรียกทุกแท่ง เพื่อให้สถานะของทฤษฎีเดินต่อเนื่อง
            if (signal == null || flat) return;

            if (_strategy.ExitOnOppositeSignal)
                foreach (var pos in Positions.FindAll(Label, SymbolName).Where(p => p.TradeType != signal.Value))
                    ClosePosition(pos);

            // ถือพร้อมกันได้ตามที่ทฤษฎีกำหนด (ปกติ 1) แต่ไม่เกิน 3 ไม้ และ 3 ไม้ต้องไม่ไปทางเดียวกันทั้งหมด
            var open = Positions.FindAll(Label, SymbolName);
            if (open.Length >= Math.Min(3, _strategy.MaxPositions)) return;
            if (open.Length >= 2 && open.All(p => p.TradeType == signal.Value)) return;

            var volume = Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(Lots));
            double? sl = StopLossPips > 0 ? StopLossPips : (double?)null;
            double? tp = TakeProfitPips > 0 ? TakeProfitPips : (double?)null;

            var distance = _strategy.StopDistance(signal.Value);
            if (distance.HasValue)
            {
                var price = Symbol.Bid;
                var d = Math.Max(distance.Value, price * MinStopPct / 100);
                if (MaxStopPct > 0) d = Math.Min(d, price * MaxStopPct / 100);
                sl = Math.Round(d / Symbol.PipSize, 1);
                tp = Math.Round(sl.Value * _strategy.RewardRisk, 1);
                _structureStops = true;
            }
            var result = ExecuteMarketOrder(signal.Value, SymbolName, volume, Label, sl, tp, _strategy.SignalTag);
            if (result.IsSuccessful && result.Position != null)
            {
                var p = result.Position;
                _stops[p.Id] = new[] { p.StopLoss, p.TakeProfit };
                if (p.StopLoss.HasValue && (_strategy.BreakevenAtR > 0 || _strategy.CutAfterMinutes > 0))
                    _plans[p.Id] = new TradePlan
                    {
                        R = Math.Abs(p.EntryPrice - p.StopLoss.Value),
                        BreakevenAtR = _strategy.BreakevenAtR,
                        CutAfterMinutes = _strategy.CutAfterMinutes,
                        CutBelowR = _strategy.CutBelowR,
                    };
            }
        }

        private class TradePlan
        {
            public double R, BreakevenAtR, CutBelowR;
            public int CutAfterMinutes;
            public bool AtBreakeven, CutChecked;
        }

        /// <summary>จัดการออเดอร์ตามแผนของทฤษฎี: เลื่อน SL มากันทุน และตัดทิ้งเมื่อครบเวลาแล้วยังไม่ถึงระดับที่กำหนด — คืน true ถ้าปิดไปแล้ว</summary>
        private bool Manage(Position pos)
        {
            TradePlan plan;
            if (!_plans.TryGetValue(pos.Id, out plan) || plan.R <= 0) return false;
            var profitR = pos.Pips * Symbol.PipSize / plan.R;

            if (plan.CutAfterMinutes > 0 && !plan.CutChecked && (Server.Time - pos.EntryTime).TotalMinutes >= plan.CutAfterMinutes)
            {
                plan.CutChecked = true;
                if (profitR < plan.CutBelowR) { ClosePosition(pos); return true; }
            }
            if (plan.BreakevenAtR > 0 && !plan.AtBreakeven && profitR >= plan.BreakevenAtR)
            {
                plan.AtBreakeven = true;
                ModifyPosition(pos, pos.EntryPrice, pos.TakeProfit);
            }
            return false;
        }

        /// <summary>ช่วงห้ามถือออเดอร์: ตั้งแต่ FlatTime ถึง 23:00 UTC (ครอบช่วงตลาดพักประจำวันและคืนวันศุกร์)</summary>
        private bool InFlatWindow(DateTime t)
        {
            if (FlatTime <= 0) return false;
            var minutes = t.Hour * 60 + t.Minute;
            return minutes >= (FlatTime / 100) * 60 + FlatTime % 100 && minutes < 23 * 60;
        }

        protected override void OnStop()
        {
            if (_equity == null) return;
            _equity.Add(Server.Time, Account.Equity, force: true);
            _strategy.OnStop();

            if (RunningMode == RunningMode.RealTime || _strategy.IsUtility || (!SendResults && !SaveLocal)) return;

            var openCount = Positions.FindAll(Label, SymbolName).Length;
            if (openCount > 0) Print("Note: {0} position(s) still open at the end are not counted as trades.", openCount);

            var report = BuildReport();
            if (SaveLocal) Reporter.SaveLocal(this, report, _strategy.Code + "-" + SymbolName + "-" + TimeFrame);
            if (SendResults) Reporter.Send(this, report);
        }

        private object BuildReport()
        {
            var trades = History
                .Where(t => t.Label == Label && t.SymbolName == SymbolName)
                .OrderBy(t => t.ClosingTime)
                .ToList();

            var wins = trades.Where(t => t.NetProfit > 0).ToList();
            var grossWin = wins.Sum(t => t.NetProfit);
            var grossLoss = trades.Where(t => t.NetProfit < 0).Sum(t => t.NetProfit);
            var netProfit = trades.Sum(t => t.NetProfit);

            var parameters = new Dictionary<string, object>(_strategy.NamedParams()) { ["Lots"] = Lots };
            if (FlatTime > 0) parameters["FlatTimeUtc"] = FlatTime;
            if (_structureStops)
            {
                parameters["MinStopPct"] = MinStopPct;
                parameters["MaxStopPct"] = MaxStopPct;
                parameters["RewardRisk"] = _strategy.RewardRisk;
            }
            else
            {
                parameters["StopLossPips"] = StopLossPips;
                parameters["TakeProfitPips"] = TakeProfitPips;
            }

            return new
            {
                strategy = new
                {
                    code = _strategy.Code,
                    version = _strategy.Version,
                    name = _strategy.Name,
                    theory = _strategy.Theory,
                    entryRules = _strategy.EntryRules,
                    exitRules = _strategy.ExitRules,
                    paramNames = _strategy.ParamNames,
                },
                run = new
                {
                    symbol = SymbolName,
                    timeframe = TimeFrame.ToString(),
                    dateFrom = Iso(_startTime),
                    dateTo = Iso(Server.Time),
                    @params = parameters,
                    runningMode = RunningMode.ToString(),
                    startBalance = Round(_startBalance),
                    endBalance = Round(Account.Balance),
                    netProfit = Round(netProfit),
                    totalTrades = trades.Count,
                    winRate = trades.Count > 0 ? Round(100.0 * wins.Count / trades.Count) : (double?)null,
                    profitFactor = grossLoss < 0 ? Round(grossWin / -grossLoss) : (double?)null,
                    maxDdPct = Round(_equity.MaxDrawdownPct),
                    machine = Environment.MachineName,
                    note = string.IsNullOrWhiteSpace(RunNote) ? null : RunNote,
                },
                trades = TradeRows(trades),
                equity = _equity.Points,   // [[unixSeconds, equity], ...]
            };
        }

        private List<Dictionary<string, object>> TradeRows(List<HistoricalTrade> trades)
        {
            var examples = PickExamples(trades);
            var rows = new List<Dictionary<string, object>>(trades.Count);
            foreach (var t in trades)
            {
                var row = new Dictionary<string, object>
                {
                    ["id"] = t.PositionId,
                    ["side"] = t.TradeType.ToString(),
                    ["entryTime"] = Iso(t.EntryTime),
                    ["exitTime"] = Iso(t.ClosingTime),
                    ["entryPrice"] = t.EntryPrice,
                    ["exitPrice"] = t.ClosingPrice,
                    ["volume"] = t.VolumeInUnits,
                    ["pips"] = Round(t.Pips),
                    ["gross"] = Round(t.GrossProfit),
                    ["commissions"] = Round(t.Commissions),
                    ["swap"] = Round(t.Swap),
                    ["net"] = Round(t.NetProfit),
                };
                if (!string.IsNullOrEmpty(t.Comment)) row["tag"] = t.Comment;
                double?[] stops;
                if (_stops.TryGetValue(t.PositionId, out stops))
                {
                    if (stops[0].HasValue) row["sl"] = stops[0].Value;
                    if (stops[1].HasValue) row["tp"] = stops[1].Value;
                }
                if (examples.Contains(t.PositionId))
                {
                    var bars = ExampleBars(t);
                    if (bars != null) row["bars"] = bars;   // [[unixSeconds, open, high, low, close], ...]
                }
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>เลือกเทรดตัวอย่าง: ต่อวันในสัปดาห์ (ตามเวลาเข้า UTC) × ชนะ/แพ้ เอาที่กระจายตามช่วงเวลา ไม่ใช่ตัวที่ดีสุด</summary>
        private HashSet<int> PickExamples(List<HistoricalTrade> trades)
        {
            var picked = new HashSet<int>();
            foreach (var group in trades.Where(t => t.NetProfit != 0)
                         .GroupBy(t => new { t.EntryTime.DayOfWeek, Win = t.NetProfit > 0 }))
            {
                var list = group.ToList();
                for (int i = 1; i <= ExamplesPerGroup; i++)
                    picked.Add(list[Math.Min(list.Count - 1, list.Count * i / (ExamplesPerGroup + 1))].PositionId);
            }
            return picked;
        }

        private List<double[]> ExampleBars(HistoricalTrade t)
        {
            var entry = Bars.OpenTimes.GetIndexByTime(t.EntryTime);
            var exit = Bars.OpenTimes.GetIndexByTime(t.ClosingTime);
            if (entry < 0 || exit < 0) return null;

            var from = Math.Max(0, entry - ExampleBarsBefore);
            var to = Math.Min(Bars.Count - 1, Math.Min(exit + ExampleBarsAfter, from + ExampleMaxBars - 1));
            var rows = new List<double[]>(to - from + 1);
            for (int i = from; i <= to; i++)
            {
                var unix = new DateTimeOffset(DateTime.SpecifyKind(Bars.OpenTimes[i], DateTimeKind.Utc)).ToUnixTimeSeconds();
                rows.Add(new[] { unix, Bars.OpenPrices[i], Bars.HighPrices[i], Bars.LowPrices[i], Bars.ClosePrices[i] });
            }
            return rows;
        }

        // InvariantCulture: เครื่องที่ตั้งภาษาไทยจะได้ปี พ.ศ. ถ้าไม่ระบุ
        private static string Iso(DateTime t) =>
            DateTime.SpecifyKind(t, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        private static double Round(double v) => Math.Round(v, 2);
    }

    /// <summary>เก็บ equity ทุกแท่ง คำนวณ max drawdown จากทุกจุด แต่เก็บจุดไว้ส่งไม่เกิน max (สุ่มลดแบบเว้นระยะ)</summary>
    public class EquityRecorder
    {
        private readonly int _max;
        private readonly List<double[]> _points = new List<double[]>();
        private int _stride = 1;
        private long _count;
        private double _peak = double.MinValue;

        public double MaxDrawdownPct { get; private set; }
        public List<double[]> Points => _points;

        public EquityRecorder(int max) { _max = Math.Max(200, max); }

        public void Add(DateTime time, double equity, bool force = false)
        {
            if (equity > _peak) _peak = equity;
            if (_peak > 0) MaxDrawdownPct = Math.Max(MaxDrawdownPct, 100.0 * (_peak - equity) / _peak);

            if (_count++ % _stride != 0 && !force) return;
            var unix = (double)new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)).ToUnixTimeSeconds();
            _points.Add(new[] { unix, Math.Round(equity, 2) });

            if (_points.Count >= _max * 2)
            {
                var kept = new List<double[]>(_max + 1);
                for (int i = 0; i < _points.Count; i += 2) kept.Add(_points[i]);
                _points.Clear();
                _points.AddRange(kept);
                _stride *= 2;
            }
        }
    }
}
