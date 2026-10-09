<#
  wake-test.ps1 — تست بیدارکردن ویندوز از خواب با برد Pico

  استفاده:
    wake-test.cmd         ← فقط بیدارکردن (امن: ماکرو شروع نمی‌شود)
    wake-test.cmd full    ← بیدارکردن + شروع ماکرو
    wake-test.cmd window  ← تست پنجرهٔ واقعی شیفت: اول چک می‌کند برد روی کدام
                            ساعت مسلح است، بعد خودش سیستم را می‌خواباند و
                            منتظر بیداری واقعی می‌ماند

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

# ویندوز فقط وقتی روی صفحهٔ قفل است پروسهٔ LogonUI را بالا نگه می‌دارد، پس بهترین
# نشانهٔ عینی برای «قفل باز شد یا نه» همین است؛ عکس صفحه هم شاهد دوم است.
function Test-Locked {
    try {
        $p = Get-Process -Name LogonUI -ErrorAction SilentlyContinue
        if ($null -ne $p) { return $true }
    } catch { }
    return $false
}
function Save-Shot($path) {
    # A locked or secure desktop, a headless session or an assembly that will not
    # load must never cost the report, so every failure here is swallowed.
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Stop'
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose()
        $bmp.Dispose()
        return $true
    } catch { return $false }
    finally { $ErrorActionPreference = $previous }
}

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
$status = Drain 700
SayLines $status
Say ''
Say 'وضعیت مهلت بیداری روی برد:' 'Cyan'
Send-Cmd 'WAKE?'
$wakeState = Drain 700
SayLines $wakeState

# ---- ۳) مسلح کردن بیداری ----
$window = ($Mode -eq 'window')
$dry = -not (($Mode -eq 'full') -or $window)
$seconds = 60
$command = 'WAKE!' + $seconds
if ($dry) { $command = $command + '!dry' }

if ($window) {
    # مهلت واقعی را نمونهٔ ساعتِ پل مسلح می‌کند، نه یک دستور دستی؛ پس قبل از
    # خواباندن سیستم می‌خوانیم برد واقعاً منتظر چه ساعتی است.
    $armed = $false
    $synced = $false
    $targetMinute = 0
    if ($status -match 'wake=(\d)') { $armed = ($matches[1] -eq '1') }
    if ($status -match 'wake-synced=(\d)') { $synced = ($matches[1] -eq '1') }
    if ($status -match 'wake-target=(\d\d):(\d\d)') { $targetMinute = ([int]$matches[1] * 60) + [int]$matches[2] }
    if ((-not $armed) -or (-not $synced) -or ($targetMinute -le 0)) {
        Say ''
        Say 'برد هنوز روی پنجرهٔ واقعی شیفت مسلح نیست.' 'Red'
        Say 'یعنی نمونهٔ ساعت از پل به برد نرسیده است (wake-target=00:00).' 'Yellow'
        Say 'یک بار Classroom Studio را باز کن تا چک هویت شیفت انجام شود و ساعت' 'Yellow'
        Say 'برسد؛ بعد دوباره wake-test.cmd window را اجرا کن.' 'Yellow'
        try { $pico.Close() } catch { }
        return
    }
    $nowMinute = ((Get-Date).Hour * 60) + (Get-Date).Minute
    $seconds = $targetMinute - $nowMinute
    if ($seconds -lt 0) { $seconds = $seconds + 1440 }
    $expect = $seconds - 120
    if ($expect -lt 0) { $expect = 0 }
    Say ''
    Say ('برد روی پنجرهٔ واقعی مسلح است: ' + ('{0:D2}:{1:D2}' -f [int][Math]::Floor($targetMinute / 60), ($targetMinute % 60))) 'Green'
    Say ('پالس ۲ دقیقه قبل از این ساعت می‌رود، یعنی حدود ' + $expect + ' ثانیه دیگر.') 'Green'
    Say ('این تست ' + [int][Math]::Floor($seconds / 60) + ' دقیقه طول می‌کشد؛ این پنجره را باز بگذار.') 'Yellow'
    Say 'در این حالت ماکرو واقعاً اجرا می‌شود؛ دسکتاپ را در وضعیت بی‌خطر بگذار.' 'Yellow'
} else {
    $expect = $seconds
    Say ''
    Say ('ارسال دستور: ' + $command) 'Cyan'
    Send-Cmd $command
    SayLines (Drain 900)
}

Say ''
if ($dry) {
    Say 'حالت امن: سیستم را بیدار می‌کند و قفل صفحه را هم برمی‌دارد،' 'Green'
    Say 'ولی ماکرو را شروع نمی‌کند.' 'Green'
} elseif ($window) {
    Say 'حالت پنجرهٔ واقعی: بیداری با مهلت واقعی برد و بعد شروع ماکرو.' 'Yellow'
} else {
    Say 'حالت کامل: بعد از بیداری، ماکرو روی دسکتاپ کار می‌کند.' 'Yellow'
}
Say ''
Say 'اگر سیستم بیدار نشد، بعد از پایان تست یک کلید بزن تا دستی بیدار شود؛' 'DarkGray'
Say 'برد لاگ زمان خواب را ذخیره کرده و بلافاصله می‌فرستد (خطوط RPL).' 'DarkGray'
Say ''
Say 'حالا خودم سیستم را می‌خوابانم. اگر نمی‌خواهی، همین حالا Ctrl+C بزن.' 'White'
Say 'مهم: سیستم باید قبل از رسیدن مهلت خواب باشد، وگرنه آن بیداری مصرف می‌شود.' 'Yellow'
for ($i = 5; $i -gt 0; $i--) {
    Say ('  خواباندن تا ' + $i + ' ثانیه...') 'DarkGray'
    Start-Sleep -Seconds 1
}
try {
    Start-Process -FilePath 'rundll32.exe' -ArgumentList 'powrprof.dll,SetSuspendState 0,1,0' -NoNewWindow
} catch {
    Say 'خواباندن خودکار نشد؛ دستی از Start > Sleep بخوابان.' 'Yellow'
}
Say 'اگر ۱۰ ثانیه بعد سیستم هنوز بیدار است، خودت از Start > Sleep بخوابان.' 'DarkGray'
Say ('حدود ' + $expect + ' ثانیه بعد باید خودش روشن شود.') 'White'
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
$hostAwake = $false
$recoveryBlocked = $false
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
            if ($line -match 'state=host-awake') { $hostAwake = $true }
            if ($line -match 'recovery\|blocks') { $recoveryBlocked = $true }
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
    if ($hostAwake) { break }
    $elapsed = [int]((Get-Date) - $start).TotalSeconds
    if ($elapsed -ge ($lastNotice + 20)) {
        $lastNotice = $elapsed
        if (-not $woke) { Say ('... ' + $elapsed + ' ثانیه گذشت، منتظر بیداری') 'DarkGray' }
    }
    Start-Sleep -Milliseconds 200
}

# قفل صفحه تنها چیزی است که لاگ نمی‌تواند اثبات کند، پس از خود ویندوز می‌پرسیم؛
# عکس صفحه هم شاهد دوم و برای فرستادن به من راحت‌ترین راه است.
$lockedAfter = $null
$shotSaved = $false
if ($woke) {
    Start-Sleep -Seconds 2
    try { $lockedAfter = Test-Locked } catch { $lockedAfter = $null }
    try { if (Save-Shot (Join-Path (Get-Location) 'wake-shot.png')) { $shotSaved = $true } } catch { $shotSaved = $false }
}

# ---- ۵) نتیجه ----
Say ''
Say '-----------------------------------------------------' 'White'
if ($hostAwake) {
    Say 'نتیجه: مهلت بیداری قبل از خوابیدن سیستم رسید   [FAIL]' 'Red'
    Say 'آن لحظه سیستم بیدار بود، پس فریم‌ور عمداً پالسی نفرستاد و همان بیداری' 'Yellow'
    Say 'مصرف شد؛ بعد از خوابیدن سیستم دیگر چیزی برای بیدارکردن نمانده بود.' 'Yellow'
    Say 'این نسخه خودش سیستم را می‌خواباند، پس دوباره اجرا کن و تا خواب رفتن' 'Yellow'
    Say 'سیستم دست به ماوس/کیبورد نزن.' 'Yellow'
} elseif ($woke) {
    Say 'نتیجه: سیستم بیدار شد   [OK]' 'Green'
} elseif ($sawPulse) {
    Say 'نتیجه: پالس فرستاده شد ولی سیستم بیدار نشد   [FAIL]' 'Red'
    Say 'یعنی remote wake-up روی این سیستم مسلح نیست. چک کن:' 'Yellow'
    Say '  - Device Manager > کیبورد Pico > Power Management > Allow this device to wake the computer' 'Yellow'
    Say '  - بایوس > USB Wake Support = Enabled و ErP / Deep Sleep = Disabled' 'Yellow'
} else {
    Say 'نتیجه: هیچ پالسی دیده نشد   [FAIL]' 'Red'
    Say 'دو دلیل رایج را در لاگ ببین:' 'Yellow'
    Say '  EVT|WAKE|due|...            ← چه چیزی در لحظهٔ مهلت تصمیم را گرفت' 'Yellow'
    Say '  ERR|WAKE|recovery|blocks|.. ← مسیر بیداری در دست بازیابی بود' 'Yellow'
    Say 'اگر هیچ‌کدام نبود، لاگ را برایم بفرست.' 'Yellow'
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
if ($null -ne $lockedAfter) {
    if ($lockedAfter) { Say '  صفحهٔ قفل (LogonUI)            : هنوز قفل است' 'Yellow' } else { Say '  صفحهٔ قفل (LogonUI)            : برداشته شد' 'Green' }
}
if ($shotSaved) { Say '  عکس صفحه                      : wake-shot.png' 'White' }
if ($sawReplay) { Say '  لاگ زمان خواب (RPL)            : بله — در فایل ذخیره شده' 'White' }
if ($hostAwake) { Say '  مهلت قبل از خواب سیستم رسید    : بله — پالس عمداً نرفت' 'Red' }
if ($recoveryBlocked) { Say '  مسیر بیداری در دست بازیابی بود : بله — ERR|WAKE|recovery|blocks' 'Yellow' }

Start-Sleep -Seconds 3
Say ''
Say 'وضعیت نهایی برد:' 'Cyan'
Send-Cmd 'STATUS'
$final = Drain 900
SayLines $final

if ($sawStart -and (-not $dry)) {
    if ($final -match 'state=running') { Say '  ماکرو واقعاً در حال اجراست      : بله' 'Green' }
    else { Say '  ماکرو واقعاً در حال اجراست      : نه — مقدار state= در STATUS بالا را ببین' 'Yellow' }
}

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
if ($shotSaved) { Say 'عکس صفحه ذخیره شد در: wake-shot.png' 'Cyan' }
Say 'اگر نتیجه درست نبود، همین فایل wake-log.txt را برای من بفرست.' 'Cyan'
Say ''
Say 'پایان. کلید را بزن تا بسته شود.' 'White'
