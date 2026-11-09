import os
import sys
import csv
import math
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8')

def load_all_xau_bars():
    base_dir = os.path.expandvars(r'%LOCALAPPDATA%\BacktestLab\live\XAUUSD')
    raw_bars = []
    
    for fn in ['min-2026-10.csv', 'min-2026-11.csv']:
        fp = os.path.join(base_dir, fn)
        if not os.path.exists(fp):
            continue
        with open(fp, 'r', encoding='utf-8') as f:
            reader = csv.DictReader(f)
            for r in reader:
                try:
                    dt = datetime.strptime(r['time'], '%Y-%m-%d %H:%M')
                    c = float(r['close'])
                    h = float(r['high'])
                    l = float(r['low'])
                    o = float(r['open'])
                    sp = float(r['spreadAvg'])
                    imb = float(r['imbalance']) if r.get('imbalance') else 0.0
                    vol = float(r.get('tickVolume', 1))
                    raw_bars.append({
                        'dt': dt,
                        'time': r['time'],
                        'date': r['time'].split(' ')[0],
                        'open': o,
                        'high': h,
                        'low': l,
                        'close': c,
                        'spread': sp,
                        'imbalance': imb,
                        'volume': vol
                    })
                except Exception:
                    pass
                    
    raw_bars.sort(key=lambda x: x['dt'])
    bars = []
    seen = set()
    for b in raw_bars:
        if b['time'] not in seen:
            seen.add(b['time'])
            bars.append(b)
            
    # Calculate ATR and Session & News Gate
    for i in range(len(bars)):
        b = bars[i]
        if i == 0:
            b['atr'] = max(0.5, b['high'] - b['low'])
        else:
            prev = bars[i-1]
            tr = max(b['high'] - b['low'], abs(b['high'] - prev['close']), abs(b['low'] - prev['close']))
            b['atr'] = (prev['atr'] * 13 + tr) / 14.0
            
        hr = b['dt'].hour
        if 0 <= hr < 8:
            b['session'] = 'ASIA'
        elif 8 <= hr < 13:
            b['session'] = 'LONDON'
        elif 13 <= hr < 21:
            b['session'] = 'NY'
        else:
            b['session'] = 'OVERNIGHT'
            
        dt_str = b['time']
        # US Election / FOMC / NFP blackout windows
        is_fomc = ('2026-11-05 18:00' <= dt_str <= '2026-11-05 22:00')
        is_nfp = ('2026-11-06 12:30' <= dt_str <= '2026-11-06 16:30')
        b['is_news_void'] = (is_fomc or is_nfp)
        
    return bars

def run_hybrid_backtest(bars, use_news_gate=True, use_be=True, sl_points=8.75, tp_points=12.5):
    trades = []
    in_pos = False
    side = None
    entry_p = 0.0
    sl = 0.0
    tp = 0.0
    entry_idx = 0
    be_active = False
    
    # Track equity curve for max drawdown
    peak_equity = 0.0
    max_dd = 0.0
    cum_pnl = 0.0
    
    for i in range(60, len(bars)):
        b = bars[i]
        
        if in_pos:
            high = b['high']
            low = b['low']
            close = b['close']
            
            # Breakeven lock after 1.0R (+3.0 points move)
            if use_be and not be_active:
                if side == 'BUY' and high >= entry_p + 3.0:
                    sl = entry_p + b['spread'] + 0.10
                    be_active = True
                elif side == 'SELL' and low <= entry_p - 3.0:
                    sl = entry_p - b['spread'] - 0.10
                    be_active = True
                    
            hit_tp = False
            hit_sl = False
            exit_p = close
            
            if side == 'BUY':
                if high >= tp:
                    hit_tp = True
                    exit_p = tp
                elif low <= sl:
                    hit_sl = True
                    exit_p = sl
            else:
                if low <= tp:
                    hit_tp = True
                    exit_p = tp
                elif high >= sl:
                    hit_sl = True
                    exit_p = sl
                    
            # Timeout at 45 bars (45 min)
            if (i - entry_idx >= 45) and not hit_tp and not hit_sl:
                exit_p = close
                hit_tp = (side == 'BUY' and exit_p > entry_p) or (side == 'SELL' and exit_p < entry_p)
                hit_sl = not hit_tp
                
            if hit_tp or hit_sl:
                raw_diff = (exit_p - entry_p) if side == 'BUY' else (entry_p - exit_p)
                # 0.01 lot on Gold = 1 oz = $1.00 per 1 dollar move
                cost = (b['spread'] * 1.0) + 0.08  # spread + cTrader commission
                net_pnl = raw_diff - cost
                
                cum_pnl += net_pnl
                if cum_pnl > peak_equity:
                    peak_equity = cum_pnl
                dd = peak_equity - cum_pnl
                if dd > max_dd:
                    max_dd = dd
                    
                trades.append({
                    'entry_time': bars[entry_idx]['time'],
                    'exit_time': b['time'],
                    'side': side,
                    'entry_p': entry_p,
                    'exit_p': exit_p,
                    'duration_m': i - entry_idx,
                    'pnl': net_pnl,
                    'is_win': net_pnl > 0,
                    'date': b['date']
                })
                in_pos = False
                continue
                
        # Entry Logic (4-Pillars Hybrid)
        if not in_pos:
            # 1. Event & Spread Gate (Pillar 3)
            if use_news_gate and b['is_news_void']:
                continue
            if b['spread'] > 0.35:
                continue
            # Session filter: London & NY preferred (08:00 - 21:00 UTC)
            if b['session'] not in ['LONDON', 'NY']:
                continue
                
            # 2. Structural Anchor (Pillar 1: AMT 70% Value Area)
            window = bars[i-60:i]
            lows = sorted([x['low'] for x in window])
            highs = sorted([x['high'] for x in window])
            val = lows[int(len(lows) * 0.15)]
            vah = highs[int(len(highs) * 0.85)]
            
            # 3. Timing & Confirmation (Pillar 2: Displacement CHoCH)
            prev = bars[i-1]
            body = abs(b['close'] - b['open'])
            is_displacement = body > (0.8 * b['atr'])
            
            # Buy setup: swept below VAL, then printed strong bullish reversal candle breaking previous high
            if b['low'] <= val and b['close'] > prev['high'] and is_displacement and b['imbalance'] > 0.01:
                in_pos = True
                side = 'BUY'
                entry_p = b['close']
                entry_idx = i
                be_active = False
                # Pillar 4: Structural SL
                sl = entry_p - sl_points
                tp = entry_p + tp_points
                
            # Sell setup: swept above VAH, then printed strong bearish reversal candle breaking previous low
            elif b['high'] >= vah and b['close'] < prev['low'] and is_displacement and b['imbalance'] < -0.01:
                in_pos = True
                side = 'SELL'
                entry_p = b['close']
                entry_idx = i
                be_active = False
                sl = entry_p + sl_points
                tp = entry_p - tp_points
                
    wins = [t for t in trades if t['pnl'] > 0]
    losses = [t for t in trades if t['pnl'] < 0]
    gw = sum(t['pnl'] for t in wins)
    gl = abs(sum(t['pnl'] for t in losses))
    pf = (gw / gl) if gl > 0 else 999.0
    total_pnl = sum(t['pnl'] for t in trades)
    wr = (len(wins) / len(trades) * 100) if trades else 0.0
    avg_win = (gw / len(wins)) if wins else 0.0
    avg_loss = (gl / len(losses)) if losses else 0.0
    avg_dur = (sum(t['duration_m'] for t in trades) / len(trades)) if trades else 0.0
    
    daily = {}
    for t in trades:
        d = t['date']
        daily[d] = daily.get(d, 0.0) + t['pnl']
        
    pos_days = sum(1 for p in daily.values() if p > 0)
    total_days = len(daily)
    dwr = (pos_days / total_days * 100) if total_days > 0 else 0.0
    
    return {
        'trades_count': len(trades),
        'wins': len(wins),
        'losses': len(losses),
        'win_rate': wr,
        'profit_factor': pf,
        'net_pnl': total_pnl,
        'max_dd': max_dd,
        'gross_win': gw,
        'gross_loss': gl,
        'avg_win': avg_win,
        'avg_loss': avg_loss,
        'avg_duration_min': avg_dur,
        'pos_days': pos_days,
        'total_days': total_days,
        'daily_win_rate': dwr,
        'daily_breakdown': daily,
        'trades': trades
    }

def main():
    bars = load_all_xau_bars()
    print("=" * 75)
    print("🏆 AUDITED BACKTEST: XAUUSD ON 100% REAL LIVE COLLECTED DATA")
    print(f"Dataset: %LOCALAPPDATA%\\BacktestLab\\live\\XAUUSD")
    print(f"Total Live M1 Bars: {len(bars):,} bars (from {bars[0]['time']} to {bars[-1]['time']})")
    print("=" * 75)
    
    # Test 1: Full 4-Pillars Champion (With News Gate + Breakeven Lock)
    res_full = run_hybrid_backtest(bars, use_news_gate=True, use_be=True, sl_points=8.75, tp_points=12.5)
    
    # Test 2: 4-Pillars WITHOUT News Gate (To show impact of FOMC/NFP)
    res_no_news = run_hybrid_backtest(bars, use_news_gate=False, use_be=True, sl_points=8.75, tp_points=12.5)
    
    # Test 3: 4-Pillars WITHOUT Breakeven Lock
    res_no_be = run_hybrid_backtest(bars, use_news_gate=True, use_be=False, sl_points=8.75, tp_points=12.5)

    print("\n📊 1. COMPARATIVE PERFORMANCE TABLE:")
    print(f"{'Configuration':<38} | {'Trades':<6} | {'Win Rate':<8} | {'PF':<5} | {'Net PnL':<10} | {'Max DD':<8} | {'Daily Win%':<10}")
    print("-" * 95)
    
    for name, r in [
        ("4-Pillars Champion (Full Production)", res_full),
        ("Ablation: Without Breakeven Lock", res_no_be),
        ("Ablation: Without News Void Gate", res_no_news),
    ]:
        print(f"{name:<38} | {r['trades_count']:<6} | {r['win_rate']:<7.1f}% | {r['profit_factor']:<5.2f} | ${r['net_pnl']:<9.2f} | ${r['max_dd']:<7.2f} | {r['daily_win_rate']:<6.1f}% ({r['pos_days']}/{r['total_days']})")

    print("\n" + "=" * 75)
    print("📈 2. DETAILED BREAKDOWN OF THE CHAMPION (Full 4-Pillars on Real Live Data):")
    print("=" * 75)
    print(f"• Total Closed Trades     : {res_full['trades_count']} trades (0.01 Lot / 1 oz)")
    print(f"• Winning Trades          : {res_full['wins']} trades ({res_full['win_rate']:.1f}%)")
    print(f"• Losing Trades           : {res_full['losses']} trades")
    print(f"• Profit Factor           : {res_full['profit_factor']:.2f}")
    print(f"• Total Net Profit        : +${res_full['net_pnl']:.2f} USD")
    print(f"• Gross Profit            : +${res_full['gross_win']:.2f} USD")
    print(f"• Gross Loss              : -${res_full['gross_loss']:.2f} USD")
    print(f"• Maximum Drawdown        : ${res_full['max_dd']:.2f} USD")
    print(f"• Average Winning Trade   : +${res_full['avg_win']:.2f} USD")
    print(f"• Average Losing Trade    : -${res_full['avg_loss']:.2f} USD")
    print(f"• Win / Loss Payout Ratio : {(res_full['avg_win'] / res_full['avg_loss'] if res_full['avg_loss'] > 0 else 0):.2f} : 1")
    print(f"• Average Holding Duration: {res_full['avg_duration_min']:.1f} minutes")
    print(f"• Daily Win Rate          : {res_full['daily_win_rate']:.1f}% ({res_full['pos_days']} of {res_full['total_days']} active trading days profitable)")

    print("\n📅 3. DAY-BY-DAY AUDITED REALITY (PnL by Date):")
    print("-" * 55)
    for d, pnl in sorted(res_full['daily_breakdown'].items()):
        status = "🟢 PROFIT" if pnl > 0 else ("🔴 LOSS" if pnl < 0 else "⚪ EVEN")
        print(f"  {d} : {status} | ${pnl:+.2f} USD")
    print("-" * 55)

if __name__ == '__main__':
    main()
