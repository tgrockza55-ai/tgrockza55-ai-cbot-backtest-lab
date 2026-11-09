import sys
sys.path.append('tools')
from backtest_xau_live_collected_data import load_all_xau_bars, run_hybrid_backtest

bars = load_all_xau_bars()
res = run_hybrid_backtest(bars, use_news_gate=True, use_be=True)
print(f"Total Trades: {len(res['trades'])}")
for t in res['trades']:
    win_tag = "WIN " if t['is_win'] else "LOSS"
    print(f"{t['entry_time']} -> {t['exit_time']} | {t['side']} {t['entry_p']:.2f} -> {t['exit_p']:.2f} | {win_tag} ${t['pnl']:.2f} ({t['duration_m']}m)")
