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

        [Parameter("Send results", DefaultValue = true, Group = "Report")]
        public bool SendResults { get; set; }

        [Parameter("Max equity points", DefaultValue = 5000, MinValue = 200, Group = "Report")]
        public int MaxEquityPoints { get; set; }

        [Parameter("Run note", DefaultValue = "", Group = "Report")]
        public string RunNote { get; set; }

        public const string Label = "BacktestLab";

        private StrategyBase _strategy;
        private EquityRecorder _equity;
        private DateTime _startTime;
        private double _startBalance;

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

            foreach (var pos in Positions.FindAll(Label, SymbolName))
                if (_strategy.ShouldExit(pos)) ClosePosition(pos);

            var signal = _strategy.Signal();
            if (signal == null) return;

            if (_strategy.ExitOnOppositeSignal)
                foreach (var pos in Positions.FindAll(Label, SymbolName).Where(p => p.TradeType != signal.Value))
                    ClosePosition(pos);

            if (Positions.FindAll(Label, SymbolName).Length > 0) return;

            var volume = Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(Lots));
            double? sl = StopLossPips > 0 ? StopLossPips : (double?)null;
            double? tp = TakeProfitPips > 0 ? TakeProfitPips : (double?)null;
            ExecuteMarketOrder(signal.Value, SymbolName, volume, Label, sl, tp);
        }

        protected override void OnStop()
        {
            if (_equity == null) return;
            _equity.Add(Server.Time, Account.Equity, force: true);

            if (RunningMode == RunningMode.RealTime || !SendResults) return;

            var openCount = Positions.FindAll(Label, SymbolName).Length;
            if (openCount > 0) Print("Note: {0} position(s) still open at the end are not counted as trades.", openCount);

            Reporter.Send(this, BuildReport());
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

            var parameters = new Dictionary<string, object>(_strategy.NamedParams())
            {
                ["Lots"] = Lots,
                ["StopLossPips"] = StopLossPips,
                ["TakeProfitPips"] = TakeProfitPips,
            };

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
                trades = trades.Select(t => new
                {
                    id = t.PositionId,
                    side = t.TradeType.ToString(),
                    entryTime = Iso(t.EntryTime),
                    exitTime = Iso(t.ClosingTime),
                    entryPrice = t.EntryPrice,
                    exitPrice = t.ClosingPrice,
                    volume = t.VolumeInUnits,
                    pips = Round(t.Pips),
                    gross = Round(t.GrossProfit),
                    commissions = Round(t.Commissions),
                    swap = Round(t.Swap),
                    net = Round(t.NetProfit),
                }).ToList(),
                equity = _equity.Points,   // [[unixSeconds, equity], ...]
            };
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
