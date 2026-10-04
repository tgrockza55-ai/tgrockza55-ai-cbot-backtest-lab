# Is the cBot on the Demo account recording? Shows what the live recorder (cbot\BacktestLab\LiveRecorder.cs) has written so far.
# Usage: powershell -ExecutionPolicy Bypass -File tools\live-status.ps1 [-Symbol XAUUSD] [-Test]
# The study itself: research\Phase1\bin\Release\net6.0\Phase1.exe live

param([string]$Symbol = "XAUUSD", [switch]$Test)

$inv = [Globalization.CultureInfo]::InvariantCulture
$dir = Join-Path $env:LOCALAPPDATA ("BacktestLab\" + $(if ($Test) { "live-test" } else { "live" }) + "\$Symbol")
if (-not (Test-Path $dir)) {
    Write-Host "Nothing recorded yet: $dir" -ForegroundColor Yellow
    Write-Host "Start the BacktestLab cBot on the Demo account (chart $Symbol m1, 'Record market' = Yes)."
    exit 1
}

$files = Get-ChildItem $dir -File
"Folder: $dir   ({0:N1} MB in {1} files)" -f (($files | Measure-Object Length -Sum).Sum / 1MB), $files.Count

# the last lines of a file the cBot may still be writing
function Read-Tail([string]$file, [int]$n) { if (Test-Path $file) { @(Get-Content $file -Tail $n -ErrorAction SilentlyContinue) } else { @() } }

$runs = Read-Tail (Join-Path $dir "runs.csv") 3
if ($runs.Count) { "`nStart / stop of the cBot (UTC):"; $runs | Where-Object { $_ -notmatch '^time,' } | ForEach-Object { "  $_" } }

$min = $files | Where-Object { $_.Name -like "min-*.csv" } | Sort-Object Name | Select-Object -Last 1
if ($min) {
    $last = @(Read-Tail $min.FullName 1)[0].Split(',')
    $age = ([DateTime]::UtcNow - [DateTime]::ParseExact($last[0], "yyyy-MM-dd HH:mm", $inv)).TotalMinutes
    "`nLast recorded minute: {0} UTC ({1:N0} minutes ago)" -f $last[0], $age
    "  close {0}   ticks {1}   spread avg {2} max {3}" -f $last[4], $last[6], $last[7], $last[8]
    "  depth of market: {0} snapshots, bid volume {1}, ask volume {2}, imbalance {3}" -f $last[9], $last[10], $last[11], $last[12]
    "  next news: {0} {1} in {2} min   last news: {3} {4}, {5} min ago" -f $last[14], $last[15], $last[16], $last[17], $last[18], $last[19]
    "  model P(up) {0}   signal '{1}'   open positions {2}   equity {3}" -f $(if ($last[20]) { $last[20] } else { "(warming up)" }), $last[21], $last[22], $last[23]
    if ($age -gt 5) { Write-Host "  No new minute for more than 5 minutes: market closed, or the cBot / cTrader is not running." -ForegroundColor Yellow }
}

"`nFiles per day:"
$files | Where-Object { $_.Name -match '^(tick|book)-' } | Sort-Object Name | Select-Object -Last 8 |
    ForEach-Object { "  {0,-28} {1,8:N0} KB" -f $_.Name, ($_.Length / 1KB) }
$book = $files | Where-Object { $_.Name -like "book-*.csv" } | Sort-Object Name | Select-Object -Last 1
if ($book -and $book.Length -gt 20) {
    $row = @(Read-Tail $book.FullName 1)[0].Split(',')
    if ($row.Count -ge 3) { "  last order book at {0}: {1} bid levels, {2} ask levels" -f $row[0], @($row[1].Split('|')).Count, @($row[2].Split('|')).Count }
} else { "  no depth of market recorded today (normal in a backtest; live it means the broker sends no book for this symbol)" }

$tradeFile = Join-Path $dir "trades.csv"
if (Test-Path $tradeFile) {
    $t = @(Import-Csv $tradeFile)
    $closed = @($t | Where-Object { $_.event -eq "CLOSE" })
    $net = ($closed | ForEach-Object { [double]::Parse($_.net, $inv) } | Measure-Object -Sum).Sum
    "`nOrders: {0} opened, {1} closed, {2} failed   net {3:N2} USD" -f @($t | Where-Object { $_.event -eq "OPEN" }).Count, $closed.Count, @($t | Where-Object { $_.event -eq "FAILED" }).Count, $net
    $t | Select-Object -Last 4 | ForEach-Object { "  {0}  {1,-6} {2,-4} price {3}  expected {4}  slippage {5}  {6}  net {7}" -f $_.time, $_.event, $_.side, $_.price, $_.expected, $_.slippage, $_.reason, $_.net }
} else { "`nOrders: none yet" }
