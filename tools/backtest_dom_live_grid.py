import os, csv
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
                    'imbalance': float(r.get('imbalance', 0) or 0)
                })
            except: pass

raw_bars.sort(key=lambda x: x['dt'])
bars = []
seen = set()
for b in raw_bars:
    if b['time'] not in seen:
        seen.add(b['time'])
        bars.append(b)

print(f"Loaded {len(bars)} clean M1 bars")

def evaluate_config(name, lookback=30, imb_th=0.02, sl_usd=3.5, rr=2.0, spread_ceil=0.20, session_filter=None, news_filter=False):
    tp_usd = sl_usd * rr
    pos = None
    trades = []

    for i in range(lookback + 5, len(bars)):
        b = bars[i]
        
        # Manage position
        if pos is not None:
            side = pos['side']
            entry = pos['entry']
            
            hit_tp = False
            hit_sl = False
            smart_exit = False
            exit_p = b['close']
            
            if side == 'BUY':
                profit_dist = b['high'] - entry
                # Smart DOM exit after 1.0R (+3.5)
                if profit_dist >= sl_usd and b['imbalance'] <= -(imb_th * 1.5):
                    smart_exit = True
                    exit_p = b['close']
                elif b['high'] >= entry + tp_usd:
                    hit_tp = True
                    exit_p = entry + tp_usd
                elif b['low'] <= entry - sl_usd:
                    hit_sl = True
                    exit_p = entry - sl_usd
            else: # SELL
                profit_dist = entry - b['low']
                if profit_dist >= sl_usd and b['imbalance'] >= (imb_th * 1.5):
                    smart_exit = True
                    exit_p = b['close']
                elif b['low'] <= entry - tp_usd:
                    hit_tp = True
                    exit_p = entry - tp_usd
                elif b['high'] >= entry + sl_usd:
                    hit_sl = True
                    exit_p = entry + sl_usd
                    
            time_exit = (i - pos['idx'] >= 60)
            if time_exit and not (hit_tp or hit_sl or smart_exit):
                exit_p = b['close']
                
            if hit_tp or hit_sl or smart_exit or time_exit:
                raw_diff = (exit_p - entry) if side == 'BUY' else (entry - exit_p)
                cost = (pos['spread'] * 1.0) + 0.08
                net_pnl = raw_diff - cost
                trades.append({
                    'entry_time': pos['time'],
                    'exit_time': b['time'],
                    'day': pos['time'].split(' ')[0],
                    'side': side,
                    'pnl': net_pnl,
                    'win': net_pnl > 0,
                    'reason': 'TP' if hit_tp else ('SL' if hit_sl else ('SMART' if smart_exit else 'TIME'))
                })
                pos = None
                continue

        if pos is None:
            # Spread filter
            if b['spread'] > spread_ceil:
                continue
                
            # Session filter
            hr = b['dt'].hour
            if session_filter == 'LONDON_NY' and not (8 <= hr < 21):
                continue
            if session_filter == 'NY_ONLY' and not (13 <= hr < 21):
                continue
            if session_filter == 'ASIA_ONLY' and not (0 <= hr < 8):
                continue
                
            # News filter
            if news_filter:
                dt_str = b['time']
                is_fomc = ('2026-11-05 18:00' <= dt_str <= '2026-11-05 22:00')
                is_nfp = ('2026-11-06 12:30' <= dt_str <= '2026-11-06 16:30')
                if is_fomc or is_nfp:
                    continue
                
            window = bars[i-lookback:i]
            rolling_high = max(x['high'] for x in window)
            rolling_low = min(x['low'] for x in window)
            tol = (rolling_high - rolling_low) * 0.08
            
            if b['close'] <= rolling_low + tol and b['imbalance'] >= imb_th:
                pos = {'side': 'BUY', 'entry': b['close'], 'time': b['time'], 'idx': i, 'spread': b['spread']}
            elif b['close'] >= rolling_high - tol and b['imbalance'] <= -imb_th:
                pos = {'side': 'SELL', 'entry': b['close'], 'time': b['time'], 'idx': i, 'spread': b['spread']}

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
    
    print(f"=== {name} ===")
    print(f"Trades: {len(trades)} | Win Rate: {wr:.1f}% ({len(wins)}W / {len(losses)}L)")
    print(f"Profit Factor: {pf:.2f} | Net PnL: ${tot:.2f} USD")
    print(f"Daily Win Rate: {dwr:.1f}% ({pos_days}/{total_days} positive days)")
    for d in sorted(daily.keys()):
        tag = "+ " if daily[d] > 0 else "- "
        print(f"   {d}: {tag}${abs(daily[d]):.2f} USD")
    print()

evaluate_config("1. Pure Live DomWallAbsorption (Baseline cBot: SL $3.5, TP $7.0)", sl_usd=3.5, rr=2.0)
evaluate_config("2. DomWallAbsorption + News Void Filter", sl_usd=3.5, rr=2.0, news_filter=True)
evaluate_config("3. DomWallAbsorption + London/NY Sessions (8-21 UTC) + News Void", sl_usd=3.5, rr=2.0, session_filter='LONDON_NY', news_filter=True)
evaluate_config("4. DomWallAbsorption + NY Active Window (13-21 UTC) + News Void", sl_usd=3.5, rr=2.0, session_filter='NY_ONLY', news_filter=True)
evaluate_config("5. DomWallAbsorption (SL $2.5, TP $5.0 - Tight Risk) + NY Window", sl_usd=2.5, rr=2.0, session_filter='NY_ONLY', news_filter=True)
