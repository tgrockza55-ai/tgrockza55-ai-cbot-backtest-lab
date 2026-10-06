// ภาพตัวอย่างวิธีทายทิศแท่ง Heikin Ashi (หน้า summary)
// ข้อมูล: docs/data/ha-examples.json — ตัวอย่างจริง 3 กรณี สร้างโดย research\Phase1  Phase1.exe study:ha
// แต่ละกรณีวาดสองจังหวะ: ① ตอนแท่งใหม่เปิด (สิ่งที่เห็น และสิ่งที่ทาย) ② เมื่อแท่งจบ (ผล)
// ไฟล์นี้ไม่พึ่ง app.js จึงเปิดดูในหน้าทดสอบ (tools/preview/ha-examples.html) ได้โดยไม่ต้องล็อกอิน

const SVG = "http://www.w3.org/2000/svg";
const s = (tag, attrs = {}, ...kids) => {
  const el = document.createElementNS(SVG, tag);
  for (const [k, v] of Object.entries(attrs)) if (v != null) el.setAttribute(k, v);
  el.append(...kids.filter((k) => k != null));
  return el;
};
const e = (tag, attrs = {}, ...kids) => {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) if (v != null) k === "class" ? (el.className = v) : el.setAttribute(k, v);
  el.append(...kids.flat().filter((k) => k != null));
  return el;
};
const price = (v) => Number(v).toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const SIDE = { 1: "ขึ้น", "-1": "ลง" };

// จอแคบ: วาดกราฟแคบลงและใช้แท่งน้อยลง ตัวหนังสือในภาพจะไม่ถูกย่อจนอ่านไม่ออก
const NARROW = typeof window !== "undefined" && window.matchMedia("(max-width: 560px)").matches;
const W = NARROW ? 350 : 440, GUTTER = NARROW ? 162 : 168, SHOWN = NARROW ? 11 : 16;
const H = 240, LEFT = 10, TOP = 14, BOTTOM = 18;

/** กราฟหนึ่งจังหวะ: done = false คือ "ตอนแท่งใหม่เปิด", true คือ "เมื่อแท่งจบ" */
function chart(ex, done) {
  const bars = ex.bars.slice(-SHOWN), n = bars.length, last = n - 1, called = bars[last];
  const lows = bars.map((b) => b[3]).concat(ex.open, ex.haOpen), highs = bars.map((b) => b[2]).concat(ex.open, ex.haOpen);
  const lo = Math.min(...lows), hi = Math.max(...highs), pad = (hi - lo) * 0.07 || 1;
  const step = (W - LEFT - GUTTER) / n, bw = step * 0.62;
  const x = (i) => LEFT + step * (i + 0.5), y = (p) => TOP + (1 - (p - (lo - pad)) / (hi - lo + 2 * pad)) * (H - TOP - BOTTOM);
  const right = ex.call === ex.result;

  const svg = s("svg", { viewBox: `0 0 ${W} ${H}`, role: "img",
    "aria-label": done
      ? `เมื่อแท่งจบ: แท่ง Heikin Ashi จบเป็นแท่ง${SIDE[ex.result]} ราคาปิดของแท่ง ${price(called[4])} ${right ? "ทายถูก" : "ทายผิด"}`
      : `ตอนแท่งใหม่เปิด: ราคาจริง ${price(ex.open)} ราคาเปิดของแท่งใหม่ ${price(ex.haOpen)} ทายว่าแท่ง${SIDE[ex.call]}` });

  // แท่งที่ปิดแล้ว (และแท่งที่ทาย เมื่อจบ)
  bars.forEach((b, i) => {
    if (i === last && !done) return;
    const up = b[4] >= b[1], top = y(Math.max(b[1], b[4])), bottom = y(Math.min(b[1], b[4]));
    svg.append(s("g", { class: up ? "cu" : "cd" },
      s("title", {}, `แท่ง HA  เปิด ${price(b[1])}  สูง ${price(b[2])}  ต่ำ ${price(b[3])}  ปิด ${price(b[4])}`),
      s("line", { x1: x(i), x2: x(i), y1: y(b[2]), y2: y(b[3]), "stroke-width": 1 }),
      s("rect", { x: x(i) - bw / 2, y: top, width: bw, height: Math.max(1.5, bottom - top), "stroke-width": 0 })));
  });

  const edge = W - GUTTER + 6;                                   // ขอบซ้ายของช่องป้าย
  const yOpen = y(ex.haOpen), yReal = y(ex.open);
  // ป้ายสองอันในช่องขวา: ถ้าระดับใกล้กันให้แยกป้ายออก แล้วลากเส้นชี้กลับไปที่ระดับจริง
  const label = (level, want, lines, strong) => {
    const yy = Math.min(H - BOTTOM - 16, Math.max(TOP + 10, want));
    svg.append(s("polyline", { class: "leader", points: `${edge - 2},${level} ${edge + 6},${yy - 4}` }));
    lines.forEach((t, k) => svg.append(s("text", { class: `lab${k === 0 && strong ? " strong" : ""}`, x: edge + 10, y: yy + k * 14 }, t)));
  };
  let labA = yOpen, labB = done ? y(called[4]) : yReal;
  if (Math.abs(labA - labB) < 34) { const mid = (labA + labB) / 2, sign = labA <= labB ? -1 : 1; labA = mid + sign * 17; labB = mid - sign * 17; }

  // ราคาเปิดของแท่งใหม่: รู้ตั้งแต่ก่อนแท่งเริ่ม
  svg.append(s("line", { class: "lvl ha-open", x1: x(last - 1.6), x2: edge - 2, y1: yOpen, y2: yOpen }));
  label(yOpen, labA, ["ราคาเปิดของแท่งใหม่", price(ex.haOpen)], false);

  if (!done) {
    // ราคาจริงตอนแท่งเปิด: วงกลมไว้
    svg.append(
      s("line", { class: "guide", x1: x(last), x2: x(last), y1: TOP, y2: H - BOTTOM }),
      s("circle", { class: "ring", cx: x(last), cy: yReal, r: 11 }),
      s("circle", { class: "dot", cx: x(last), cy: yReal, r: 4.5 }, s("title", {}, `ราคาจริงตอนแท่งเปิด ${price(ex.open)}`)),
      s("line", { class: "leader", x1: x(last) + 11, x2: edge - 2, y1: yReal, y2: yReal }));
    if (Math.abs(yReal - yOpen) >= 22) {                           // ลูกศรจากเส้นไปหาวงกลม: ราคาจริงอยู่ฝั่งไหนของเส้น
      const dir = yReal < yOpen ? -1 : 1, tip = yReal - dir * 13;
      svg.append(
        s("line", { class: "gap", x1: x(last), x2: x(last), y1: yOpen, y2: tip - dir * 5 }),
        s("polygon", { class: "gap-head", points: `${x(last)},${tip} ${x(last) - 4},${tip - dir * 7} ${x(last) + 4},${tip - dir * 7}` }));
    }
    label(yReal, labB, ["ราคาจริงตอนนี้", price(ex.open)], true);
  } else {
    // แท่งที่ทาย: ตีกรอบไว้ และยังเห็นจุดราคาจริงตอนเปิดแบบจาง
    const b = called, top = y(b[2]) - 7, bottom = y(b[3]) + 7;
    svg.append(
      s("circle", { class: "dot ghost", cx: x(last), cy: yReal, r: 3.5 }, s("title", {}, `ราคาจริงตอนแท่งเปิด ${price(ex.open)}`)),
      s("rect", { class: "ring", x: x(last) - bw / 2 - 7, y: top, width: bw + 14, height: bottom - top, rx: 7 }),
      s("line", { class: "leader", x1: x(last) + bw / 2 + 7, x2: edge - 2, y1: y(b[4]), y2: y(b[4]) }));
    label(y(b[4]), labB, [`แท่งจบ${SIDE[ex.result]} ${right ? "✓" : "✗"}`, `ราคาปิดของแท่ง ${price(b[4])}`], true);
  }
  return svg;
}

function figure(ex, index) {
  const right = ex.call === ex.result, above = ex.open > ex.haOpen, gap = Math.abs(ex.open - ex.haOpen);
  const when = new Date(ex.time * 1000).toLocaleString("th-TH", { dateStyle: "medium", timeStyle: "short" });
  const arrow = (side) => e("span", { class: side > 0 ? "pos" : "neg", "aria-hidden": "true" }, side > 0 ? "▲" : "▼");
  const step = (num, title, badge, svg, text) => e("div", { class: "ha-step" },
    e("h3", {}, e("span", { class: "num" }, num), title, e("span", { class: "ha-badge" }, badge)), svg, e("p", {}, text));
  return e("figure", { class: "card example ha-fig", style: "margin:0" },
    e("figcaption", {},
      e("b", {}, `ตัวอย่างที่ ${index + 1}: ทายว่าแท่ง${SIDE[ex.call]}`), " · ",
      e("span", { class: right ? "pos" : "neg" }, right ? "ทายถูก" : "ทายผิด"),
      e("span", { class: "muted" }, ` · ${when}`)),
    e("div", { class: "ha-steps" },
      step("1", "ตอนแท่งใหม่เปิด", ["ทายว่า ", arrow(ex.call), ` แท่ง${SIDE[ex.call]}`], chart(ex, false), [
        `ราคาจริง ${price(ex.open)} `, e("b", {}, above ? "สูงกว่า" : "ต่ำกว่า"), `ราคาเปิดของแท่งใหม่ ${price(ex.haOpen)} (ห่าง ${price(gap)} ดอลลาร์) → `,
        e("b", {}, `ทายว่าแท่งนี้จะจบเป็นแท่ง${SIDE[ex.call]}`)]),
      step("2", "เมื่อแท่งจบ (1 นาทีต่อมา)", ["ผล ", arrow(ex.result), ` แท่ง${SIDE[ex.result]} · ${right ? "ทายถูก ✓" : "ทายผิด ✗"}`], chart(ex, true), right
        ? [`แท่งจบเป็นแท่ง${SIDE[ex.result]}ตามที่ทาย: ราคาปิดของแท่ง ${price(ex.bars.at(-1)[4])} อยู่${ex.result > 0 ? "เหนือ" : "ใต้"}ราคาเปิดของแท่ง`]
        : [`ราคาพลิกกลับระหว่างนาที แท่งจบเป็นแท่ง${SIDE[ex.result]} `, e("b", {}, "ทายผิด"),
           ` — ตอนทาย ราคาห่างจากเส้นแค่ ${price(gap)} ดอลลาร์ ยิ่งห่างน้อยยิ่งทายผิดง่าย`])));
}

/** วาดตัวอย่างทั้งหมดลงใน target (คืน false ถ้าไม่มีข้อมูล) */
export async function renderHaExamples(target, url = "data/ha-examples.json") {
  let data;
  try { const res = await fetch(url); if (!res.ok) return false; data = await res.json(); } catch { return false; }
  const list = data?.examples ?? [];
  if (!list.length) return false;
  target.replaceChildren(
    e("h2", {}, "ตัวอย่างจากข้อมูลจริง: เห็นอะไร ทายว่าอะไร แล้วผลเป็นอย่างไร"),
    e("p", { class: "muted ha-legend" },
      e("span", {}, e("i", { class: "k-ring" }), "วงกลม = ราคาจริงตอนแท่งเปิด"),
      e("span", {}, e("i", { class: "k-dash" }), "เส้นประ = ราคาเปิดของแท่ง Heikin Ashi ใหม่ (รู้ก่อนแท่งเริ่ม)"),
      e("span", {}, e("i", { class: "k-box" }), "กรอบ = แท่งที่ทาย")),
    e("div", { class: "ha-figs" }, list.map(figure)));
  return true;
}
