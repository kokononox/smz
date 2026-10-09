<#
  wake-test.ps1 — تست بیدارکردن ویندوز از خواب با برد Pico

  استفاده:
    wake-test.cmd         ← فقط بیدارکردن (امن: ماکرو شروع نمی‌شود)
    wake-test.cmd full    ← بیدارکردن + شروع ماکرو

  پیش از اجرا Classroom Studio را کامل ببند، چون پورت COM برد را نگه می‌دارد.
#>
param([string]$Mode = '')

$ErrorActionPreference = 'Continue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
try { Add-Type -AssemblyName System.IO.Ports -ErrorAction Stop } catch { }

function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }
$logPath = Join-Path (Get-Location) 'wake-log.txt'
function Add-Log($text) {
    try { Add-Content -LiteralPath $logPath -Value $text -Encoding UTF8 } catch { }
}
function SayLines($text) {
    foreach ($raw in ($text -split "`r?`n")) {
        $line = $raw.Trim()
        if ($line.Length -gt 0) { Say ('   ' + $line) 'White'; Add-Log $line }
    }
}
try { Set-Content -LiteralPath $logPath -Value ('# wake-test ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) -Encoding UTF8 } catch { }

Say ''
Say '=====================================================' 'White'
Say '  تست بیدارکردن ویندوز از خواب — برد Pico' 'White'
Say '=====================================================' 'White'
Say ''

# ---- ۱) پیدا کردن پورت Pico (با اثر انگشت PING) ----
$ports = @()
try { $ports = [System.IO.Ports.SerialPort]::GetPortNames() } catch { }
if ($ports.Count -eq 0) {
    Say 'هیچ پورت COM پیدا نشد.' 'Red'
    Say 'کابل USB برد Pico را چک کن و دوباره اجرا کن.' 'Yellow'
    return
}
Say ('پورت‌های COM این سیستم: ' + ($ports -join ', '))

$pico = $null
foreach ($name in ($ports | Sort-Object)) {
    $sp = New-Object System.IO.Ports.SerialPort($name, 115200)
    $sp.ReadTimeout = 300
    $sp.WriteTimeout = 1000
    $sp.DtrEnable = $true
    try {
        $sp.Open()
        Start-Sleep -Milliseconds 250
        $sp.DiscardInBuffer()
        $sp.Write("PING`r`n")
        $buffer = ''
        $until = (Get-Date).AddMilliseconds(1500)
        while ((Get-Date) -lt $until) {
            try { if ($sp.BytesToRead -gt 0) { $buffer += $sp.ReadExisting() } } catch { }
            Start-Sleep -Milliseconds 50
        }
        if ($buffer -match 'role=brain') {
            $pico = $sp
            $script:portName = $name
            Say ('برد Pico روی ' + $name + ' پیدا شد.') 'Green'
            SayLines $buffer
            break
        }
        $sp.Close()
    } catch {
        try { $sp.Close() } catch { }
    }
}

if ($null -eq $pico) {
    Say ''
    Say 'برد Pico روی هیچ پورتی جواب نداد.' 'Red'
    Say '۱) Classroom Studio را کامل ببند (پورت COM را آزاد می‌کند).' 'Yellow'
    Say '۲) کابل USB برد را جدا و دوباره وصل کن.' 'Yellow'
    Say '۳) اگر برد تازه فلش شده، چند ثانیه صبر کن و دوباره اجرا کن.' 'Yellow'
    return
}

function Send-Cmd($text) { try { $pico.Write($text + "`r`n") } catch { } }
function Drain([int]$milliseconds) {
    $out = ''
    $until = (Get-Date).AddMilliseconds($milliseconds)
    while ((Get-Date) -lt $until) {
        try { if ($pico.BytesToRead -gt 0) { $out += $pico.ReadExisting() } } catch { }
        Start-Sleep -Milliseconds 50
    }
    return $out
}

# ---- ۲) وضعیت فعلی برد ----
Say ''
Say 'وضعیت فعلی برد:' 'Cyan'
Send-Cmd 'STATUS'
SayLines (Drain 700)

# ---- ۳) مسلح کردن بیداری ----
$dry = ($Mode -ne 'full')
$seconds = 60
$command = 'WAKE!' + $seconds
if ($dry) { $command = $command + '!dry' }

Say ''
Say ('ارسال دستور: ' + $command) 'Cyan'
Send-Cmd $command
SayLines (Drain 900)

Say ''
if ($dry) {
    Say 'حالت امن: سیستم را بیدار می‌کند و قفل صفحه را هم برمی‌دارد،' 'Green'
    Say 'ولی ماکرو را شروع نمی‌کند.' 'Green'
} else {
    Say 'حالت کامل: بعد از بیداری، ماکرو روی دسکتاپ کار می‌کند.' 'Yellow'
}
Say ''
Say 'اگر سیستم بیدار نشد، بعد از پایان تست یک کلید بزن تا دستی بیدار شود؛' 'DarkGray'
Say 'برد لاگ زمان خواب را ذخیره کرده و بلافاصله می‌فرستد (خطوط RPL).' 'DarkGray'
Say ''
Say 'حالا سیستم را بخوابان:   Start  >  Sleep' 'White'
Say ('حدود ' + $seconds + ' ثانیه بعد باید خودش روشن شود.') 'White'
Say 'این پنجره را باز بگذار؛ لاگ‌ها همین‌جا می‌آیند.' 'White'
Say ''

# ---- ۴) تماشای لاگ برد ----
$start = Get-Date
$limit = $start.AddSeconds($seconds + 180)
$sawPulse = $false
$woke = $false
$sawStart = $false
$dismisses = 0
$stalled = $false
$reopenTries = 0
$sawReplay = $false
$lastNotice = 0
$pulseAt = $null
$hostUpAt = $null
$firstEnterAt = $null
$startAt = $null
$settleMs = 0

while ((Get-Date) -lt $limit) {
    $chunk = ''
    try {
        if ($null -ne $pico -and $pico.IsOpen -and $pico.BytesToRead -gt 0) { $chunk = $pico.ReadExisting() }
    } catch {
        try { $pico.Close() } catch { }
    }
    if ($chunk.Length -gt 0) {
        foreach ($raw in ($chunk -split "`r?`n")) {
            $line = $raw.Trim()
            if ($line.Length -eq 0) { continue }
            Say (('[' + (Get-Date).ToString('HH:mm:ss') + ']  ' + $line)) 'White'
            Add-Log (('[' + (Get-Date).ToString('HH:mm:ss') + ']  ' + $line))
            if ($line -match 'state=pulse') { $sawPulse = $true; if (-not $pulseAt) { $pulseAt = Get-Date } }
            if ($line -match 'state=host-up|HOSTUSB\|UP') { $woke = $true; if (-not $hostUpAt) { $hostUpAt = Get-Date } }
            if ($line -match 'state=start') { $sawStart = $true; if (-not $startAt) { $startAt = Get-Date } }
            if ($line -match 'settle-ms=(\d+)') { $settleMs = [int]$matches[1] }
            if ($line -match 'state=dismiss\|step=enter') { $dismisses = $dismisses + 1; if (-not $firstEnterAt) { $firstEnterAt = Get-Date } }
            if ($line -match 'dismiss\|stall') { $stalled = $true }
            if ($line -match '^RPL\|') { $sawReplay = $true }
        }
    }
    # The COM port can disappear while the machine is asleep; the board comes
    # back on the same name, so reopen it instead of losing the whole log.
    if ($null -eq $pico -or -not $pico.IsOpen) {
        if ($reopenTries -lt 40) {
            $reopenTries = $reopenTries + 1
            try {
                $sp = New-Object System.IO.Ports.SerialPort($script:portName, 115200)
                $sp.ReadTimeout = 300
                $sp.WriteTimeout = 1000
                $sp.DtrEnable = $true
                $sp.Open()
                $pico = $sp
                Add-Log ('# port reopened (' + $script:portName + ')')
            } catch { }
        }
    }
    if ($woke -and $sawStart) { break }
    $elapsed = [int]((Get-Date) - $start).TotalSeconds
    if ($elapsed -ge ($lastNotice + 20)) {
        $lastNotice = $elapsed
        if (-not $woke) { Say ('... ' + $elapsed + ' ثانیه گذشت، منتظر بیداری') 'DarkGray' }
    }
    Start-Sleep -Milliseconds 200
}

# ---- ۵) نتیجه ----
Say ''
Say '-----------------------------------------------------' 'White'
if ($woke) {
    Say 'نتیجه: سیستم بیدار شد   [OK]' 'Green'
} elseif ($sawPulse) {
    Say 'نتیجه: پالس فرستاده شد ولی سیستم بیدار نشد   [FAIL]' 'Red'
    Say 'یعنی remote wake-up روی این سیستم مسلح نیست. چک کن:' 'Yellow'
    Say '  - Device Manager > کیبورد Pico > Power Management > Allow this device to wake the computer' 'Yellow'
    Say '  - بایوس > USB Wake Support = Enabled و ErP / Deep Sleep = Disabled' 'Yellow'
} else {
    Say 'نتیجه: هیچ پالسی دیده نشد   [FAIL]' 'Red'
    Say 'اگر سیستم واقعاً خواب بود و پیام پالس هم نیامد، لاگ را برایم بفرست.' 'Yellow'
}

Say ''
Say 'تشخیص گام‌به‌گام:' 'Cyan'
if ($sawPulse) { Say '  پالس بیداری فرستاده شد        : بله' 'White' } else { Say '  پالس بیداری فرستاده شد        : نه' 'Red' }
if ($woke) { Say '  سیستم برگشت (host-up)          : بله' 'White' } else { Say '  سیستم برگشت (host-up)          : نه' 'Red' }
Say ('  اینترهای برداشتن قفل           : ' + $dismisses) 'White'
if ($stalled) {
    Say '  گیر کردن روی قفل (stall)       : بله — فریم‌ور بعد از مهلت رد شد' 'Yellow'
}
if ($sawStart) { Say '  ماکرو شروع شد                  : بله' 'White' } else { Say '  ماکرو شروع شد                  : نه' 'Yellow' }
if ($settleMs -gt 0) { Say ('  مهلت نشستن (settle-ms)         : ' + $settleMs + ' میلی‌ثانیه') 'White' }
if ($pulseAt -and $firstEnterAt) { Say ('  پالس تا اینتر اول              : ' + [int]($firstEnterAt - $pulseAt).TotalSeconds + ' ثانیه') 'White' }
if ($pulseAt -and $startAt) { Say ('  پالس تا شروع ماکرو             : ' + [int]($startAt - $pulseAt).TotalSeconds + ' ثانیه') 'White' }
if ($sawReplay) { Say '  لاگ زمان خواب (RPL)            : بله — در فایل ذخیره شده' 'White' }

Start-Sleep -Seconds 3
Say ''
Say 'وضعیت نهایی برد:' 'Cyan'
Send-Cmd 'STATUS'
$final = Drain 900
SayLines $final

if ($final -match 'pico-rw=(\d)') {
    if ($matches[1] -eq '1') {
        Say 'pico-rw=1 → ویندوز بیدارکردن از طرف خود Pico را مسلح کرده است.' 'Green'
    } else {
        Say 'pico-rw=0 → ویندوز بیدارکردن از طرف Pico را مسلح نکرده (مسیر آردوینو پشتیبان است).' 'Yellow'
    }
}
if ($final -match 'wake-recovery=(\d+)') {
    if ([int]$matches[1] -gt 0) {
        Say ('wake-recovery=' + $matches[1] + ' → پالس بازیابی بعد از ری‌استارت Pico مصرف شده است.') 'Yellow'
    }
}

try { $pico.Close() } catch { }
Say ''
Say ('لاگ کامل ذخیره شد در: ' + $logPath) 'Cyan'
Say 'اگر نتیجه درست نبود، همین فایل wake-log.txt را برای من بفرست.' 'Cyan'
Say ''
Say 'پایان. کلید را بزن تا بسته شود.' 'White'
