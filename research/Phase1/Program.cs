// Phase 1A — "predict every minute, then compare with what happened", replayed on historical M1 bars.
//
// For every closed M1 bar T it builds features from data <= T only, and a walk-forward logistic model
// (train on the previous 12 months, predict the next month, roll month by month) gives P(up) for the next
// 1 / 3 / 5 / 15 / 60 minutes. Every prediction is therefore out-of-sample. The report answers:
//   * how often is the direction right, overall and per confidence bucket (calibration)?
//   * is the move it predicts bigger than the cost of trading it?
//   * does it beat naive baselines? is it stable across years, sessions and regimes?
//
// Input : Documents\BacktestLab\data\<symbol>_Minute.csv  (cBot strategy DATA_EXPORT: unix,open,high,low,close,volume)
// Output: console report + docs\data\phase1-<symbol>.json
// Run   : research\Phase1\bin\Release\net6.0\Phase1.exe [csvPath|-] [symbol] [fixedCost] [commissionPerMillionPerSide]
//         (build: set MSBuildEnableWorkloadResolver=false, then dotnet build -c Release research\Phase1)
//
// Limits: bars are bid prices, so there is no historical spread; cost is an assumption:
//   fixedCost (default 0.15 = raw spread 0.12 + slippage 0.03) + round-trip commission (default 30 USD per million per side, Pepperstone Razor) x price.
// No tick/order-book or news features — those need live logging (Phase 1B).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

static class Phase1
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly int[] Horizons = { 1, 3, 5, 15, 60 };
    static readonly double[] Edges = { 0.52, 0.54, 0.56, 0.58, 0.60, 0.65 };   // confidence buckets: [0.5,0.52) ... [0.65,1]
    const int TrainMonths = 12;
    const int Warmup = 1500;

    static long[] T; static double[] O, H, L, C, V;
    static double FixedCost, CommissionRate;
    static double Cost(int bar) => FixedCost + CommissionRate * C[bar];
    static string[] FeatureNames;
    static float[][] X;          // [feature][bar]
    static bool[] Valid;

    // ---- news / macro layer (tools\news-fetch.ps1 -> Documents\BacktestLab\data\news) ----
    static float[][] Active;     // feature columns the model may use in the current run (base only, or base + news)
    static int BaseCount;        // number of price/volume features; the columns after them are the news/macro features
    static bool HasNews;
    static long[] EvT; static byte[] EvLevel; static string[] EvCode;      // scheduled releases, UTC; level 0 medium, 1 high, 2 extreme
    static int[] MacroDay; static double[][] MacroV;                       // business days (unix day number) x series, forward filled
    static byte[] Window;        // where a bar sits relative to the nearest HIGH/EXTREME release
    static readonly string[] WindowNames = { "none", "before30", "after0to5", "after5to30", "after30to120" };

    static bool LoadNews(string docs)
    {
        var dir = Path.Combine(docs, "BacktestLab", "data", "news");
        var ev = Path.Combine(dir, "events.csv");
        if (!File.Exists(ev)) return false;
        var rows = File.ReadAllLines(ev).Skip(1).Select(l => l.Split(',')).Where(p => p.Length >= 3)
            .Select(p => (t: long.Parse(p[0], Inv), code: p[1], level: (byte)(p[2] == "EXTREME" ? 2 : p[2] == "HIGH" ? 1 : 0)))
            .OrderBy(r => r.t).ToArray();
        EvT = rows.Select(r => r.t).ToArray(); EvCode = rows.Select(r => r.code).ToArray(); EvLevel = rows.Select(r => r.level).ToArray();

        var macro = Path.Combine(dir, "macro.csv");
        if (File.Exists(macro))
        {
            var days = new List<int>(); var vals = new List<double[]>(); double[] last = null;
            foreach (var line in File.ReadAllLines(macro).Skip(1))
            {
                var p = line.Split(',');
                if (p.Length < 7 || p[2].Length == 0) continue;              // keep business days (10-year yield published)
                var v = new double[6];
                for (int s = 0; s < 6; s++) v[s] = p[s + 1].Length > 0 ? double.Parse(p[s + 1], Inv) : (last != null ? last[s] : double.NaN);
                var d = DateTime.ParseExact(p[0], "yyyy-MM-dd", Inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                days.Add((int)(new DateTimeOffset(d).ToUnixTimeSeconds() / 86400)); vals.Add(v); last = v;
            }
            MacroDay = days.ToArray(); MacroV = vals.ToArray();
        }
        return true;
    }

    /// <summary>How much bigger than usual is the 5-minute range right after each kind of release? (sanity check of the event times)</summary>
    static string EventStudy()
    {
        var parts = new List<string>();
        foreach (var g in Enumerable.Range(0, EvT.Length).GroupBy(j => EvCode[j]).OrderByDescending(g => EvLevel[g.First()]).ThenBy(g => g.Key))
        {
            double sum = 0; int n = 0;
            foreach (var j in g)
            {
                int i = LowerBound(EvT[j]);
                if (i < 70 || i + 5 >= T.Length || T[i] - EvT[j] > 120) continue;        // market closed at that time
                double hi = double.MinValue, lo = double.MaxValue;
                for (int q = i; q < i + 5; q++) { hi = Math.Max(hi, H[q]); lo = Math.Min(lo, L[q]); }
                double usual = 0;
                for (int w = 1; w <= 12; w++)
                {
                    double a = double.MinValue, b = double.MaxValue;
                    for (int q = i - 5 * w; q < i - 5 * w + 5; q++) { a = Math.Max(a, H[q]); b = Math.Min(b, L[q]); }
                    usual += a - b;
                }
                if (usual > 0) { sum += (hi - lo) / (usual / 12); n++; }
            }
            var level = EvLevel[g.First()] == 2 ? "EXTREME" : EvLevel[g.First()] == 1 ? "HIGH" : "MEDIUM";
            Console.WriteLine($"  {g.Key,-7} {level,-8} events in data {n,4}   5-min range after release = {(n > 0 ? sum / n : 0):N2} x the usual 5-min range of the hour before");
            parts.Add("{\"code\":\"" + g.Key + "\",\"impact\":\"" + level + "\",\"n\":" + n + ",\"rangeRatio\":" + F(n > 0 ? sum / n : double.NaN) + "}");
        }
        return string.Join(",", parts);
    }

    static int Main(string[] args)
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var symbol = args.Length > 1 ? args[1] : "XAUUSD";
        var csv = args.Length > 0 && args[0] != "-" ? args[0] : Path.Combine(docs, "BacktestLab", "data", symbol + "_Minute.csv");
        FixedCost = args.Length > 2 ? double.Parse(args[2], Inv) : 0.15;
        CommissionRate = 2 * (args.Length > 3 ? double.Parse(args[3], Inv) : 30) / 1e6;
        var cost = FixedCost;
        if (!File.Exists(csv)) { Console.Error.WriteLine("not found: " + csv); return 1; }

        Load(csv);
        Console.WriteLine($"bars: {T.Length:N0}  {Date(T[0])} .. {Date(T[T.Length - 1])}   cost per trade: {FixedCost} + {CommissionRate * 1e6:N0} per million round trip (= {Cost(0):N3} at {C[0]:N0}, {Cost(T.Length - 1):N3} at {C[T.Length - 1]:N0})");
        HasNews = LoadNews(docs);
        BuildFeatures();
        Console.WriteLine($"features: {BaseCount} price/volume" + (HasNews ? $" + {FeatureNames.Length - BaseCount} news/macro ({EvT.Length} scheduled releases, {MacroDay?.Length ?? 0} macro days)" : "  (no news cache: run tools\\news-fetch.ps1)"));
        var eventJson = "";
        if (HasNews) { Console.WriteLine("release-time check:"); eventJson = EventStudy(); }

        var months = MonthStarts();
        var json = new StringBuilder();
        json.Append("{\"symbol\":\"").Append(symbol).Append("\",\"fixedCost\":").Append(F(FixedCost)).Append(",\"commissionRoundTripPerMillion\":").Append(F(CommissionRate * 1e6))
            .Append(",\"trainMonths\":").Append(TrainMonths)
            .Append(",\"from\":\"").Append(Date(months[TrainMonths])).Append("\",\"to\":\"").Append(Date(T[T.Length - 1]))
            .Append("\",\"features\":[").Append(string.Join(",", FeatureNames.Select(n => "\"" + n + "\""))).Append("],\"baseFeatures\":").Append(BaseCount)
            .Append(",\"hasNews\":").Append(HasNews ? "true" : "false").Append(",\"events\":[").Append(eventJson).Append("],\"horizons\":[");

        var first = true;
        foreach (var h in Horizons)
        {
            // same walk-forward twice: price/volume features only, then with the news/macro layer added
            Active = X.Take(BaseCount).ToArray();
            var baseRes = WalkForward(h, months);
            var res = baseRes;
            if (HasNews) { Active = X; res = WalkForward(h, months); }
            if (!first) json.Append(',');
            first = false;
            Report(h, res, cost, json, HasNews ? baseRes : null);
        }
        json.Append("]}");

        var repo = FindRepo();
        if (repo != null)
        {
            var outFile = Path.Combine(repo, "docs", "data", "phase1-" + symbol + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(outFile));
            File.WriteAllText(outFile, json.ToString());
            Console.WriteLine("\nwrote " + outFile);
        }
        return 0;
    }

    // ------------------------------------------------------------------ data

    static void Load(string path)
    {
        var t = new List<long>(); var o = new List<double>(); var h = new List<double>(); var l = new List<double>(); var c = new List<double>(); var v = new List<double>();
        using (var r = new StreamReader(path))
        {
            r.ReadLine(); string line;
            while ((line = r.ReadLine()) != null)
            {
                var p = line.Split(',');
                if (p.Length < 6) continue;
                t.Add(long.Parse(p[0], Inv)); o.Add(double.Parse(p[1], Inv)); h.Add(double.Parse(p[2], Inv));
                l.Add(double.Parse(p[3], Inv)); c.Add(double.Parse(p[4], Inv)); v.Add(double.Parse(p[5], Inv));
            }
        }
        T = t.ToArray(); O = o.ToArray(); H = h.ToArray(); L = l.ToArray(); C = c.ToArray(); V = v.ToArray();
    }

    static string Date(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd", Inv);
    static string F(double v) => double.IsNaN(v) || double.IsInfinity(v) ? "null" : Math.Round(v, 5).ToString(Inv);

    static string FindRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "docs"))) d = d.Parent;
        return d?.FullName;
    }

    /// <summary>Unix time of the first bar of every calendar month (UTC), plus an end sentinel.</summary>
    static List<long> MonthStarts()
    {
        var list = new List<long>(); int last = -1;
        for (int i = 0; i < T.Length; i++)
        {
            var d = DateTimeOffset.FromUnixTimeSeconds(T[i]).UtcDateTime;
            var key = d.Year * 12 + d.Month;
            if (key != last) { list.Add(new DateTimeOffset(d.Year, d.Month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()); last = key; }
        }
        list.Add(long.MaxValue);
        return list;
    }

    // ------------------------------------------------------------------ features (data <= T only)

    static double[] Ema(double[] s, int n)
    {
        var e = new double[s.Length]; double k = 2.0 / (n + 1); e[0] = s[0];
        for (int i = 1; i < s.Length; i++) e[i] = e[i - 1] + k * (s[i] - e[i - 1]);
        return e;
    }

    static double[] RollMean(double[] s, int n)
    {
        var m = new double[s.Length]; double sum = 0;
        for (int i = 0; i < s.Length; i++) { sum += s[i]; if (i >= n) sum -= s[i - n]; m[i] = sum / Math.Min(i + 1, n); }
        return m;
    }

    static double[] RollStd(double[] s, double[] mean, int n)
    {
        var sq = RollMean(s.Select(x => x * x).ToArray(), n); var r = new double[s.Length];
        for (int i = 0; i < s.Length; i++) r[i] = Math.Sqrt(Math.Max(0, sq[i] - mean[i] * mean[i]));
        return r;
    }

    static void BuildFeatures()
    {
        int n = T.Length;
        var tr = new double[n];
        for (int i = 0; i < n; i++) tr[i] = i == 0 ? H[i] - L[i] : Math.Max(H[i], C[i - 1]) - Math.Min(L[i], C[i - 1]);
        var atr60 = RollMean(tr, 60); var atr1440 = RollMean(tr, 1440);
        var ema20 = Ema(C, 20); var ema60 = Ema(C, 60); var ema200 = Ema(C, 200);
        var mean60 = RollMean(C, 60); var std60 = RollStd(C, mean60, 60);
        var vMean = RollMean(V, 60); var vStd = RollStd(V, vMean, 60);
        var absChg = new double[n]; for (int i = 1; i < n; i++) absChg[i] = Math.Abs(C[i] - C[i - 1]);
        var path30 = RollMean(absChg, 30);

        // RSI(14), Wilder
        var rsi = new double[n]; double ag = 0, al = 0;
        for (int i = 1; i < n; i++)
        {
            double ch = C[i] - C[i - 1], g = Math.Max(ch, 0), lo = Math.Max(-ch, 0);
            ag = (ag * 13 + g) / 14; al = (al * 13 + lo) / 14;
            rsi[i] = al == 0 ? 100 : 100 - 100 / (1 + ag / al);
        }

        // per-UTC-day running state: session high/low/VWAP, previous day's high/low
        var dayHigh = new double[n]; var dayLow = new double[n]; var vwap = new double[n]; var pdh = new double[n]; var pdl = new double[n];
        long curDay = -1; double dh = 0, dl = 0, pv = 0, vv = 0, prevH = double.NaN, prevL = double.NaN;
        for (int i = 0; i < n; i++)
        {
            long day = T[i] / 86400; int wd = (int)((day + 4) % 7); if (wd == 0) day++;      // Sunday evening belongs to Monday
            if (day != curDay) { if (curDay >= 0) { prevH = dh; prevL = dl; } curDay = day; dh = H[i]; dl = L[i]; pv = 0; vv = 0; }
            dh = Math.Max(dh, H[i]); dl = Math.Min(dl, L[i]);
            double typical = (H[i] + L[i] + C[i]) / 3; pv += typical * V[i]; vv += V[i];
            dayHigh[i] = dh; dayLow[i] = dl; vwap[i] = vv > 0 ? pv / vv : C[i]; pdh[i] = prevH; pdl[i] = prevL;
        }

        // rolling 60-bar high/low of the bars BEFORE i (for sweep detection) and including i
        var hi60 = new double[n]; var lo60 = new double[n]; var hiPrev = new double[n]; var loPrev = new double[n];
        for (int i = 0; i < n; i++)
        {
            double a = double.MinValue, b = double.MaxValue;
            for (int j = Math.Max(0, i - 60); j < i; j++) { if (H[j] > a) a = H[j]; if (L[j] < b) b = L[j]; }
            hiPrev[i] = a; loPrev[i] = b; hi60[i] = Math.Max(a, H[i]); lo60[i] = Math.Min(b, L[i]);
        }

        var names = new List<string>(); var cols = new List<float[]>();
        void Add(string name, Func<int, double> f)
        {
            var col = new float[n];
            for (int i = Warmup; i < n; i++) { var x = f(i); col[i] = double.IsNaN(x) || double.IsInfinity(x) ? 0f : (float)Math.Max(-50, Math.Min(50, x)); }
            names.Add(name); cols.Add(col);
        }
        double U(int i) => Math.Max(atr60[i], 1e-6);
        double Ret(int i, int k) => (C[i] - C[i - k]) / U(i);
        double Rng(int i) => Math.Max(H[i] - L[i], 1e-9);

        Add("ret1", i => Ret(i, 1)); Add("ret3", i => Ret(i, 3)); Add("ret5", i => Ret(i, 5)); Add("ret15", i => Ret(i, 15)); Add("ret60", i => Ret(i, 60));
        Add("body", i => (C[i] - O[i]) / Rng(i));
        Add("upperWick", i => (H[i] - Math.Max(O[i], C[i])) / Rng(i));
        Add("lowerWick", i => (Math.Min(O[i], C[i]) - L[i]) / Rng(i));
        Add("range", i => (H[i] - L[i]) / U(i));
        Add("volZ", i => vStd[i] > 0 ? (V[i] - vMean[i]) / vStd[i] : 0);
        Add("volRatio", i => Math.Log(Math.Max(V[i], 1) / Math.Max(vMean[i], 1)));
        Add("distEma20", i => (C[i] - ema20[i]) / U(i)); Add("distEma60", i => (C[i] - ema60[i]) / U(i)); Add("distEma200", i => (C[i] - ema200[i]) / U(i));
        Add("slopeEma20", i => (ema20[i] - ema20[i - 10]) / U(i)); Add("slopeEma60", i => (ema60[i] - ema60[i - 30]) / U(i));
        Add("distVwap", i => (C[i] - vwap[i]) / U(i));
        Add("z60", i => std60[i] > 0 ? (C[i] - mean60[i]) / std60[i] : 0);
        Add("rsi14", i => (rsi[i] - 50) / 50);
        Add("atrRatio", i => Math.Log(Math.Max(atr60[i], 1e-6) / Math.Max(atr1440[i], 1e-6)));
        Add("distDayHigh", i => (C[i] - dayHigh[i]) / U(i)); Add("distDayLow", i => (C[i] - dayLow[i]) / U(i));
        Add("distPrevDayHigh", i => (C[i] - pdh[i]) / U(i)); Add("distPrevDayLow", i => (C[i] - pdl[i]) / U(i));
        Add("distHigh60", i => (C[i] - hi60[i]) / U(i)); Add("distLow60", i => (C[i] - lo60[i]) / U(i));
        Add("sweepHigh", i => H[i] > hiPrev[i] && C[i] < hiPrev[i] ? 1 : 0);
        Add("sweepLow", i => L[i] < loPrev[i] && C[i] > loPrev[i] ? 1 : 0);
        Add("breakHigh", i => C[i] > hiPrev[i] ? 1 : 0);
        Add("breakLow", i => C[i] < loPrev[i] ? 1 : 0);
        Add("hourSin", i => Math.Sin(2 * Math.PI * (T[i] % 86400) / 86400.0)); Add("hourCos", i => Math.Cos(2 * Math.PI * (T[i] % 86400) / 86400.0));
        Add("efficiency30", i => path30[i] > 0 ? (C[i] - C[i - 30]) / (path30[i] * 30) : 0);
        Add("ret1xVol", i => Ret(i, 1) * (vStd[i] > 0 ? (V[i] - vMean[i]) / vStd[i] : 0));
        Add("streak", i => { int s = 0, d = Math.Sign(C[i] - O[i]); if (d == 0) return 0; for (int j = i; j > i - 10 && Math.Sign(C[j] - O[j]) == d; j--) s++; return d * s; });

        // ---- news / macro layer: only information that was known at bar close ----
        BaseCount = names.Count;
        Window = new byte[n];
        if (HasNews)
        {
            // minutes from bar close to the next scheduled release / since the last one; [0] = HIGH + EXTREME, [1] = EXTREME only
            var toNext = new float[2][]; var since = new float[2][];
            for (int c = 0; c < 2; c++)
            {
                toNext[c] = new float[n]; since[c] = new float[n];
                var ev = Enumerable.Range(0, EvT.Length).Where(j => EvLevel[j] >= c + 1).Select(j => EvT[j]).ToArray();
                int e = 0;
                for (int i = 0; i < n; i++)
                {
                    long now = T[i] + 60;
                    while (e < ev.Length && ev[e] < now) e++;
                    toNext[c][i] = e < ev.Length ? Math.Min(100000f, (ev[e] - now) / 60f) : 100000f;
                    since[c][i] = e > 0 ? Math.Min(100000f, (now - ev[e - 1]) / 60f) : 100000f;
                }
            }
            double Near(float minutes, double span) => Math.Max(0, 1 - minutes / span);
            Add("newsAhead30", i => Near(toNext[0][i], 30)); Add("newsAhead120", i => Near(toNext[0][i], 120));
            Add("newsAfter15", i => Near(since[0][i], 15)); Add("newsAfter120", i => Near(since[0][i], 120));
            Add("extremeAhead120", i => Near(toNext[1][i], 120)); Add("extremeAfter120", i => Near(since[1][i], 120));
            Add("extremeToday", i => toNext[1][i] <= 720 ? 1 : 0);
            Add("newsAfterXret5", i => Near(since[0][i], 120) * Ret(i, 5)); Add("newsAfterXret15", i => Near(since[0][i], 120) * Ret(i, 15));
            for (int i = 0; i < n; i++)
            {
                float a = toNext[0][i], s = since[0][i];
                Window[i] = (byte)(s < 5 ? 2 : s < 30 ? 3 : a <= 30 ? 1 : s < 120 ? 4 : 0);
            }

            if (MacroDay != null)
            {
                // a daily value is used from two calendar days after its date (FRED publishes yields the next business day)
                var mi = new int[n]; int m = -1;
                for (int i = 0; i < n; i++)
                {
                    int day = (int)(T[i] / 86400) - 2;
                    while (m + 1 < MacroDay.Length && MacroDay[m + 1] <= day) m++;
                    mi[i] = m;
                }
                double Chg(int i, int s, int back) => mi[i] >= back ? MacroV[mi[i]][s] - MacroV[mi[i] - back][s] : 0;
                double Pct(int i, int s, int back) => mi[i] >= back && MacroV[mi[i] - back][s] > 0 ? 100 * (MacroV[mi[i]][s] / MacroV[mi[i] - back][s] - 1) : 0;
                Add("realYieldChg1", i => Chg(i, 0, 1)); Add("realYieldChg5", i => Chg(i, 0, 5));
                Add("yield10Chg1", i => Chg(i, 1, 1)); Add("yield10Chg5", i => Chg(i, 1, 5));
                Add("curve10y2y", i => mi[i] >= 0 ? MacroV[mi[i]][1] - MacroV[mi[i]][2] : 0);
                Add("breakevenChg5", i => Chg(i, 3, 5));
                Add("dollarChg1", i => Pct(i, 4, 1)); Add("dollarChg5", i => Pct(i, 4, 5));
            }
        }

        FeatureNames = names.ToArray(); X = cols.ToArray();
        Valid = new bool[n];
        for (int i = Warmup; i < n; i++) Valid[i] = !double.IsNaN(pdh[i]) && atr60[i] > 0;

        // regime tags used only for the report (same information as the features)
        Regime = new byte[n]; Session = new byte[n];
        for (int i = Warmup; i < n; i++)
        {
            double eff = path30[i] > 0 ? Math.Abs(C[i] - C[i - 30]) / (path30[i] * 30) : 0;
            double vol = atr60[i] / Math.Max(atr1440[i], 1e-6);
            Regime[i] = (byte)(vol > 1.6 ? 2 : eff > 0.3 ? 1 : 0);                       // 0 ranging, 1 trending, 2 high volatility
            int hour = (int)(T[i] % 86400 / 3600);
            Session[i] = (byte)(hour < 7 ? 0 : hour < 13 ? 1 : hour < 21 ? 2 : 3);       // Asia, London, New York, late
        }
    }

    static byte[] Regime, Session;
    static readonly string[] RegimeNames = { "ranging", "trending", "highVol" };
    static readonly string[] SessionNames = { "asia", "london", "newYork", "late" };

    // ------------------------------------------------------------------ walk-forward logistic regression

    class Pred { public int Bar; public float P; public float Move; }      // P = P(up); Move = close[T+h] - close[T]

    static List<Pred> WalkForward(int h, List<long> months)
    {
        var X = Active;                                                     // the feature set of this run
        int n = T.Length, k = X.Length;
        // usable sample: features valid and the bar h steps ahead is exactly h minutes later (no gap in between)
        var move = new float[n]; var ok = new bool[n];
        for (int i = Warmup; i + h < n; i++)
            if (Valid[i] && T[i + h] - T[i] == h * 60L) { ok[i] = true; move[i] = (float)(C[i + h] - C[i]); }

        var folds = new List<Pred>[months.Count];
        Parallel.For(TrainMonths, months.Count - 1, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, m =>
        {
            long trainFrom = months[m - TrainMonths], testFrom = months[m], testTo = months[m + 1];
            int a = LowerBound(trainFrom), b = LowerBound(testFrom), c = testTo == long.MaxValue ? n : LowerBound(testTo);

            var train = new List<int>(b - a);
            for (int i = a; i < b - h; i++) if (ok[i] && move[i] != 0) train.Add(i);    // b - h: labels must end before the test month starts
            if (train.Count < 20000) { folds[m] = new List<Pred>(); return; }

            var mean = new double[k]; var std = new double[k];
            for (int f = 0; f < k; f++)
            {
                double s = 0, s2 = 0; var col = X[f];
                foreach (var i in train) { s += col[i]; s2 += (double)col[i] * col[i]; }
                mean[f] = s / train.Count; std[f] = Math.Sqrt(Math.Max(1e-12, s2 / train.Count - mean[f] * mean[f]));
            }

            // mini-batch SGD with momentum, L2
            var w = new double[k]; var vel = new double[k]; double bias = 0, vb = 0;
            var rnd = new Random(1234 + m); var order = train.ToArray();
            const int Batch = 2048; const double L2 = 1e-4, Mom = 0.9;
            var grad = new double[k]; var x = new double[k];
            for (int epoch = 0; epoch < 4; epoch++)
            {
                for (int i = order.Length - 1; i > 0; i--) { int j = rnd.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
                double lr = 0.05 / (1 + epoch);
                for (int s = 0; s < order.Length; s += Batch)
                {
                    Array.Clear(grad, 0, k); double gb = 0; int cnt = Math.Min(Batch, order.Length - s);
                    for (int q = s; q < s + cnt; q++)
                    {
                        int i = order[q]; double z = bias;
                        for (int f = 0; f < k; f++) { double xv = (X[f][i] - mean[f]) / std[f]; xv = xv > 5 ? 5 : xv < -5 ? -5 : xv; x[f] = xv; z += w[f] * xv; }
                        double err = 1 / (1 + Math.Exp(-z)) - (move[i] > 0 ? 1 : 0);
                        for (int f = 0; f < k; f++) grad[f] += err * x[f];
                        gb += err;
                    }
                    for (int f = 0; f < k; f++) { vel[f] = Mom * vel[f] - lr * (grad[f] / cnt + L2 * w[f]); w[f] += vel[f]; }
                    vb = Mom * vb - lr * gb / cnt; bias += vb;
                }
            }

            var list = new List<Pred>(c - b);
            for (int i = b; i < c; i++)
            {
                if (!ok[i]) continue;
                double z = bias;
                for (int f = 0; f < k; f++) { double xv = (X[f][i] - mean[f]) / std[f]; xv = xv > 5 ? 5 : xv < -5 ? -5 : xv; z += w[f] * xv; }
                list.Add(new Pred { Bar = i, P = (float)(1 / (1 + Math.Exp(-z))), Move = move[i] });
            }
            folds[m] = list;
        });

        var all = new List<Pred>();
        foreach (var f in folds) if (f != null) all.AddRange(f);
        return all;
    }

    static int LowerBound(long unix)
    {
        int i = Array.BinarySearch(T, unix);
        return i >= 0 ? i : ~i;
    }

    // ------------------------------------------------------------------ report

    class Acc
    {
        public long N, Right, Wrong; public double Signed, Abs, Brier, SumP, CostSum;
        public void Add(Pred p)
        {
            N++; bool up = p.P >= 0.5; double conf = up ? p.P : 1 - p.P; CostSum += Cost(p.Bar);
            if (p.Move != 0) { if ((p.Move > 0) == up) Right++; else Wrong++; }
            Signed += up ? p.Move : -p.Move; Abs += Math.Abs(p.Move); SumP += conf;
            double y = p.Move > 0 ? 1 : 0; Brier += (p.P - y) * (p.P - y);
        }
        public double Accuracy => Right + Wrong > 0 ? 100.0 * Right / (Right + Wrong) : double.NaN;
        public double AvgSigned => N > 0 ? Signed / N : double.NaN;
        public double AvgAbs => N > 0 ? Abs / N : double.NaN;
        public double AvgConf => N > 0 ? 100 * SumP / N : double.NaN;
        public double AvgCost => N > 0 ? CostSum / N : double.NaN;
        public double Ev => AvgSigned - AvgCost;
        public string Json(double unused) =>
            $"{{\"n\":{N},\"accuracy\":{F(Accuracy)},\"avgConfidence\":{F(AvgConf)},\"avgMove\":{F(AvgSigned)},\"avgAbsMove\":{F(AvgAbs)},\"avgCost\":{F(AvgCost)},\"evAfterCost\":{F(Ev)},\"brier\":{F(N > 0 ? Brier / N : double.NaN)}}}";
    }

    static int Bucket(float p) { double c = p >= 0.5 ? p : 1 - p; int b = 0; while (b < Edges.Length && c >= Edges[b]) b++; return b; }
    static string BucketName(int b) => b == 0 ? "50-52" : b == Edges.Length ? $"{Edges[b - 1] * 100:0}+" : $"{Edges[b - 1] * 100:0}-{Edges[b] * 100:0}";

    /// <summary>Non-overlapping trades: enter at confidence >= th when the bar's news window is allowed, hold h minutes. Returns n, wins, grossWin, grossLoss.</summary>
    static double[] Sim(List<Pred> preds, int h, double th, Func<byte, bool> allow)
    {
        var a = new double[4]; long freeAt = 0;
        foreach (var p in preds)
        {
            double conf = p.P >= 0.5 ? p.P : 1 - p.P;
            if (conf < th || T[p.Bar] < freeAt || !allow(Window[p.Bar])) continue;
            freeAt = T[p.Bar] + h * 60L;
            double pnl = (p.P >= 0.5 ? p.Move : -p.Move) - Cost(p.Bar);
            a[0]++; if (pnl > 0) { a[1]++; a[2] += pnl; } else a[3] -= pnl;
        }
        return a;
    }
    static string SimJson(double[] a) => "{\"n\":" + a[0] + ",\"wins\":" + a[1] + ",\"grossWin\":" + F(a[2]) + ",\"grossLoss\":" + F(a[3]) + "}";
    static string SimLine(double[] a) => a[0] == 0 ? "n 0" : $"n {a[0]:N0} win {100 * a[1] / a[0]:N1}% PF {(a[3] > 0 ? a[2] / a[3] : 0):N2} net {a[2] - a[3]:N0}";

    static void Report(int h, List<Pred> preds, double cost, StringBuilder json, List<Pred> basePreds = null)
    {
        int nb = Edges.Length + 1;
        var all = new Acc(); var buckets = Enumerable.Range(0, nb).Select(_ => new Acc()).ToArray();
        var byYear = new SortedDictionary<int, Acc>(); var byYearHi = new SortedDictionary<int, Acc>();
        var byReg = RegimeNames.Select(_ => new Acc()).ToArray(); var byRegHi = RegimeNames.Select(_ => new Acc()).ToArray();
        var bySes = SessionNames.Select(_ => new Acc()).ToArray(); var bySesHi = SessionNames.Select(_ => new Acc()).ToArray();
        long up = 0, decided = 0, prevRight = 0, maRight = 0;
        var aboveEma = X[Array.IndexOf(FeatureNames, "distEma20")];

        foreach (var p in preds)
        {
            all.Add(p); int b = Bucket(p.P); buckets[b].Add(p);
            int year = DateTimeOffset.FromUnixTimeSeconds(T[p.Bar]).UtcDateTime.Year;
            if (!byYear.TryGetValue(year, out var ya)) { byYear[year] = ya = new Acc(); byYearHi[year] = new Acc(); }
            ya.Add(p); byReg[Regime[p.Bar]].Add(p); bySes[Session[p.Bar]].Add(p);
            if (b >= 3) { byYearHi[year].Add(p); byRegHi[Regime[p.Bar]].Add(p); bySesHi[Session[p.Bar]].Add(p); }   // confidence >= 56%

            if (p.Move != 0)
            {
                decided++; bool wentUp = p.Move > 0; if (wentUp) up++;
                int i = p.Bar; double last = C[i] - C[i - 1];
                if (last != 0 && (last > 0) == wentUp) prevRight++;
                if ((aboveEma[i] > 0) == wentUp) maRight++;
            }
        }
        double upShare = 100.0 * up / Math.Max(1, decided);
        double prevAcc = 100.0 * prevRight / Math.Max(1, decided), maAcc = 100.0 * maRight / Math.Max(1, decided);

        Console.WriteLine($"\n================ horizon {h} min   predictions {all.N:N0} ================");
        Console.WriteLine($"model accuracy {all.Accuracy:N2}%   brier {all.Brier / Math.Max(1, all.N):N4} (0.25 = coin)   avg |move| {all.AvgAbs:N3}   avg cost {all.AvgCost:N3}");
        Console.WriteLine($"baselines: always-up {upShare:N2}%  always-down {100 - upShare:N2}%  same-as-last-candle {prevAcc:N2}%  opposite-of-last-candle {100 - prevAcc:N2}%  above-EMA20 {maAcc:N2}%");
        Console.WriteLine("confidence        n   share  accuracy  claimed   avg move  avg|move|  EV after cost");
        for (int b = 0; b < nb; b++)
        {
            var a = buckets[b]; if (a.N == 0) continue;
            Console.WriteLine($"  {BucketName(b),-8} {a.N,9:N0} {100.0 * a.N / all.N,6:N2}%  {a.Accuracy,7:N2}%  {a.AvgConf,6:N1}%  {a.AvgSigned,9:N4}  {a.AvgAbs,8:N3}  {a.Ev,9:N4}");
        }
        Console.WriteLine("by year (all / confidence>=56%):");
        foreach (var kv in byYear)
        {
            var hi = byYearHi[kv.Key];
            Console.WriteLine($"  {kv.Key}  acc {kv.Value.Accuracy,6:N2}%  |move| {kv.Value.AvgAbs,6:N3}   >=56%: n {hi.N,8:N0}  acc {hi.Accuracy,6:N2}%  avg move {hi.AvgSigned,8:N4}  EV {hi.Ev,8:N4}");
        }
        Console.WriteLine("by regime / session (confidence>=56%):");
        for (int r = 0; r < RegimeNames.Length; r++) Console.WriteLine($"  {RegimeNames[r],-9} all acc {byReg[r].Accuracy,6:N2}%   >=56%: n {byRegHi[r].N,8:N0}  acc {byRegHi[r].Accuracy,6:N2}%  avg move {byRegHi[r].AvgSigned,8:N4}  EV {byRegHi[r].Ev,8:N4}");
        for (int s = 0; s < SessionNames.Length; s++) Console.WriteLine($"  {SessionNames[s],-9} all acc {bySes[s].Accuracy,6:N2}%   >=56%: n {bySesHi[s].N,8:N0}  acc {bySesHi[s].Accuracy,6:N2}%  avg move {bySesHi[s].AvgSigned,8:N4}  EV {bySesHi[s].Ev,8:N4}");

        var top = new SortedDictionary<int, Acc>();
        foreach (var p in preds)
        {
            if (Bucket(p.P) < 5) continue;                                   // confidence >= 60%
            int year = DateTimeOffset.FromUnixTimeSeconds(T[p.Bar]).UtcDateTime.Year;
            if (!top.TryGetValue(year, out var ta)) top[year] = ta = new Acc();
            ta.Add(p);
        }
        Console.WriteLine("confidence >= 60% by year:  " + string.Join("  ", top.Select(kv => $"{kv.Key}: n {kv.Value.N:N0} acc {kv.Value.Accuracy:N1}% EV {kv.Value.Ev:N3}")));

        // Trade simulation without overlap: enter when confidence >= threshold, hold h minutes, then look again.
        Console.WriteLine("non-overlapping trades (enter at confidence >= threshold, hold " + h + " min, then look again):");
        var sims = new List<string>();
        foreach (var th in new[] { 0.56, 0.58, 0.60, 0.62 })
        {
            var years = new SortedDictionary<int, double[]>();               // n, wins, grossWin, grossLoss
            long freeAt = 0; double[] tot = new double[4];
            foreach (var p in preds)
            {
                double conf = p.P >= 0.5 ? p.P : 1 - p.P;
                if (conf < th || T[p.Bar] < freeAt) continue;
                freeAt = T[p.Bar] + h * 60L;
                double pnl = (p.P >= 0.5 ? p.Move : -p.Move) - Cost(p.Bar);
                int year = DateTimeOffset.FromUnixTimeSeconds(T[p.Bar]).UtcDateTime.Year;
                if (!years.TryGetValue(year, out var y)) years[year] = y = new double[4];
                foreach (var a in new[] { y, tot }) { a[0]++; if (pnl > 0) { a[1]++; a[2] += pnl; } else a[3] -= pnl; }
            }
            string Line(double[] a) => a[0] == 0 ? "n 0" : $"n {a[0]:N0} win {100 * a[1] / a[0]:N1}% PF {(a[3] > 0 ? a[2] / a[3] : 0):N2} net {a[2] - a[3]:N0}";
            Console.WriteLine($"  >= {th * 100:0}%: {Line(tot)}   | " + string.Join(" | ", years.Select(kv => $"{kv.Key % 100}: {Line(kv.Value)}")));
            sims.Add("{\"threshold\":" + F(th) + ",\"n\":" + tot[0] + ",\"wins\":" + tot[1] + ",\"grossWin\":" + F(tot[2]) + ",\"grossLoss\":" + F(tot[3]) + ",\"years\":[" +
                string.Join(",", years.Select(kv => "{\"year\":" + kv.Key + ",\"n\":" + kv.Value[0] + ",\"wins\":" + kv.Value[1] + ",\"grossWin\":" + F(kv.Value[2]) + ",\"grossLoss\":" + F(kv.Value[3]) + "}")) + "]}");
        }

        json.Append("{\"minutes\":").Append(h).Append(",\"trades\":[").Append(string.Join(",", sims)).Append("],\"all\":").Append(all.Json(cost))
            .Append(",\"baselines\":{\"alwaysUp\":").Append(F(upShare)).Append(",\"sameAsLastCandle\":").Append(F(prevAcc))
            .Append(",\"oppositeOfLastCandle\":").Append(F(100 - prevAcc)).Append(",\"aboveEma20\":").Append(F(maAcc)).Append("},\"buckets\":[");
        json.Append(string.Join(",", Enumerable.Range(0, nb).Select(b => "{\"range\":\"" + BucketName(b) + "\",\"stats\":" + buckets[b].Json(cost) + "}")));
        json.Append("],\"years\":[").Append(string.Join(",", byYear.Select(kv => "{\"year\":" + kv.Key + ",\"all\":" + kv.Value.Json(cost) + ",\"confident\":" + byYearHi[kv.Key].Json(cost) + "}")));
        json.Append("],\"regimes\":[").Append(string.Join(",", RegimeNames.Select((nm, r) => "{\"name\":\"" + nm + "\",\"all\":" + byReg[r].Json(cost) + ",\"confident\":" + byRegHi[r].Json(cost) + "}")));
        json.Append("],\"sessions\":[").Append(string.Join(",", SessionNames.Select((nm, s) => "{\"name\":\"" + nm + "\",\"all\":" + bySes[s].Json(cost) + ",\"confident\":" + bySesHi[s].Json(cost) + "}")));
        json.Append("]");

        if (basePreds != null)
        {
            // 1) does the news layer make the model better? same walk-forward with and without it
            var b = new Acc(); foreach (var p in basePreds) b.Add(p);
            Console.WriteLine($"news layer: accuracy {b.Accuracy:N2}% -> {all.Accuracy:N2}%   brier {b.Brier / Math.Max(1, b.N):N5} -> {all.Brier / Math.Max(1, all.N):N5}");
            var ths = new[] { 0.56, 0.58, 0.60, 0.62 };
            foreach (var th in ths)
                Console.WriteLine($"  trades >= {th * 100:0}%:  without news [{SimLine(Sim(basePreds, h, th, w => true))}]   with news [{SimLine(Sim(preds, h, th, w => true))}]");

            // 2) how does the model do around releases, and what if we skip / only trade those windows?
            var win = WindowNames.Select(_ => new Acc()).ToArray(); var winHi = WindowNames.Select(_ => new Acc()).ToArray();
            foreach (var p in preds) { win[Window[p.Bar]].Add(p); if (Bucket(p.P) >= 3) winHi[Window[p.Bar]].Add(p); }
            Console.WriteLine("around HIGH/EXTREME releases (model with news):");
            for (int w = 0; w < WindowNames.Length; w++)
                Console.WriteLine($"  {WindowNames[w],-13} n {win[w].N,9:N0}  acc {win[w].Accuracy,6:N2}%  |move| {win[w].AvgAbs,6:N3}   >=56%: n {winHi[w].N,7:N0} acc {winHi[w].Accuracy,6:N2}% EV {winHi[w].Ev,8:N4}");
            foreach (var th in ths)
                Console.WriteLine($"  trades >= {th * 100:0}%:  skip news windows [{SimLine(Sim(preds, h, th, w => w == 0))}]   only after a release (0-120m) [{SimLine(Sim(preds, h, th, w => w >= 2))}]   only before (30m) [{SimLine(Sim(preds, h, th, w => w == 1))}]");

            json.Append(",\"news\":{\"base\":").Append(b.Json(cost)).Append(",\"windows\":[")
                .Append(string.Join(",", WindowNames.Select((nm, w) => "{\"name\":\"" + nm + "\",\"all\":" + win[w].Json(cost) + ",\"confident\":" + winHi[w].Json(cost) + "}")))
                .Append("],\"trades\":[")
                .Append(string.Join(",", ths.Select(th => "{\"threshold\":" + F(th)
                    + ",\"withoutNews\":" + SimJson(Sim(basePreds, h, th, w => true)) + ",\"withNews\":" + SimJson(Sim(preds, h, th, w => true))
                    + ",\"skipNewsWindows\":" + SimJson(Sim(preds, h, th, w => w == 0)) + ",\"afterRelease\":" + SimJson(Sim(preds, h, th, w => w >= 2))
                    + ",\"beforeRelease\":" + SimJson(Sim(preds, h, th, w => w == 1)) + "}")))
                .Append("]}");
        }
        json.Append("}");
    }
}
