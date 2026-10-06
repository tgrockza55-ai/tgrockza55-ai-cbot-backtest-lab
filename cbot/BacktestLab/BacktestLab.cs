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

        // รันสดบนบัญชี Demo: บันทึกตลาด (tick, DOM, คำทำนาย, ข่าว, ราคาที่ได้จริง) ลง %LOCALAPPDATA%\BacktestLab\live — ดู LiveRecorder.cs
        // ไม่มีผลตอน backtest
        [Parameter("Record market", DefaultValue = true, Group = "Live")]
        public bool RecordMarket { get; set; }

        [Parameter("Place orders (off = record only)", DefaultValue = true, Group = "Live")]
        public bool PlaceOrders { get; set; }

        [Parameter("Book snapshot every (s)", DefaultValue = 2, MinValue = 1, Group = "Live")]
        public int BookSeconds { get; set; }

        public const string Label = "BacktestLab";
        // กราฟสดโหลดประวัติมาแค่ราวพันแท่ง — ทฤษฎีที่มีสถานะต่อเนื่อง (EMA, กรอบของวันก่อน, โมเดล) ต้องการมากกว่านั้น
        private const int LiveHistoryBars = 6000;

        private StrategyBase _strategy;
        private EquityRecorder _equity;
        private LiveRecorder _recorder;
        private DateTime _startTime;
        private double _startBalance;
        private bool _structureStops;
        private bool _canTrade = true;
        // ราคาที่เห็นตอนสั่งปิด ของแต่ละ position — ไว้เทียบกับราคาที่ได้จริง (ใช้เฉพาะตอนบันทึกตลาดสด)
        private readonly Dictionary<int, double> _expectedClose = new Dictionary<int, double>();
        // ราคา SL / TP ตอนเปิดของแต่ละ position (History ไม่เก็บไว้ให้)
        private readonly Dictionary<int, double?[]> _stops = new Dictionary<int, double?[]>();
        private readonly Dictionary<int, TradePlan> _plans = new Dictionary<int, TradePlan>();

        // แท่งราคาที่แนบไปกับเทรด ให้หน้าเว็บวาดกราฟ
        //   เทรดตัวอย่าง (ต่อวันในสัปดาห์ เอาชนะ/แพ้อย่างละ ExamplesPerGroup) ได้กรอบกว้าง — แสดงในหัวข้อ "ตัวอย่างกราฟ"
        //   เทรดที่เหลือได้กรอบแคบ — แสดงเมื่อกดแถวในรายการเทรด; จำนวนแท่งรวมจำกัดที่ MaxReportBars (เกินแล้วเทรดเก่าสุดไม่มีกราฟ)
        private const int ExamplesPerGroup = 2;
        private const int ExampleBarsBefore = 60, ExampleBarsAfter = 20, ExampleMaxBars = 300;
        private const int TradeBarsBefore = 30, TradeBarsAfter = 12, MaxReportBars = 50000;

        protected override void OnStart()
        {
            var live = RunningMode == RunningMode.RealTime;
            if (live)
            {
                ApplyLiveSettings();
                try
                {
                    for (int k = 0; k < 30 && Bars.Count < LiveHistoryBars; k++)
                        if (Bars.LoadMoreHistory() <= 0) break;
                }
                catch (Exception e) { Print("Could not load more history: {0}", e.Message); }
                Print("History loaded: {0} bars", Bars.Count);
            }

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

            if (live)
            {
                // cBot นี้ไม่ส่งคำสั่งบนบัญชีเงินจริงเด็ดขาด — บันทึกตลาดได้อย่างเดียว
                _canTrade = PlaceOrders && !Account.IsLive;
                if (Account.IsLive) Print("LIVE (real money) account: this cBot never places orders here. Recording only.");
                else if (!PlaceOrders) Print("Place orders is off: recording only.");
                else Print("Demo account: orders are ON ({0} lot). Results are not sent to the lab in real time.", Lots);
            }
            else if (RunningMode != RunningMode.Optimization && SendResults)
                Reporter.RetryPending(this);

            // BACKTESTLAB_RECORD=<ชื่อโฟลเดอร์>: ใช้ตัวบันทึกใน backtest (ทดสอบ หรือสร้างข้อมูล tick ย้อนหลังให้งานวิจัย; ไม่มี DOM)
            var recordTo = live || RunningMode == RunningMode.Optimization ? null : Environment.GetEnvironmentVariable("BACKTESTLAB_RECORD");
            if ((live && RecordMarket) || !string.IsNullOrEmpty(recordTo))
            {
                try
                {
                    MarketDepth depth = null; Ticks history = null;
                    try { depth = MarketData.GetMarketDepth(SymbolName); } catch (Exception e) { Print("No market depth: {0}", e.Message); }
                    if (live) try { history = MarketData.GetTicks(); } catch (Exception e) { Print("No tick history: {0}", e.Message); }
                    _recorder = new LiveRecorder(this, depth, history, BookSeconds, live ? null : recordTo, _strategy.Code);
                    Positions.Closed += OnPositionClosed;
                    Timer.Start(1);
                }
                catch (Exception e) { _recorder = null; Print("Recorder could not start: {0}", e.Message); }
            }
        }

        /// <summary>
        /// รันสด: ถ้ามี Documents\BacktestLab\live.json ค่าในไฟล์มาก่อนค่าที่ตั้งบนหน้าจอ cTrader (ซึ่งค้างค่าเริ่มต้น EMA_CROSS ได้ง่าย)
        ///   { "strategy": "SCALP_S3D", "p1": 0.56, "p2": 5, "p3": 12, "p4": 1, "lots": 0.01, "placeOrders": true, "flatTime": 0 }
        /// ใส่เฉพาะช่องที่ต้องการกำหนด; ไฟล์เสีย = ไม่ส่งออเดอร์ (บันทึกอย่างเดียว) เพื่อไม่ให้ไปเทรดด้วยค่าที่ไม่ได้ตั้งใจ
        /// </summary>
        private void ApplyLiveSettings()
        {
            var file = System.IO.Path.Combine(Reporter.Folder, "live.json");
            if (!System.IO.File.Exists(file))
            {
                Print("No live.json: using the parameters set in cTrader (Strategy code = {0})", StrategyCode);
                return;
            }
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(file)))
                {
                    var root = doc.RootElement; System.Text.Json.JsonElement v;
                    if (root.TryGetProperty("strategy", out v)) StrategyCode = v.GetString();
                    if (root.TryGetProperty("p1", out v)) P1 = v.GetDouble();
                    if (root.TryGetProperty("p2", out v)) P2 = v.GetDouble();
                    if (root.TryGetProperty("p3", out v)) P3 = v.GetDouble();
                    if (root.TryGetProperty("p4", out v)) P4 = v.GetDouble();
                    if (root.TryGetProperty("lots", out v)) Lots = v.GetDouble();
                    if (root.TryGetProperty("placeOrders", out v)) PlaceOrders = v.GetBoolean();
                    if (root.TryGetProperty("flatTime", out v)) FlatTime = v.GetInt32();
                }
                Print("Live settings from live.json: {0}  P1 {1}  P2 {2}  P3 {3}  P4 {4}  lots {5}  place orders {6}", StrategyCode, P1, P2, P3, P4, Lots, PlaceOrders);
            }
            catch (Exception e)
            {
                PlaceOrders = false;
                Print("live.json could not be read ({0}): recording only, no orders", e.Message);
            }
        }

        protected override void OnTick()
        {
            _strategy.OnTick();
            if (_recorder != null) _recorder.OnTick();
        }

        protected override void OnTimer()
        {
            if (_recorder != null) _recorder.OnTimer();
        }

        protected override void OnBar()
        {
            _equity.Add(Server.Time, Account.Equity);

            var flat = InFlatWindow(Server.Time);
            foreach (var pos in Positions.FindAll(Label, SymbolName))
            {
                if (flat || _strategy.ShouldExit(pos)) Close(pos);
                else Manage(pos);
            }

            var signal = _strategy.Signal();   // เรียกทุกแท่ง เพื่อให้สถานะของทฤษฎีเดินต่อเนื่อง
            if (_recorder != null) _recorder.OnBar(signal, _strategy.Prediction, Positions.FindAll(Label, SymbolName).Length);
            if (signal == null || flat || !_canTrade) return;

            if (_strategy.ExitOnOppositeSignal)
                foreach (var pos in Positions.FindAll(Label, SymbolName).Where(p => p.TradeType != signal.Value))
                    Close(pos);

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
                tp = _strategy.RewardRisk > 0 ? Math.Round(sl.Value * _strategy.RewardRisk, 1) : (double?)null;   // RewardRisk <= 0 = ไม่มี TP
                _structureStops = true;
            }
            var expected = signal.Value == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = ExecuteMarketOrder(signal.Value, SymbolName, volume, Label, sl, tp, _strategy.SignalTag);
            clock.Stop();
            if (_recorder != null)
            {
                if (result.IsSuccessful && result.Position != null)
                    _recorder.OrderOpened(result.Position, expected, clock.Elapsed.TotalMilliseconds, _strategy.Prediction);
                else
                    _recorder.OrderFailed(signal.Value, result.Error.HasValue ? result.Error.Value.ToString() : "unknown");
            }
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

        /// <summary>ปิดออเดอร์ และจำราคาที่เห็นตอนสั่งไว้เทียบกับราคาที่ได้จริง (เฉพาะตอนบันทึกตลาดสด)</summary>
        private void Close(Position pos)
        {
            if (_recorder != null) _expectedClose[pos.Id] = pos.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;
            ClosePosition(pos);
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var p = args.Position;
            if (_recorder == null || p.Label != Label || p.SymbolName != SymbolName) return;

            double? expected = null; double seen;
            if (args.Reason == PositionCloseReason.StopLoss) expected = p.StopLoss;
            else if (args.Reason == PositionCloseReason.TakeProfit) expected = p.TakeProfit;
            else if (_expectedClose.TryGetValue(p.Id, out seen)) expected = seen;
            _expectedClose.Remove(p.Id);

            var deal = History.LastOrDefault(t => t.PositionId == p.Id);
            _recorder.OrderClosed(p, args.Reason.ToString(), expected, deal != null ? deal.ClosingPrice : (double?)null);
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
                if (profitR < plan.CutBelowR) { Close(pos); return true; }
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
            if (_recorder != null) _recorder.Stop();

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
                    note = Note(),
                },
                trades = TradeRows(trades),
                equity = _equity.Points,   // [[unixSeconds, equity], ...]
            };
        }

        /// <summary>โน้ตของรอบทดสอบ: ข้อความสรุปของทฤษฎี (ถ้ามี) ตามด้วยโน้ตที่ผู้รันใส่</summary>
        private string Note()
        {
            var parts = new[] { _strategy.ReportNote, RunNote }.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            return parts.Length == 0 ? null : string.Join(" — ", parts);
        }

        private List<Dictionary<string, object>> TradeRows(List<HistoricalTrade> trades)
        {
            var examples = PickExamples(trades);
            var bars = new Dictionary<int, List<double[]>>();
            int used = 0;
            foreach (var t in trades.Where(t => examples.Contains(t.PositionId)))
            {
                var b = TradeBars(t, ExampleBarsBefore, ExampleBarsAfter);
                if (b != null) { bars[t.PositionId] = b; used += b.Count; }
            }
            for (int k = trades.Count - 1; k >= 0 && used < MaxReportBars; k--)      // ใหม่สุดก่อน
            {
                if (bars.ContainsKey(trades[k].PositionId)) continue;
                var b = TradeBars(trades[k], TradeBarsBefore, TradeBarsAfter);
                if (b != null) { bars[trades[k].PositionId] = b; used += b.Count; }
            }

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
                List<double[]> tradeBars;
                if (bars.TryGetValue(t.PositionId, out tradeBars))
                {
                    row["bars"] = tradeBars;                // [[unixSeconds, open, high, low, close], ...]
                    if (examples.Contains(t.PositionId)) row["example"] = true;
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

        private List<double[]> TradeBars(HistoricalTrade t, int before, int after)
        {
            var entry = Bars.OpenTimes.GetIndexByTime(t.EntryTime);
            var exit = Bars.OpenTimes.GetIndexByTime(t.ClosingTime);
            if (entry < 0 || exit < 0) return null;

            var from = Math.Max(0, entry - before);
            var to = Math.Min(Bars.Count - 1, Math.Min(exit + after, from + ExampleMaxBars - 1));
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
