// บันทึกตลาดสด (Phase 1B) — เก็บสิ่งที่ backtest ไม่มี: ทุก tick ที่ได้รับจริง, สเปรดจริง, ความลึกของออเดอร์ (Depth of Market),
// คำทำนายของโมเดลทุกนาที, ข่าวที่อยู่ใกล้ และราคาที่ได้จริงเทียบกับราคาที่ตั้งใจ (slippage) ของทุกออเดอร์
//
// ทำงานตอนรันสด (RealTime) เท่านั้น — เขียนลง %LOCALAPPDATA%\BacktestLab\live\<symbol>\
// (นอก Documents เพราะไฟล์ถูกเขียนต่อท้ายตลอดเวลา ถ้าอยู่ใน OneDrive จะถูกอัปโหลดซ้ำไม่หยุด)
//   tick-YYYY-MM-DD.csv   time,bid,ask          ทุก tick (เวลา UTC)
//   book-YYYY-MM-DD.csv   time,bids,asks        ภาพ DOM เมื่อมีการเปลี่ยนแปลง ไม่ถี่กว่าที่ตั้งไว้ — แต่ละฝั่งเป็น ราคา:ปริมาณ|ราคา:ปริมาณ เรียงจากราคาที่ดีที่สุด
//   min-YYYY-MM.csv       หนึ่งแถวต่อแท่ง M1 ที่ปิดแล้ว: แท่งราคา + สรุป tick / สเปรด / DOM + ข่าว + คำทำนาย + สัญญาณ
//   flow-YYYY-MM.csv      หนึ่งแถวต่อนาที: delta / CVD, anchored VWAP, volume profile (POC, VAH, VAL), ระดับสภาพคล่องและ sweep (FlowTracker.cs)
//                         + ladderBid / ladderAsk = ระยะจากราคาที่ดีที่สุดถึงชั้นลึกสุดของ DOM เฉลี่ยในนาที (สภาพคล่องบางลง = ระยะกว้างขึ้น)
//   trades.csv            OPEN / CLOSE ของทุกออเดอร์ พร้อมราคาที่ตั้งใจ ราคาที่ได้จริง และเวลาที่ใช้ส่งคำสั่ง
//   runs.csv              เวลาเริ่ม / หยุดของ cBot (นาทีแรกหลังเริ่มไม่ครบ จึงไม่ถูกเขียนลง min)
// ไฟล์ tick / book ของวันก่อน ๆ ถูกบีบเป็น .csv.gz อัตโนมัติ
//
// ข่าว: Documents\BacktestLab\data\news\events.csv (tools\news-fetch.ps1) + ปฏิทินรายสัปดาห์ของ Forex Factory ที่ตัวบันทึกดึงเองวันละครั้ง
// ความผิดพลาดของตัวบันทึกต้องไม่ทำให้ cBot หยุด — ทุกทางเข้าจึงถูกครอบด้วย Guard

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using cAlgo.API;
using File = System.IO.File;

namespace cAlgo.Robots
{
    public class LiveRecorder
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private const string WeeklyCalendar = "https://nfs.faireconomy.media/ff_calendar_thisweek.json";
        private const int MaxLevels = 25;
        private const string MinuteHeader = "time,open,high,low,close,tickVolume,ticks,spreadAvg,spreadMax,books,bidVol,askVol,imbalance,depthUpdates," +
                                            "nextNews,nextImpact,minToNext,lastNews,lastImpact,minSinceLast,pUp,signal,positions,equity";
        private const string TradeHeader = "time,event,id,side,units,price,expected,slippage,latencyMs,spread,stopLoss,pUp,reason,gross,commission,swap,net,balance";

        /// <summary>folder = "live" ตอนรันสด; ตอนทดสอบใน backtest ใช้ชื่ออื่นเสมอ (ค่าเริ่มต้น live-test) เพื่อไม่ให้ปนกับข้อมูลจริง</summary>
        public static string Root(string folder) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BacktestLab", folder);

        private struct News { public long Time; public int Rank; public string Code, Impact; }

        private readonly Robot _bot;
        private readonly MarketDepth _depth;
        private readonly string _dir, _newsDir, _price;
        private readonly int _bookEvery;
        private readonly bool _test;
        private readonly StringBuilder _sb = new StringBuilder(1024);
        private StreamWriter _tick, _book;
        private DateTime _day = DateTime.MinValue, _lastBook = DateTime.MinValue, _lastFlush = DateTime.MinValue;
        private bool _bookDirty, _firstBar = true;
        private FlowTracker _flow;
        private double _ladderBid, _ladderAsk; private int _ladderCount;      // สะสมระหว่างสองแถวของ flow
        private volatile News[] _news = new News[0];
        private volatile string _message;                    // ข้อความจากงานเบื้องหลัง — พิมพ์ลง Log ตอนแท่งถัดไป
        private int _errors;

        // สะสมภายในหนึ่งนาที
        private int _ticks, _books, _depthUpdates;
        private double _spreadSum, _spreadMax, _bidVolSum, _askVolSum, _imbalanceSum;

        /// <param name="history">tick ย้อนหลังของ symbol (รันสด) ใช้สร้างสถานะของวันนี้และวันก่อนให้ FlowTracker ตั้งแต่เริ่ม; null = เริ่มจากศูนย์</param>
        /// <param name="testFolder">null = รันสด (โฟลเดอร์ live); ไม่ null = ทดสอบใน backtest เขียนลงโฟลเดอร์ชื่อนี้</param>
        public LiveRecorder(Robot bot, MarketDepth depth, Ticks history, int bookSeconds, string testFolder, string strategy)
        {
            bool test = testFolder != null;
            _bot = bot; _depth = depth; _bookEvery = Math.Max(1, bookSeconds); _test = test;
            _dir = Path.Combine(Root(!test ? "live" : testFolder == "live" || testFolder == "1" ? "live-test" : testFolder), bot.SymbolName);
            _newsDir = Path.Combine(Reporter.Folder, "data", "news");
            _price = "F" + bot.Symbol.Digits;
            Directory.CreateDirectory(_dir);

            var now = bot.Server.TimeInUtc;
            CompressOldDays(now.Date);
            _news = ReadNews();
            if (!test) RefreshCalendar(now);
            if (_depth != null) _depth.Updated += () => Guard(OnDepth);
            Append("runs.csv", "time,event,account,balance,strategy", string.Join(",", Stamp(now), "START",
                bot.Account.IsLive ? "live" : "demo", Num(bot.Account.Balance, "0.00"), strategy));
            _flow = test ? new FlowTracker() : WarmFlow(history, now);
            bot.Print("Recording the market to {0} ({1} scheduled news events loaded)", _dir, _news.Length);
            if (!test) bot.Print("Flow tracker: today's values are {0}; yesterday's levels are {1}",
                _flow.DayWarm ? "complete from the start of the trading day" : "incomplete until the next trading day starts (22:00 UTC)",
                _flow.PrevWarm ? "complete" : "not complete yet");
        }

        // ------------------------------------------------------------------ ทางเข้าจาก robot

        public void OnTick() => Guard(() =>
        {
            var now = _bot.Server.TimeInUtc; Roll(now);
            double bid = _bot.Symbol.Bid, ask = _bot.Symbol.Ask, spread = ask - bid;
            _tick.Write(now.ToString("HH:mm:ss.fff", Inv)); _tick.Write(',');
            _tick.Write(bid.ToString(_price, Inv)); _tick.Write(',');
            _tick.WriteLine(ask.ToString(_price, Inv));
            _ticks++; _spreadSum += spread; if (spread > _spreadMax) _spreadMax = spread;

            var row = _flow.Feed(Unix(now), bid, ask);                          // ไม่ null = นาทีก่อนหน้าปิดแล้ว
            if (row != null)
            {
                Append("flow-" + row.Substring(0, 7) + ".csv", FlowTracker.Header + ",ladderBid,ladderAsk", row + "," +
                    (_ladderCount > 0 ? Num(_ladderBid / _ladderCount, "0.###") : "") + "," + (_ladderCount > 0 ? Num(_ladderAsk / _ladderCount, "0.###") : ""));
                _ladderBid = _ladderAsk = 0; _ladderCount = 0;
            }
        });

        public void OnTimer() => Guard(() =>
        {
            var now = _bot.Server.TimeInUtc;
            if (_bookDirty && (now - _lastBook).TotalSeconds >= _bookEvery) WriteBook(now);
            if ((now - _lastFlush).TotalSeconds >= 2) Flush(now);
        });

        /// <summary>เรียกทุกแท่งใหม่ หลังทฤษฎีคำนวณสัญญาณแล้ว — เขียนสรุปของแท่งที่เพิ่งปิด</summary>
        public void OnBar(TradeType? signal, double? pUp, int positions) => Guard(() =>
        {
            if (_message != null) { _bot.Print("Recorder: {0}", _message); _message = null; }
            var b = _bot.Bars; int i = b.Count - 2;
            if (i < 0) return;
            if (_firstBar) { _firstBar = false; ResetMinute(); return; }

            var t = b.OpenTimes[i]; long close = Unix(t) + 60;
            var news = _news;
            int k = LowerBound(news, close);                                   // news[k] = ข่าวถัดไป (เวลา >= เวลาปิดแท่ง)
            string next = ",,", last = ",,";
            if (k < news.Length) { var e = Strongest(news, k, +1); next = e.Code + "," + e.Impact + "," + Num((e.Time - close) / 60.0, "0.#"); }
            if (k > 0) { var e = Strongest(news, k - 1, -1); last = e.Code + "," + e.Impact + "," + Num((close - e.Time) / 60.0, "0.#"); }

            _sb.Clear();
            _sb.Append(t.ToString("yyyy-MM-dd HH:mm", Inv)).Append(',')
               .Append(b.OpenPrices[i].ToString(_price, Inv)).Append(',').Append(b.HighPrices[i].ToString(_price, Inv)).Append(',')
               .Append(b.LowPrices[i].ToString(_price, Inv)).Append(',').Append(b.ClosePrices[i].ToString(_price, Inv)).Append(',')
               .Append(Num(b.TickVolumes[i], "0")).Append(',').Append(_ticks).Append(',')
               .Append(_ticks > 0 ? Num(_spreadSum / _ticks, "0.###") : "").Append(',').Append(_ticks > 0 ? Num(_spreadMax, "0.###") : "").Append(',')
               .Append(_books).Append(',')
               .Append(_books > 0 ? Num(_bidVolSum / _books, "0.#") : "").Append(',').Append(_books > 0 ? Num(_askVolSum / _books, "0.#") : "").Append(',')
               .Append(_books > 0 ? Num(_imbalanceSum / _books, "0.###") : "").Append(',').Append(_depthUpdates).Append(',')
               .Append(next).Append(',').Append(last).Append(',')
               .Append(pUp.HasValue ? Num(pUp.Value, "0.####") : "").Append(',').Append(signal.HasValue ? signal.Value.ToString() : "").Append(',')
               .Append(positions).Append(',').Append(Num(_bot.Account.Equity, "0.00"));
            Append("min-" + t.ToString("yyyy-MM", Inv) + ".csv", MinuteHeader, _sb.ToString());
            ResetMinute();
        });

        /// <summary>ออเดอร์เปิดแล้ว: expected = ราคาที่เห็นตอนสั่ง, slippage เป็นบวก = ได้ราคาแย่กว่าที่เห็น</summary>
        public void OrderOpened(Position p, double expected, double latencyMs, double? pUp) => Guard(() =>
        {
            double slip = p.TradeType == TradeType.Buy ? p.EntryPrice - expected : expected - p.EntryPrice;
            Append("trades.csv", TradeHeader, string.Join(",", Stamp(_bot.Server.TimeInUtc), "OPEN", p.Id, p.TradeType, Num(p.VolumeInUnits, "0.##"),
                p.EntryPrice.ToString(_price, Inv), expected.ToString(_price, Inv), Num(slip, "0.###"), Num(latencyMs, "0"),
                Num(_bot.Symbol.Ask - _bot.Symbol.Bid, "0.###"), p.StopLoss.HasValue ? p.StopLoss.Value.ToString(_price, Inv) : "",
                pUp.HasValue ? Num(pUp.Value, "0.####") : "", "", "", "", "", "", Num(_bot.Account.Balance, "0.00")));
        });

        public void OrderFailed(TradeType side, string error) => Guard(() =>
            Append("trades.csv", TradeHeader, string.Join(",", Stamp(_bot.Server.TimeInUtc), "FAILED", "", side, "", "", "", "", "", "", "", "", error, "", "", "", "", "")));

        /// <summary>ออเดอร์ปิดแล้ว: expected = ราคา SL/TP หรือราคาที่เห็นตอนสั่งปิด (ว่าง = ไม่ทราบ)</summary>
        public void OrderClosed(Position p, string reason, double? expected, double? price) => Guard(() =>
        {
            string slip = "";
            if (expected.HasValue && price.HasValue)
                slip = Num(p.TradeType == TradeType.Buy ? expected.Value - price.Value : price.Value - expected.Value, "0.###");
            Append("trades.csv", TradeHeader, string.Join(",", Stamp(_bot.Server.TimeInUtc), "CLOSE", p.Id, p.TradeType, Num(p.VolumeInUnits, "0.##"),
                price.HasValue ? price.Value.ToString(_price, Inv) : "", expected.HasValue ? expected.Value.ToString(_price, Inv) : "", slip, "",
                Num(_bot.Symbol.Ask - _bot.Symbol.Bid, "0.###"), "", "", reason,
                Num(p.GrossProfit, "0.00"), Num(p.Commissions, "0.00"), Num(p.Swap, "0.00"), Num(p.NetProfit, "0.00"), Num(_bot.Account.Balance, "0.00")));
        });

        public void Stop() => Guard(() =>
        {
            var now = _bot.Server.TimeInUtc;
            if (_bookDirty) WriteBook(now);
            Close(ref _tick); Close(ref _book);
            Append("runs.csv", "time,event,account,balance,strategy", string.Join(",", Stamp(now), "STOP",
                _bot.Account.IsLive ? "live" : "demo", Num(_bot.Account.Balance, "0.00"), ""));
        });

        // ------------------------------------------------------------------ flow (FlowTracker)

        /// <summary>
        /// สร้างสถานะของ FlowTracker ก่อนรับ tick สด: ลอง tick ย้อนหลังจาก cTrader ก่อน (ไม่เกิน 20 วินาที)
        /// ถ้าได้ไม่ถึงต้นวันเทรด ใช้ไฟล์ tick ที่ตัวบันทึกเขียนไว้เอง 5 วันล่าสุดแทน; ไม่ได้ทั้งสองทางก็เริ่มจากศูนย์ (dayWarm = 0)
        /// </summary>
        private FlowTracker WarmFlow(Ticks history, DateTime now)
        {
            var flow = new FlowTracker();
            try
            {
                if (history != null)
                {
                    var from = now.AddDays(-4); var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (history.Count > 0 && history[0].Time > from && clock.Elapsed.TotalSeconds < 20)
                        if (history.LoadMoreHistory() <= 0) break;
                    for (int i = 0; i < history.Count; i++) flow.Feed(Unix(history[i].Time), history[i].Bid, history[i].Ask);
                    _bot.Print("Flow tracker: {0} historical ticks from {1:yyyy-MM-dd HH:mm}", history.Count, history.Count > 0 ? history[0].Time : now);
                }
            }
            catch (Exception e) { _bot.Print("Flow tracker: tick history not available: {0}", e.Message); flow = new FlowTracker(); }
            if (flow.DayWarm) return flow;

            var files = new FlowTracker();
            try
            {
                for (int back = 5; back >= 0; back--)
                {
                    var file = Path.Combine(_dir, "tick-" + Day(now.Date.AddDays(-back)) + ".csv");
                    if (File.Exists(file + ".gz")) files.ReplayFile(file + ".gz"); else if (File.Exists(file)) files.ReplayFile(file);
                }
            }
            catch (Exception e) { _bot.Print("Flow tracker: could not replay the tick files: {0}", e.Message); }
            return files.DayWarm || !flow.HasState ? files : flow;
        }

        // ------------------------------------------------------------------ DOM

        private void OnDepth()
        {
            _depthUpdates++; _bookDirty = true;
            var now = _bot.Server.TimeInUtc;
            if ((now - _lastBook).TotalSeconds >= _bookEvery) WriteBook(now);
        }

        private void WriteBook(DateTime now)
        {
            _bookDirty = false; _lastBook = now;
            if (_depth == null) return;
            var bids = _depth.BidEntries.OrderByDescending(e => e.Price).Take(MaxLevels).ToList();
            var asks = _depth.AskEntries.OrderBy(e => e.Price).Take(MaxLevels).ToList();
            if (bids.Count == 0 && asks.Count == 0) return;
            Roll(now);

            _sb.Clear().Append(now.ToString("HH:mm:ss.fff", Inv)).Append(',');
            double bidVol = Levels(bids); _sb.Append(',');
            double askVol = Levels(asks);
            _book.WriteLine(_sb.ToString());
            if (bids.Count > 1 && asks.Count > 1)
            {
                _ladderBid += bids[0].Price - bids[bids.Count - 1].Price; _ladderAsk += asks[asks.Count - 1].Price - asks[0].Price; _ladderCount++;
            }
            _books++; _bidVolSum += bidVol; _askVolSum += askVol;
            if (bidVol + askVol > 0) _imbalanceSum += (bidVol - askVol) / (bidVol + askVol);
        }

        private double Levels(List<MarketDepthEntry> side)
        {
            double total = 0;
            for (int i = 0; i < side.Count; i++)
            {
                if (i > 0) _sb.Append('|');
                _sb.Append(side[i].Price.ToString(_price, Inv)).Append(':').Append(Num(side[i].VolumeInUnits, "0.##"));
                total += side[i].VolumeInUnits;
            }
            return total;
        }

        // ------------------------------------------------------------------ ไฟล์

        private void Roll(DateTime now)
        {
            if (now.Date == _day) return;
            var first = _day == DateTime.MinValue;
            Close(ref _tick); Close(ref _book);
            _day = now.Date;
            _tick = Open("tick-" + Day(_day) + ".csv", "time,bid,ask");
            _book = Open("book-" + Day(_day) + ".csv", "time,bids,asks");
            if (first) return;
            CompressOldDays(_day);
            _news = ReadNews();
            if (!_test) RefreshCalendar(now);
        }

        private StreamWriter Open(string name, string header)
        {
            var file = Path.Combine(_dir, name);
            var isNew = !File.Exists(file) || new FileInfo(file).Length == 0;
            var w = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false), 1 << 16);
            if (isNew) w.WriteLine(header);
            return w;
        }

        private static void Close(ref StreamWriter w)
        {
            if (w == null) return;
            w.Dispose(); w = null;
        }

        private void Flush(DateTime now)
        {
            _lastFlush = now;
            if (_tick != null) _tick.Flush();
            if (_book != null) _book.Flush();
        }

        private void Append(string name, string header, string line)
        {
            var file = Path.Combine(_dir, name);
            File.AppendAllText(file, (File.Exists(file) ? "" : header + "\r\n") + line + "\r\n", new UTF8Encoding(false));
        }

        private void CompressOldDays(DateTime today)
        {
            foreach (var file in Directory.GetFiles(_dir, "*.csv"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                DateTime day;
                if (!(name.StartsWith("tick-") || name.StartsWith("book-")) || File.Exists(file + ".gz")) continue;
                if (!DateTime.TryParseExact(name.Substring(5), "yyyy-MM-dd", Inv, DateTimeStyles.None, out day) || day >= today) continue;
                try
                {
                    var tmp = file + ".gz.tmp";
                    using (var source = File.OpenRead(file))
                    using (var target = new GZipStream(File.Create(tmp), CompressionLevel.Optimal))
                        source.CopyTo(target);
                    File.Move(tmp, file + ".gz");
                    File.Delete(file);
                }
                catch (Exception e) { _bot.Print("Recorder: could not compress {0}: {1}", name, e.Message); }
            }
        }

        // ------------------------------------------------------------------ ข่าว

        private static int Rank(string impact) => impact == "EXTREME" ? 3 : impact == "HIGH" ? 2 : impact == "MEDIUM" ? 1 : 0;

        /// <summary>events.csv + ปฏิทิน Forex Factory ของสัปดาห์นี้และสัปดาห์ก่อน (เฉพาะ USD ระดับ Medium ขึ้นไป) เรียงตามเวลา</summary>
        private News[] ReadNews()
        {
            var list = new List<News>();
            try
            {
                var events = Path.Combine(_newsDir, "events.csv");
                if (File.Exists(events))
                    foreach (var line in File.ReadAllLines(events).Skip(1))
                    {
                        var p = line.Split(',');
                        if (p.Length >= 3) list.Add(new News { Time = long.Parse(p[0], Inv), Code = p[1], Impact = p[2], Rank = Rank(p[2]) });
                    }
                if (Directory.Exists(_newsDir))
                    foreach (var file in Directory.GetFiles(_newsDir, "ff-*.json").OrderByDescending(f => f).Take(2))
                        using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
                            foreach (var e in doc.RootElement.EnumerateArray())
                            {
                                var impact = (e.GetProperty("impact").GetString() ?? "").ToUpperInvariant();
                                if (e.GetProperty("country").GetString() != "USD" || Rank(impact) == 0) continue;
                                var title = (e.GetProperty("title").GetString() ?? "").Replace(',', ' ').Trim();
                                list.Add(new News
                                {
                                    Time = DateTimeOffset.Parse(e.GetProperty("date").GetString(), Inv).ToUnixTimeSeconds(),
                                    Code = "FF:" + (title.Length > 40 ? title.Substring(0, 40) : title), Impact = impact, Rank = Rank(impact),
                                });
                            }
            }
            catch (Exception e) { _message = "news files: " + e.Message; }
            return list.OrderBy(n => n.Time).ToArray();
        }

        /// <summary>ดึงปฏิทินของสัปดาห์นี้ถ้ายังไม่มีไฟล์ (ชื่อไฟล์ = วันจันทร์ของสัปดาห์ เหมือน tools\news-fetch.ps1) — ทำเบื้องหลัง ไม่ให้ tick ค้าง</summary>
        private void RefreshCalendar(DateTime now)
        {
            var monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
            var file = Path.Combine(_newsDir, "ff-" + Day(monday) + ".json");
            if (File.Exists(file)) return;
            Task.Run(() =>
            {
                try
                {
                    var text = Http.GetStringAsync(WeeklyCalendar).GetAwaiter().GetResult();
                    using (JsonDocument.Parse(text)) { }                        // ต้องเป็น JSON จริง ไม่ใช่หน้าแจ้งว่าเรียกถี่เกิน
                    Directory.CreateDirectory(_newsDir);
                    File.WriteAllText(file, text, new UTF8Encoding(false));
                    _news = ReadNews();
                    _message = "weekly calendar saved: " + Path.GetFileName(file);
                }
                catch (Exception e) { _message = "weekly calendar not fetched: " + e.Message; }
            });
        }

        private static int LowerBound(News[] a, long time)
        {
            int lo = 0, hi = a.Length;
            while (lo < hi) { int mid = (lo + hi) / 2; if (a[mid].Time < time) lo = mid + 1; else hi = mid; }
            return lo;
        }

        /// <summary>หลายข่าวออกเวลาเดียวกัน → เอาตัวที่ระดับสูงสุด</summary>
        private static News Strongest(News[] a, int k, int step)
        {
            var best = a[k];
            for (int j = k + step; j >= 0 && j < a.Length && a[j].Time == a[k].Time; j += step)
                if (a[j].Rank > best.Rank) best = a[j];
            return best;
        }

        // ------------------------------------------------------------------ เบ็ดเตล็ด

        private void ResetMinute()
        {
            _ticks = _books = _depthUpdates = 0;
            _spreadSum = _spreadMax = _bidVolSum = _askVolSum = _imbalanceSum = 0;
        }

        private void Guard(Action action)
        {
            try { action(); }
            catch (Exception e) { if (_errors++ < 5) _bot.Print("Recorder error: {0}", e.Message); }
        }

        private static long Unix(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeSeconds();
        private static string Day(DateTime t) => t.ToString("yyyy-MM-dd", Inv);
        private static string Stamp(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss.fff", Inv);
        private static string Num(double v, string format) => v.ToString(format, Inv);
    }
}
