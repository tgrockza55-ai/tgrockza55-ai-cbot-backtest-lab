// หาจุดสวิง (swing high / swing low) จากราคาอย่างเดียว — ใช้ร่วมกันในทฤษฎีกราฟ
// จุดสวิง = แท่งที่ High สูงกว่า (หรือ Low ต่ำกว่า) แท่งข้างเคียงฝั่งละ k แท่ง
// ยืนยันได้ก็ต่อเมื่อมีแท่งปิดทางขวาครบ k แท่งแล้ว จึงไม่มี look-ahead

using System.Collections.Generic;
using cAlgo.API;

namespace cAlgo.Robots
{
    public struct Swing
    {
        public int Index;      // ลำดับแท่งใน Bars (นับจากแท่งแรก)
        public double Price;
        public bool IsHigh;
    }

    public class SwingTracker
    {
        private const int Keep = 40;
        private readonly Bars _bars;
        private readonly int _k;
        private int _lastChecked = -1;

        public List<Swing> Highs { get; } = new List<Swing>();
        public List<Swing> Lows { get; } = new List<Swing>();
        /// <summary>ทุกจุดสวิงเรียงตามเวลา (สูง/ต่ำปนกัน)</summary>
        public List<Swing> All { get; } = new List<Swing>();

        public SwingTracker(Bars bars, int k)
        {
            _bars = bars;
            _k = k < 1 ? 1 : k;
        }

        /// <summary>ลำดับของแท่งล่าสุดที่ปิดแล้ว</summary>
        public int LastClosedIndex => _bars.Count - 2;

        /// <summary>เรียกครั้งเดียวต่อแท่งใหม่ — คืน true ถ้าเพิ่งยืนยันจุดสวิงใหม่</summary>
        public bool Update()
        {
            var c = LastClosedIndex - _k;          // แท่งที่มีแท่งปิดทางขวาครบ k แท่ง
            if (c - _k < 0 || c <= _lastChecked) return false;
            _lastChecked = c;

            var h = _bars.HighPrices[c];
            var l = _bars.LowPrices[c];
            bool isHigh = true, isLow = true;
            for (int j = c - _k; j <= c + _k && (isHigh || isLow); j++)
            {
                if (j == c) continue;
                if (_bars.HighPrices[j] >= h) isHigh = false;
                if (_bars.LowPrices[j] <= l) isLow = false;
            }

            if (isHigh) Add(Highs, new Swing { Index = c, Price = h, IsHigh = true });
            if (isLow) Add(Lows, new Swing { Index = c, Price = l, IsHigh = false });
            return isHigh || isLow;
        }

        /// <summary>Low ต่ำสุดของแท่งลำดับ from..to (รวมปลาย)</summary>
        public double LowestBetween(int from, int to)
        {
            var v = double.MaxValue;
            for (int i = from; i <= to; i++) if (_bars.LowPrices[i] < v) v = _bars.LowPrices[i];
            return v;
        }

        public double HighestBetween(int from, int to)
        {
            var v = double.MinValue;
            for (int i = from; i <= to; i++) if (_bars.HighPrices[i] > v) v = _bars.HighPrices[i];
            return v;
        }

        private void Add(List<Swing> list, Swing s)
        {
            list.Add(s);
            All.Add(s);
            if (list.Count > Keep) list.RemoveAt(0);
            if (All.Count > Keep * 2) All.RemoveAt(0);
        }
    }
}
