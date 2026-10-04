// Scalp S3d (research\JOURNAL.md): "รอย่อแล้วเข้าตามทิศของวัน" ด้วยโมเดลทำนาย 5 นาที
//
// ต้องมีไฟล์ในเครื่อง (ไม่อยู่ใน repo):
//   Documents\BacktestLab\model\<symbol>-5m.json   โมเดลรายเดือน — สร้างด้วย research\Phase1  Phase1.exe export  (ควรสร้างใหม่ทุกเดือน)
//   Documents\BacktestLab\data\news\events.csv     เวลาข่าว — สร้างด้วย tools\news-fetch.ps1
// ต้องรันบนกราฟ M1 เท่านั้น
//
// feature ทั้ง 35 ตัวต้องคำนวณเหมือน research\Phase1\Program.cs (BuildFeatures) ทุกประการ — แก้ที่หนึ่งต้องแก้อีกที่ และตรวจด้วย
//   tools\backtest.ps1 -Strategy SCALP_S3D ... -Dump -NoSend   แล้ว   Phase1.exe study:parity

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using cAlgo.API;
using File = System.IO.File;

namespace cAlgo.Robots
{
    public class ScalpPullback : StrategyBase
    {
        public override string Code => "SCALP_S3D";
        public override string Name => "Scalp: ย่อแล้วตามทิศของวัน";
        public override string Theory =>
            "Scalp ด้วยโมเดลทำนาย 5 นาที (logistic regression ฝึกใหม่ทุกเดือนจาก 12 เดือนก่อนหน้า): โมเดลเก่งเรื่องจับจังหวะที่ราคายืดไปแล้วจะถูกดึงกลับ จึงใช้เฉพาะเมื่อทิศที่โมเดลทายตรงกับทิศของวัน (ราคาเทียบราคาเปิดวัน) คือรอราคาย่อแล้วเข้าตามทิศของวัน และเทรดเฉพาะช่วงลอนดอนซึ่งเป็นช่วงที่ราคาถูกดึงกลับมากที่สุด";
        public override string[] ParamNames => new[] { "MinConfidence", "HoldMinutes", "StopUsd", "Sessions" };
        public override string[] EntryRules => new[]
        {
            "โมเดลให้ความมั่นใจ ≥ {MinConfidence} ว่า 5 นาทีข้างหน้าราคาจะขึ้น (หรือลง)",
            "ทิศนั้นต้องตรงกับทิศของวัน: ราคาปัจจุบันสูงกว่าราคาเปิดวัน (00:00 UTC) → รับเฉพาะ Buy, ต่ำกว่า → รับเฉพาะ Sell",
            "Sessions {Sessions}: 1 = เฉพาะช่วงลอนดอน 07:00–13:00 UTC, 2 = ทุกช่วง",
            "ไม่เข้าตั้งแต่ 30 นาทีก่อนข่าวระดับสูง จนถึง 120 นาทีหลังข่าว",
            "ถือทีละ 1 ไม้",
        };
        public override string[] ExitRules => new[] { "ครบ {HoldMinutes} นาที → ปิด", "SL ป้องกันเหตุร้าย {StopUsd} ดอลลาร์", "ไม่มี TP" };
        public override bool ExitOnOppositeSignal => false;
        public override double RewardRisk => 0;                                  // ไม่มี TP
        public override double? StopDistance(TradeType side) => P[2];

        private const int Warmup = 1500;
        private class Model { public long From; public double[] Mean, Std, W; public double B; }
        private List<Model> _models = new List<Model>();
        private long[] _events = new long[0];
        private StreamWriter _dump;

        // สถานะที่ต้องเดินต่อเนื่องทุกแท่ง
        private int _last = -1, _processed;
        private double _ema20, _ema60, _ema200, _avgGain, _avgLoss;
        private readonly Queue<double> _ema20Hist = new Queue<double>(), _ema60Hist = new Queue<double>();
        private long _sessionDay = -1, _calendarDay = -1;
        private double _dayHigh, _dayLow, _pv, _vv, _prevDayHigh = double.NaN, _prevDayLow = double.NaN, _dayOpen;
        private double _p = double.NaN;                                          // P(ขึ้น) ของแท่งล่าสุดที่ปิดแล้ว
        private DateTime _loaded;                                                // วันที่อ่านไฟล์โมเดล/ข่าวล่าสุด

        public override double? Prediction => double.IsNaN(_p) ? (double?)null : _p;

        private static long Unix(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeSeconds();

        protected override void OnInit()
        {
            Defaults(0.56, 5, 5, 1);
            LoadFiles();
            _loaded = Bot.Server.Time.Date;
            if (Environment.GetEnvironmentVariable("BACKTESTLAB_DUMP") == "1")
            {
                _dump = new StreamWriter(Path.Combine(Reporter.Folder, "data", "scalp-dump.csv"), false);
                _dump.WriteLine("unix,p");
            }
            for (int i = 0; i <= Bot.Bars.Count - 2; i++) Process(i);
        }

        /// <summary>อ่านไฟล์โมเดลและข่าว — ขาดไฟล์ใดไฟล์หนึ่ง = ไม่เทรด</summary>
        private void LoadFiles()
        {
            var modelFile = Path.Combine(Reporter.Folder, "model", Bot.SymbolName + "-5m.json");
            var eventFile = Path.Combine(Reporter.Folder, "data", "news", "events.csv");
            if (!File.Exists(modelFile)) { Bot.Print("SCALP_S3D: missing {0} (run Phase1.exe export) — no trades", modelFile); _models = new List<Model>(); return; }
            if (!File.Exists(eventFile)) { Bot.Print("SCALP_S3D: missing {0} (run tools\\news-fetch.ps1) — no trades", eventFile); _models = new List<Model>(); return; }
            LoadEvents(eventFile);
            LoadModels(modelFile);
            if (Bot.RunningMode == RunningMode.RealTime && _models.Count > 0)
                Bot.Print("SCALP_S3D: {0} monthly models (newest from {1:yyyy-MM-dd}), {2} high-impact news times", _models.Count,
                    DateTimeOffset.FromUnixTimeSeconds(_models[_models.Count - 1].From).UtcDateTime, _events.Length);
        }

        private void LoadModels(string file)
        {
            var list = new List<Model>();
            using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
                foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
                {
                    double[] Arr(string name) => m.GetProperty(name).EnumerateArray().Select(v => v.GetDouble()).ToArray();
                    list.Add(new Model { From = m.GetProperty("from").GetInt64(), Mean = Arr("mean"), Std = Arr("std"), W = Arr("w"), B = m.GetProperty("b").GetDouble() });
                }
            list.Sort((a, b) => a.From.CompareTo(b.From));
            _models = list;
        }

        private void LoadEvents(string file)
        {
            _events = File.ReadAllLines(file).Skip(1).Select(l => l.Split(','))
                .Where(p => p.Length >= 3 && (p[2] == "HIGH" || p[2] == "EXTREME"))
                .Select(p => long.Parse(p[0], CultureInfo.InvariantCulture)).OrderBy(t => t).ToArray();
        }

        /// <summary>ใกล้ข่าวระดับสูง: ภายใน 30 นาทีก่อนข่าว หรือยังไม่ครบ 120 นาทีหลังข่าว</summary>
        private bool NearNews(long now)
        {
            int k = Array.BinarySearch(_events, now); if (k < 0) k = ~k;        // _events[k] = ข่าวถัดไป (เวลา >= now)
            if (k < _events.Length && _events[k] - now <= 30 * 60) return true;
            return k > 0 && now - _events[k - 1] < 120 * 60;
        }

        public override TradeType? Signal()
        {
            // รันสดต่อเนื่องหลายวัน: อ่านไฟล์ใหม่วันละครั้ง (ข่าวถูกสร้างใหม่รายสัปดาห์ โมเดลรายเดือน) — อ่านไม่ได้ก็ใช้ชุดเดิมต่อ
            if (Bot.RunningMode == RunningMode.RealTime && Bot.Server.Time.Date != _loaded)
            {
                _loaded = Bot.Server.Time.Date;
                try { LoadFiles(); } catch (Exception e) { Bot.Print("SCALP_S3D: reload failed, keeping the previous files: {0}", e.Message); }
            }

            int i = Bot.Bars.Count - 2;
            for (int j = _last + 1; j <= i; j++) Process(j);
            if (double.IsNaN(_p)) return null;

            var up = _p >= 0.5;
            if ((up ? _p : 1 - _p) < P[0]) return null;
            if (Math.Sign(Bot.Bars.ClosePrices[i] - _dayOpen) != (up ? 1 : -1)) return null;
            var t = Bot.Bars.OpenTimes[i];
            if ((int)P[3] == 1 && (t.Hour < 7 || t.Hour >= 13)) return null;
            if (NearNews(Unix(t) + 60)) return null;
            return up ? TradeType.Buy : TradeType.Sell;
        }

        public override bool ShouldExit(Position position) => (Bot.Server.Time - position.EntryTime).TotalMinutes >= P[1];

        public override void OnStop()
        {
            if (_dump != null) { _dump.Dispose(); _dump = null; }
        }

        /// <summary>เดินสถานะไปหนึ่งแท่ง และถ้าเป็นแท่งล่าสุดที่ปิดแล้ว คำนวณ P(ขึ้น)</summary>
        private void Process(int i)
        {
            var b = Bot.Bars; _last = i; _p = double.NaN;
            double o = b.OpenPrices[i], h = b.HighPrices[i], l = b.LowPrices[i], c = b.ClosePrices[i], v = b.TickVolumes[i];
            long unix = Unix(b.OpenTimes[i]);

            if (_processed == 0) { _ema20 = _ema60 = _ema200 = c; }
            else
            {
                _ema20 += 2.0 / 21 * (c - _ema20); _ema60 += 2.0 / 61 * (c - _ema60); _ema200 += 2.0 / 201 * (c - _ema200);
                double ch = c - b.ClosePrices[i - 1];
                _avgGain = (_avgGain * 13 + Math.Max(ch, 0)) / 14; _avgLoss = (_avgLoss * 13 + Math.Max(-ch, 0)) / 14;
            }
            _ema20Hist.Enqueue(_ema20); if (_ema20Hist.Count > 11) _ema20Hist.Dequeue();     // Peek = ค่าเมื่อ 10 แท่งก่อน
            _ema60Hist.Enqueue(_ema60); if (_ema60Hist.Count > 31) _ema60Hist.Dequeue();     // Peek = ค่าเมื่อ 30 แท่งก่อน

            long day = unix / 86400; if ((day + 4) % 7 == 0) day++;                            // คืนวันอาทิตย์นับเป็นวันจันทร์
            if (day != _sessionDay)
            {
                if (_sessionDay >= 0) { _prevDayHigh = _dayHigh; _prevDayLow = _dayLow; }
                _sessionDay = day; _dayHigh = h; _dayLow = l; _pv = 0; _vv = 0;
            }
            _dayHigh = Math.Max(_dayHigh, h); _dayLow = Math.Min(_dayLow, l);
            _pv += (h + l + c) / 3 * v; _vv += v;
            if (unix / 86400 != _calendarDay) { _calendarDay = unix / 86400; _dayOpen = o; }   // ราคาเปิดของวันตามปฏิทิน UTC

            _processed++;
            if (i != b.Count - 2 || _processed < Warmup || i < 1441 || double.IsNaN(_prevDayHigh) || _models.Count == 0) return;
            Model model = null;
            foreach (var m in _models) { if (m.From <= unix) model = m; else break; }
            if (model == null) return;

            double Tr(int j) => Math.Max(b.HighPrices[j], b.ClosePrices[j - 1]) - Math.Min(b.LowPrices[j], b.ClosePrices[j - 1]);
            double atr60 = 0, atr1440 = 0, sumC = 0, sumC2 = 0, sumV = 0, sumV2 = 0, path30 = 0, hiPrev = double.MinValue, loPrev = double.MaxValue;
            for (int j = i - 1439; j <= i; j++) { double tr = Tr(j); atr1440 += tr; if (j > i - 60) atr60 += tr; }
            atr60 /= 60; atr1440 /= 1440;
            for (int j = i - 59; j <= i; j++) { double cj = b.ClosePrices[j], vj = b.TickVolumes[j]; sumC += cj; sumC2 += cj * cj; sumV += vj; sumV2 += vj * vj; }
            for (int j = i - 60; j < i; j++) { hiPrev = Math.Max(hiPrev, b.HighPrices[j]); loPrev = Math.Min(loPrev, b.LowPrices[j]); }
            for (int j = i - 29; j <= i; j++) path30 += Math.Abs(b.ClosePrices[j] - b.ClosePrices[j - 1]);
            path30 /= 30;
            double mean60 = sumC / 60, std60 = Math.Sqrt(Math.Max(0, sumC2 / 60 - mean60 * mean60));
            double vMean = sumV / 60, vStd = Math.Sqrt(Math.Max(0, sumV2 / 60 - vMean * vMean));
            double rsi = _avgLoss == 0 ? 100 : 100 - 100 / (1 + _avgGain / _avgLoss);
            double vwap = _vv > 0 ? _pv / _vv : c;
            double u = Math.Max(atr60, 1e-6), rng = Math.Max(h - l, 1e-9);
            double Ret(int k) => (c - b.ClosePrices[i - k]) / u;
            double volZ = vStd > 0 ? (v - vMean) / vStd : 0;
            double dayFrac = (unix % 86400) / 86400.0;
            int sign = Math.Sign(c - o), streak = 0;
            if (sign != 0) for (int j = i; j > i - 10 && Math.Sign(b.ClosePrices[j] - b.OpenPrices[j]) == sign; j--) streak++;

            var x = new[]
            {
                Ret(1), Ret(3), Ret(5), Ret(15), Ret(60),
                (c - o) / rng, (h - Math.Max(o, c)) / rng, (Math.Min(o, c) - l) / rng, (h - l) / u,
                volZ, Math.Log(Math.Max(v, 1) / Math.Max(vMean, 1)),
                (c - _ema20) / u, (c - _ema60) / u, (c - _ema200) / u, (_ema20 - _ema20Hist.Peek()) / u, (_ema60 - _ema60Hist.Peek()) / u,
                (c - vwap) / u, std60 > 0 ? (c - mean60) / std60 : 0, (rsi - 50) / 50,
                Math.Log(Math.Max(atr60, 1e-6) / Math.Max(atr1440, 1e-6)),
                (c - _dayHigh) / u, (c - _dayLow) / u, (c - _prevDayHigh) / u, (c - _prevDayLow) / u,
                (c - Math.Max(hiPrev, h)) / u, (c - Math.Min(loPrev, l)) / u,
                h > hiPrev && c < hiPrev ? 1 : 0, l < loPrev && c > loPrev ? 1 : 0, c > hiPrev ? 1 : 0, c < loPrev ? 1 : 0,
                Math.Sin(2 * Math.PI * dayFrac), Math.Cos(2 * Math.PI * dayFrac),
                path30 > 0 ? (c - b.ClosePrices[i - 30]) / (path30 * 30) : 0,
                Ret(1) * volZ,
                sign * streak,
            };
            if (x.Length != model.W.Length) return;

            double z = model.B;
            for (int f = 0; f < x.Length; f++)
            {
                double raw = double.IsNaN(x[f]) || double.IsInfinity(x[f]) ? 0 : (float)Math.Max(-50, Math.Min(50, x[f]));   // เก็บเป็น float เหมือนฝั่งวิจัย
                double s = (raw - model.Mean[f]) / model.Std[f];
                z += model.W[f] * (s > 5 ? 5 : s < -5 ? -5 : s);
            }
            _p = 1 / (1 + Math.Exp(-z));
            if (_dump != null) _dump.WriteLine(unix.ToString(CultureInfo.InvariantCulture) + "," + _p.ToString("R", CultureInfo.InvariantCulture));
        }
    }
}
