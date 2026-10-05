// ทฤษฎีรวมสำหรับรันเดินหน้าบน Demo: S3d (scalp ช่วงลอนดอน) + NYV (นิวยอร์กเปิดนอก value area ของวันก่อน) ใน cBot ตัวเดียว
// สองกฎเทรดคนละช่วงเวลา จึงถือพร้อมกันได้อย่างมากกฎละ 1 ไม้ — ออเดอร์ติดป้าย "S3D" / "NYV" ใน Comment เพื่อแยกวิธีออกและแยกผล
// ตรรกะของแต่ละกฎอยู่ในคลาสเดิม (ScalpPullback, NyValue) ไฟล์นี้แค่เรียกทั้งคู่ — แก้กฎให้แก้ที่คลาสเดิม

using System.Linq;
using cAlgo.API;

namespace cAlgo.Robots
{
    public class DemoCombo : StrategyBase
    {
        public override string Code => "DEMO_S3D_NYV";
        public override string Name => "รวม: scalp ลอนดอน (S3d) + นิวยอร์กเปิดนอก value area (NYV)";
        public override string Theory =>
            "รันสองกฎที่เทรดคนละช่วงเวลาในบัญชีเดียว: S3d รอราคาย่อแล้วเข้าตามทิศของวันในช่วงลอนดอน (ช่วงที่ราคาถูกดึงกลับมากที่สุด) " +
            "ส่วน NYV เข้าตามทิศเมื่อถึงเวลาเปิดช่วงนิวยอร์กแล้วราคายังอยู่นอกช่วงราคาที่ตลาดยอมรับเมื่อวาน (ช่วงที่ราคาวิ่งเป็นแนวโน้มมากที่สุด)";
        public override string[] ParamNames => new[] { "MinConfidence", "ScalpHoldMinutes", "StopUsd", "NyHoldMinutes" };
        public override string[] EntryRules => new[]
        {
            "S3D (07:00–13:00 UTC): โมเดลทำนาย 5 นาทีมั่นใจ ≥ {MinConfidence} และทิศตรงกับทิศของวัน, นอกช่วงข่าว",
            "NYV (13:00 UTC): ราคาเหนือขอบบนของ value area ของวันก่อน → Buy, ต่ำกว่าขอบล่าง → Sell, นอกช่วงข่าว, วันละไม่เกิน 1 ไม้",
            "ถือได้อย่างมากกฎละ 1 ไม้",
        };
        public override string[] ExitRules => new[]
        {
            "S3D: ครบ {ScalpHoldMinutes} นาที → ปิด", "NYV: ครบ {NyHoldMinutes} นาที → ปิด", "SL {StopUsd} ดอลลาร์ทุกไม้", "ไม่มี TP",
        };
        public override bool ExitOnOppositeSignal => false;
        public override double RewardRisk => 0;                                  // ไม่มี TP
        public override int MaxPositions => 2;
        public override double? StopDistance(TradeType side) => P[2];
        public override string SignalTag => _tag;
        public override double? Prediction => _scalp.Prediction;

        private StrategyBase _scalp, _ny;
        private string _tag;

        protected override void OnInit()
        {
            Defaults(0.56, 5, 12, 60);
            _scalp = Create("SCALP_S3D"); _scalp.Attach(Bot, new[] { P[0], P[1], P[2], 1.0 });      // 1 = เฉพาะช่วงลอนดอน
            _ny = Create("NY_VALUE"); _ny.Attach(Bot, new[] { P[3], P[2], 0.0, 0.0 });
        }

        public override void OnTick() { _scalp.OnTick(); _ny.OnTick(); }
        public override void OnStop() { _scalp.OnStop(); _ny.OnStop(); }

        private bool Holding(string tag) => Bot.Positions.FindAll(BacktestLab.Label, Bot.SymbolName).Any(p => p.Comment == tag);

        public override TradeType? Signal()
        {
            var scalp = _scalp.Signal(); var ny = _ny.Signal();                  // เรียกทั้งคู่ทุกแท่ง ให้สถานะของแต่ละกฎเดินต่อเนื่อง
            if (ny != null && !Holding("NYV")) { _tag = "NYV"; return ny; }      // NYV มีโอกาสเดียวต่อวัน จึงมาก่อนถ้าตรงแท่งเดียวกัน
            if (scalp != null && !Holding("S3D")) { _tag = "S3D"; return scalp; }
            return null;
        }

        public override bool ShouldExit(Position position) =>
            position.Comment == "NYV" ? _ny.ShouldExit(position) : _scalp.ShouldExit(position);
    }
}
