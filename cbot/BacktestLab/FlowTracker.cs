// พฤติกรรมราคาระดับ tick แบบเรียลไทม์: volume profile, anchored VWAP, delta / CVD และ liquidity sweep
// ป้อนทุก tick ผ่าน Feed() — เมื่อ tick เปิดนาทีใหม่ จะได้แถวสรุปของนาทีที่เพิ่งปิดกลับมา (ตัวบันทึกเขียนลง flow-YYYY-MM.csv)
//
// นิยาม (ต้องตรงกับ research\Phase1\Flow.cs ทุกประการ — ตรวจด้วย  Phase1.exe flow <โฟลเดอร์> <จาก> <ถึง> check):
//   ราคา        = ราคากลาง (bid + ask) / 2
//   วันเทรด     = เริ่ม 22:00 UTC (ตลาดเปิดใหม่หลังพักประจำวัน); ช่วง: เอเชีย 22–07, ลอนดอน 07–13, นิวยอร์ก 13–22 UTC
//   volume      = จำนวน tick (ทอง spot ไม่มีปริมาณซื้อขายจริง)
//   delta / CVD = จำนวนครั้งที่ราคากลางขยับขึ้น ลบ ขยับลง ของนาที / สะสมตั้งแต่ต้นวันเทรด
//   profile     = จำนวน tick ต่อช่องราคา 0.5 USD ของวันเทรด → POC (ช่องที่หนาที่สุด) และ value area 70% (VAL..VAH)
//   VWAP        = ค่าเฉลี่ยราคาถ่วงด้วย tick: ของวัน (ยึดต้นวันเทรด) และของช่วง (ยึดต้นช่วง)
//   sweep       = นาทีที่ราคาทะลุระดับเกิน 0.3 เท่าของกรอบเฉลี่ย 60 นาที แล้วปิดกลับเข้ามา โดย 30 นาทีก่อนหน้าไม่เคยแตะระดับนั้น
//                 PH/PL = High/Low ของวันก่อน, AH/AL = ของช่วงเอเชีย, RH/RL = ของ 60 นาทีล่าสุด (H = กวาดด้านบน → เอนลง, L = กวาดด้านล่าง → เอนขึ้น)
//   dayWarm / prevWarm = 1 เมื่อค่าของวันนี้ / ของวันก่อน สะสมมาตั้งแต่ต้นวันโดยไม่ขาดช่วง (0 = cBot เริ่มกลางวันหรือหยุดไประหว่างทาง ค่ายังไม่ครบ)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace cAlgo.Robots
{
    public class FlowTracker
    {
        public const double Bucket = 0.5;
        public const string Header = "time,ticks,delta,cvd,vwapDay,sdDay,vwapSession,poc,vah,val,prevPoc,prevVah,prevVal,prevHigh,prevLow," +
                                     "dayHigh,dayLow,asiaHigh,asiaLow,high60,low60,sweep,dayWarm,prevWarm";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly Dictionary<int, int> _profile = new Dictionary<int, int>();
        private readonly List<double[]> _bars = new List<double[]>();              // นาทีที่ปิดแล้วล่าสุด: [unix, high, low]
        private readonly StringBuilder _sb = new StringBuilder(256);
        private long _minute = -1, _day = -1, _lastUnix, _count, _sesCount;
        private double _lastMid = double.NaN, _dayHigh, _dayLow, _sum, _sum2, _sesSum, _cvd, _open, _high, _low, _close;
        private double _asiaHigh = double.NaN, _asiaLow = double.NaN;
        private double _prevPoc = double.NaN, _prevVah = double.NaN, _prevVal = double.NaN, _prevHigh = double.NaN, _prevLow = double.NaN;
        private int _session = -1, _ticks, _delta;

        public bool DayWarm { get; private set; }
        public bool PrevWarm { get; private set; }
        public bool HasState => _minute >= 0;

        // ระดับของวันเทรดก่อนหน้า (NaN = ยังไม่มี) และค่าล่าสุดของวันนี้ ให้ทฤษฎีใช้ตัดสินใจ
        public double PrevPoc => _prevPoc;
        public double PrevVah => _prevVah;
        public double PrevVal => _prevVal;
        public double PrevHigh => _prevHigh;
        public double PrevLow => _prevLow;
        public double LastMid => _lastMid;
        public double DayVwap => _count > 0 ? _sum / _count : double.NaN;

        /// <summary>รันสด: ป้อน tick ย้อนหลังจาก cTrader (ย้อนไป 4 วัน ไม่เกิน 20 วินาที) เพื่อให้มีค่าของวันนี้และวันก่อนตั้งแต่เริ่ม — คืนจำนวน tick ที่ป้อน</summary>
        public int WarmFromHistory(cAlgo.API.Ticks history, DateTime now)
        {
            if (history == null) return 0;
            var from = now.AddDays(-4); var clock = System.Diagnostics.Stopwatch.StartNew();
            while (history.Count > 0 && history[0].Time > from && clock.Elapsed.TotalSeconds < 20)
                if (history.LoadMoreHistory() <= 0) break;
            for (int i = 0; i < history.Count; i++)
                Feed(new DateTimeOffset(DateTime.SpecifyKind(history[i].Time, DateTimeKind.Utc)).ToUnixTimeSeconds(), history[i].Bid, history[i].Ask);
            return history.Count;
        }

        /// <summary>ป้อน tick ตามลำดับเวลา (unix วินาที UTC) — คืนแถวของนาทีที่เพิ่งปิดเมื่อ tick นี้เปิดนาทีใหม่ ไม่งั้นคืน null</summary>
        public string Feed(long unix, double bid, double ask)
        {
            if (unix < _lastUnix) return null;
            double mid = (bid + ask) / 2; string row = null;
            long minute = unix / 60 * 60;
            if (minute != _minute)
            {
                if (_minute >= 0 && _ticks > 0) row = CloseMinute();
                long day = (unix + 7200) / 86400;
                if (day != _day)
                {
                    bool had = _day >= 0 && _profile.Count > 0;
                    if (had) { ValueArea(out _prevPoc, out _prevVah, out _prevVal); _prevHigh = _dayHigh; _prevLow = _dayLow; PrevWarm = DayWarm; }
                    _day = day; _profile.Clear(); _dayHigh = double.MinValue; _dayLow = double.MaxValue; _sum = _sum2 = 0; _count = 0; _cvd = 0;
                    _asiaHigh = _asiaLow = double.NaN; _session = -1;
                    DayWarm = had && (unix + 7200) % 86400 < 4500;                      // เห็นตั้งแต่ตลาดเปิดวันใหม่ (22:00 หรือ 23:00 UTC)
                }
                else if (unix - _lastUnix > 900) DayWarm = false;                       // ขาดช่วงกลางวันเกิน 15 นาที
                long shifted = (unix + 7200) % 86400;                                   // 0 = 22:00 UTC
                int session = shifted < 9 * 3600 ? 0 : shifted < 15 * 3600 ? 1 : 2;
                if (session != _session) { _session = session; _sesSum = 0; _sesCount = 0; }
                _minute = minute; _open = _high = _low = mid; _ticks = 0; _delta = 0;
            }

            if (mid > _high) _high = mid; if (mid < _low) _low = mid; _close = mid; _ticks++;
            if (!double.IsNaN(_lastMid)) { int d = mid > _lastMid ? 1 : mid < _lastMid ? -1 : 0; _delta += d; _cvd += d; }
            _lastMid = mid; _lastUnix = unix;
            if (mid > _dayHigh) _dayHigh = mid; if (mid < _dayLow) _dayLow = mid;
            _sum += mid; _sum2 += mid * mid; _count++; _sesSum += mid; _sesCount++;
            int bucket = (int)Math.Floor(mid / Bucket); int seen; _profile[bucket] = _profile.TryGetValue(bucket, out seen) ? seen + 1 : 1;
            if ((unix + 7200) % 86400 < 9 * 3600)
            {
                if (double.IsNaN(_asiaHigh) || mid > _asiaHigh) _asiaHigh = mid;
                if (double.IsNaN(_asiaLow) || mid < _asiaLow) _asiaLow = mid;
            }
            return row;
        }

        /// <summary>อ่านไฟล์ tick ของตัวบันทึก (.csv หรือ .csv.gz) เพื่อสร้างสถานะต่อจากรอบก่อน</summary>
        public void ReplayFile(string file)
        {
            var name = Path.GetFileName(file);                                          // tick-YYYY-MM-DD.csv[.gz]
            long dayStart = new DateTimeOffset(DateTime.ParseExact(name.Substring(5, 10), "yyyy-MM-dd", Inv), TimeSpan.Zero).ToUnixTimeSeconds();
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var stream = file.EndsWith(".gz") ? (Stream)new GZipStream(fs, CompressionMode.Decompress) : fs)
            using (var reader = new StreamReader(stream))
            {
                reader.ReadLine(); string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length < 16 || line[12] != ',') continue;
                    int comma = line.IndexOf(',', 13); double bid, ask;
                    if (comma < 0 || !double.TryParse(line.Substring(13, comma - 13), NumberStyles.Float, Inv, out bid)
                                  || !double.TryParse(line.Substring(comma + 1), NumberStyles.Float, Inv, out ask)) continue;
                    Feed(dayStart + ((line[0] - '0') * 10 + (line[1] - '0')) * 3600 + ((line[3] - '0') * 10 + (line[4] - '0')) * 60 + (line[6] - '0') * 10 + (line[7] - '0'), bid, ask);
                }
            }
        }

        private void ValueArea(out double poc, out double vah, out double val)
        {
            poc = vah = val = double.NaN;
            if (_profile.Count == 0) return;
            int lo = _profile.Keys.Min(), hi = _profile.Keys.Max(); var a = new int[hi - lo + 1]; long total = 0;
            foreach (var kv in _profile) { a[kv.Key - lo] = kv.Value; total += kv.Value; }
            int p = 0; for (int i = 1; i < a.Length; i++) if (a[i] > a[p]) p = i;
            int l = p, h = p; long acc = a[p];
            while (acc < 0.7 * total && (l > 0 || h < a.Length - 1))
            {
                long up = h < a.Length - 1 ? a[h + 1] : -1, down = l > 0 ? a[l - 1] : -1;
                if (up >= down) { h++; acc += up; } else { l--; acc += down; }
            }
            poc = (lo + p + 0.5) * Bucket; vah = (lo + h + 1) * Bucket; val = (lo + l) * Bucket;
        }

        private string CloseMinute()
        {
            double poc, vah, val; ValueArea(out poc, out vah, out val);
            double vwap = _count > 0 ? _sum / _count : _close, sd = _count > 0 ? Math.Sqrt(Math.Max(0, _sum2 / _count - vwap * vwap)) : 0;
            bool asiaOver = (_minute + 7200) % 86400 >= 9 * 3600;
            double asiaHigh = asiaOver ? _asiaHigh : double.NaN, asiaLow = asiaOver ? _asiaLow : double.NaN;

            // กรอบของ 60 นาทีก่อนหน้า (ต้องต่อเนื่องครบ) และ sweep ของนาทีนี้
            double high60 = double.NaN, low60 = double.NaN; var sweep = "";
            int n = _bars.Count;
            if (n >= 60 && _bars[n - 60][0] == _minute - 3600)
            {
                double usual = 0, high30 = double.MinValue, low30 = double.MaxValue; high60 = double.MinValue; low60 = double.MaxValue;
                for (int j = n - 60; j < n; j++)
                {
                    double h = _bars[j][1], l = _bars[j][2];
                    usual += h - l; if (h > high60) high60 = h; if (l < low60) low60 = l;
                    if (j >= n - 30) { if (h > high30) high30 = h; if (l < low30) low30 = l; }
                }
                usual /= 60;
                sweep = Sweep(_prevHigh, _prevLow, "P", usual, high30, low30) + Sweep(asiaHigh, asiaLow, "A", usual, high30, low30) + Sweep(high60, low60, "R", usual, high30, low30);
                if (sweep.Length > 0) sweep = sweep.Substring(1);
            }
            _bars.Add(new[] { (double)_minute, _high, _low });
            if (_bars.Count > 61) _bars.RemoveAt(0);

            _sb.Clear();
            _sb.Append(DateTimeOffset.FromUnixTimeSeconds(_minute).UtcDateTime.ToString("yyyy-MM-dd HH:mm", Inv)).Append(',')
               .Append(_ticks).Append(',').Append(_delta).Append(',').Append(_cvd.ToString("0", Inv)).Append(',')
               .Append(N(vwap, "0.####")).Append(',').Append(N(sd, "0.####")).Append(',').Append(N(_sesCount > 0 ? _sesSum / _sesCount : double.NaN, "0.####")).Append(',')
               .Append(N(poc)).Append(',').Append(N(vah)).Append(',').Append(N(val)).Append(',')
               .Append(N(_prevPoc)).Append(',').Append(N(_prevVah)).Append(',').Append(N(_prevVal)).Append(',').Append(N(_prevHigh)).Append(',').Append(N(_prevLow)).Append(',')
               .Append(N(_dayHigh)).Append(',').Append(N(_dayLow)).Append(',').Append(N(asiaHigh)).Append(',').Append(N(asiaLow)).Append(',')
               .Append(N(high60)).Append(',').Append(N(low60)).Append(',').Append(sweep).Append(',')
               .Append(DayWarm ? '1' : '0').Append(',').Append(PrevWarm ? '1' : '0');
            return _sb.ToString();
        }

        /// <summary>กวาดด้านบนแล้วปิดกลับลงมา → "|xH", กวาดด้านล่างแล้วปิดกลับขึ้นไป → "|xL"</summary>
        private string Sweep(double high, double low, string name, double usual, double high30, double low30)
        {
            if (double.IsNaN(high) || double.IsNaN(low)) return "";
            if (high30 <= high && _high > high + 0.3 * usual && _close < high) return "|" + name + "H";
            if (low30 >= low && _low < low - 0.3 * usual && _close > low) return "|" + name + "L";
            return "";
        }

        private static string N(double v, string format = "0.###") => double.IsNaN(v) || v == double.MinValue || v == double.MaxValue ? "" : v.ToString(format, Inv);
    }
}
