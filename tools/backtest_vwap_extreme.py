import os, csv, math
from datetime import datetime

base_dir = os.path.expandvars(r'%LOCALAPPDATA%\BacktestLab\live\XAUUSD')
raw_bars = []
for fn in ['min-2026-10.csv', 'min-2026-11.csv']:
    fp = os.path.join(base_dir, fn)
    if not os.path.exists(fp): continue
    with open(fp, 'r', encoding='utf-8') as f:
        reader = csv.DictReader(f)
        for r in reader:
            try:
                raw_bars.append({
                    'dt': datetime.strptime(r['time'], '%Y-%m-%d %H:%M'),
                    'time': r['time'],
                    'open': float(r['open']),
                    'high': float(r['high']),
                    'low': float(r['low']),
                    'close': float(r['close']),
                    'spread': float(r['spreadAvg']),
                    'volume': max(float(r.get('tickVolume', 1) or 1), 1.0)
                })
            except: pass

raw_bars.sort(key=lambda x: x['dt'])
bars = []
seen = set()
for b in raw_bars:
    if b['time'] not in seen:
        seen.add(b['time'])
        bars.append(b)

# Simulate QuantExtremeReversal (Anchored VWAP 3.8 SD + SL 2.0 ATR + TP to VWAP)
def evaluate_quant_extreme(sd_mult=3.8, sl_atr_mult=2.0, max_hold=60, start_hr=13, end_hr=17):
    current_day = None
    sum_pv = 0.0
    sum_p2v = 0.0
    sum_v = 0.0
    cur_vwap = 0.0
    cur_sd = 1.0
    cur_atr = 1.5
    
    pos = None
    trades = []
    
    for i in range(1, len(bars)):
        b = bars[i]
        prev = bars[i-1]
        
        # Day reset
        b_day = b['dt'].date()
        if b_day != current_day:
            current_day = b_day
            sum_pv = 0.0
            sum_p2v = 0.0
            sum_v = 0.0
            cur_vwap = 0.0
            cur_sd = 1.0
            
        p = (b['high'] + b['low'] + b['close']) / 3.0
        v = b['volume']
        sum_pv += p * v
        sum_p2v += (p * p) * v
        sum_v += v
        
        if sum_v > 0:
            cur_vwap = sum_pv / sum_v
            var = max((sum_p2v / sum_v) - (cur_vwap * cur_vwap), 0.01)
            cur_sd = math.sqrt(var)
            
        tr = max(b['high'] - b['low'], abs(b['high'] - prev['close']), abs(b['low'] - prev['close']))
        cur_atr = 0.95 * cur_atr + 0.05 * tr
        
        # Manage position
        if pos is not None:
            side = pos['side']
            entry = pos['entry']
            sl = pos['sl']
            tp = cur_vwap # dynamic target to VWAP
            
            hit_tp = False
            hit_sl = False
            time_exit = False
            exit_p = b['close']
            
            if side == 'BUY':
                if b['high'] >= tp:
                    hit_tp = True
                    exit_p = tp
                elif b['low'] <= sl:
                    hit_sl = True
                    exit_p = sl
            else: # SELL
                if b['low'] <= tp:
                    hit_tp = True
                    exit_p = tp
                elif b['high'] >= sl:
                    hit_sl = True
                    exit_p = sl
                    
            if (i - pos['idx'] >= max_hold) and not hit_tp and not hit_sl:
                time_exit = True
                exit_p = b['close']
                
            if hit_tp or hit_sl or time_exit:
                raw_diff = (exit_p - entry) if side == 'BUY' else (entry - exit_p)
                cost = (pos['spread'] * 1.0) + 0.08
                net_pnl = raw_diff - cost
                trades.append({
                    'entry_time': pos['time'],
                    'exit_time': b['time'],
                    'day': str(b_day),
                    'side': side,
                    'pnl': net_pnl,
                    'win': net_pnl > 0,
                    'reason': 'TP' if hit_tp else ('SL' if hit_sl else 'TIME')
                })
                pos = None
                continue

        if pos is None:
            # Session filter
            hr = b['dt'].hour
            if not (start_hr <= hr <= end_hr):
                continue
                
            # Volatility void gate
            if (b['high'] - b['low']) > 2.0 * cur_atr:
                continue
                
            dev = b['close'] - cur_vwap
            th = sd_mult * max(cur_sd, 1.0)
            sl_dist = sl_atr_mult * max(cur_atr, 1.2)
            
            # BUY
            if dev <= -th:
                lower_rejection = (b['close'] > b['low'] + 0.25 * (b['high'] - b['low']))
                if lower_rejection:
                    pos = {'side': 'BUY', 'entry': b['close'], 'sl': b['close'] - sl_dist, 'time': b['time'], 'idx': i, 'spread': b['spread']}
            # SELL
            elif dev >= th:
                upper_rejection = (b['close'] < b['high'] - 0.25 * (b['high'] - b['low']))
                if upper_rejection:
                    pos = {'side': 'SELL', 'entry': b['close'], 'sl': b['close'] + sl_dist, 'time': b['time'], 'idx': i, 'spread': b['spread']}

    wins = [t for t in trades if t['win']]
    losses = [t for t in trades if not t['win']]
    gw = sum(t['pnl'] for t in wins)
    gl = abs(sum(t['pnl'] for t in losses))
    pf = (gw / gl) if gl > 0 else 999.0
    tot = sum(t['pnl'] for t in trades)
    wr = len(wins) / len(trades) * 100 if trades else 0
    
    daily = {}
    for t in trades:
        d = t['day']
        daily[d] = daily.get(d, 0.0) + t['pnl']
    pos_days = sum(1 for p in daily.values() if p > 0)
    total_days = len(daily)
    dwr = pos_days / total_days * 100 if total_days else 0
    
    print(f"=== QuantExtremeReversal (SD={sd_mult}, SL={sl_atr_mult} ATR, {start_hr}:00-{end_hr}:00 UTC) ===")
    print(f"Trades: {len(trades)} | Win Rate: {wr:.1f}% ({len(wins)}W / {len(losses)}L)")
    print(f"Profit Factor: {pf:.2f} | Net PnL: ${tot:.2f} USD")
    print(f"Daily Win Rate: {dwr:.1f}% ({pos_days}/{total_days} positive days)")
    for d in sorted(daily.keys()):
        tag = "+ " if daily[d] > 0 else "- "
        print(f"   {d}: {tag}${abs(daily[d]):.2f} USD")
    print()

evaluate_quant_extreme(sd_mult=3.8, sl_atr_mult=2.0)
evaluate_quant_extreme(sd_mult=3.0, sl_atr_mult=2.0)
evaluate_quant_extreme(sd_mult=2.5, sl_atr_mult=2.0)
