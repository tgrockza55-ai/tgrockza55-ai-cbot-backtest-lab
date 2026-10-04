// เครื่องมือ ไม่ใช่ทฤษฎี: ส่งออกแท่งราคาที่ปิดแล้วทุกแท่งเป็น CSV สำหรับงานสถิติ (เช่น tools\day-study.ps1)
// ไฟล์: Documents\BacktestLab\data\<symbol>_<timeframe>.csv   คอลัมน์: unix,open,high,low,close,volume (เวลา UTC ของต้นแท่ง)
// ไม่เปิดออเดอร์ และไม่ส่งผลขึ้น lab

using System;
using System.Globalization;
using System.IO;
using cAlgo.API;

namespace cAlgo.Robots
{
    public class DataExport : StrategyBase
    {
        public override string Code => "DATA_EXPORT";
        public override string Name => "Data export (utility)";
        public override string Theory => "เครื่องมือส่งออกแท่งราคาเป็น CSV ไม่ใช่ทฤษฎีเทรด";
        public override string[] EntryRules => new string[0];
        public override string[] ExitRules => new string[0];
        public override bool IsUtility => true;

        private StreamWriter _out;
        private string _path;
        private long _rows;

        protected override void OnInit()
        {
            var dir = Path.Combine(Reporter.Folder, "data");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, Bot.SymbolName + "_" + Bot.TimeFrame + ".csv");
            _out = new StreamWriter(_path, false);
            _out.WriteLine("unix,open,high,low,close,volume");
        }

        public override TradeType? Signal()
        {
            if (Bot.Bars.Count < 2) return null;
            var inv = CultureInfo.InvariantCulture;
            var t = new DateTimeOffset(DateTime.SpecifyKind(Bot.Bars.OpenTimes.Last(1), DateTimeKind.Utc)).ToUnixTimeSeconds();
            _out.WriteLine(string.Join(",",
                t.ToString(inv), O(1).ToString(inv), H(1).ToString(inv), L(1).ToString(inv), C(1).ToString(inv),
                Bot.Bars.TickVolumes.Last(1).ToString(inv)));
            _rows++;
            return null;
        }

        public override void OnStop()
        {
            if (_out == null) return;
            _out.Dispose();
            _out = null;
            Bot.Print("Exported {0} bars to {1}", _rows, _path);
        }
    }
}
