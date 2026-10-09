<#
  wake-test.ps1 — تست بیدارکردن ویندوز از خواب با برد Pico

  استفاده (هر کدام یک فایل کلیک‌کردنی است):
    wake-test.cmd         ← فقط بیدارکردن (امن: ماکرو شروع نمی‌شود)
    wake-test-full.cmd    ← بیدارکردن + شروع ماکرو
    wake-test-window.cmd  ← تست پنجرهٔ واقعی شیفت: اول چک می‌کند برد روی کدام
                            ساعت مسلح است، بعد خودش سیستم را می‌خواباند و
                            منتظر بیداری واقعی می‌ماند
    wake-set-clock.cmd    ← فقط ساعت ویندوز را یک بار به برد می‌دهد (سیستم
                            نمی‌خوابد). بعد از هر فلش یک بار همین را بزن.

  پیش از اجرا Classroom Studio را کامل ببند، چون پورت COM برد را نگه می‌دارد.
#>
param([string]$Mode = '')

# حالت اجرا همین اول حساب می‌شود تا بنر ابتدای اجرا و منطق بعدی یکسان بمانند.
$window = ($Mode -eq 'window')
$dry = -not (($Mode -eq 'full') -or $window)

$ErrorActionPreference = 'Continue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
try { Add-Type -AssemblyName System.IO.Ports -ErrorAction Stop } catch { }

function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }
$logPath = Join-Path (Get-Location) 'wake-log.txt'
function Add-Log($text) {
    try { Add-Content -LiteralPath $logPath -Value $text -Encoding UTF8 } catch { }
}
function Format-Span([int]$seconds) {
    if ($seconds -lt 0) { $seconds = 0 }
    $m = [int][Math]::Floor($seconds / 60)
    if ($m -ge 60) { return ([int][Math]::Floor($m / 60)).ToString() + ' ساعت و ' + ($m % 60).ToString() + ' دقیقه' }
    if ($m -ge 1) { return $m.ToString() + ' دقیقه' }
    return $seconds.ToString() + ' ثانیه'
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

if ($Mode -eq 'clock') {
    Say 'حالت کوک ساعت: ساعت ویندوز را یک بار به برد می‌دهد.' 'Magenta'
    Say 'سیستم نمی‌خوابد و ماکرو هم شروع نمی‌شود.' 'Magenta'
} elseif ($dry) {
    Say 'حالت امن (DRY): فقط بیدارکردن — ماکرو شروع نمی‌شود.' 'Magenta'
    Say 'برای بیداری همراه با شروع واقعی ماکرو: wake-test-full.cmd' 'Magenta'
} elseif ($window) {
    Say 'حالت پنجرهٔ واقعی شیفت (WINDOW): ماکرو واقعاً اجرا می‌شود.' 'Magenta'
} else {
    Say 'حالت کامل (FULL): بعد از بیداری ماکرو واقعاً اجرا می‌شود.' 'Magenta'
}
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
if ($status -match 'state=(running|paused)') {
    Say ''
    Say 'توجه: برد الان در حال اجرای یک راند است. تا وقتی راند تمام یا متوقف نشود' 'Yellow'
    Say 'بیداری خودکار کار نمی‌کند، چون راند خودش سیستم را بیدار نگه می‌دارد.' 'Yellow'
}

# ---- ۲.۵) کوک ساعت برد ----
# برد RTC ندارد، پس تنها مرجع زمانش همین یک بار کوک کردن است. برنامهٔ شیفت
# (پنجره‌های روز/شب) را خودِ برد هنگام روشن‌شدن از پروژهٔ فلش‌شده می‌خواند، پس
# با این یک دستور برد کاملاً مستقل می‌شود و به هیچ نرم‌افزاری روی ویندوز نیاز
# ندارد. پل (بریج) هم سر جایش می‌ماند: هر وقت چک هویت شیفت انجام شود، ساعت را
# دوباره تازه می‌کند. این دستور فقط حلقهٔ «اولین بیداری» را باز می‌کند.
if ($Mode -eq 'clock') {
    $stamp = ('TIME!|{0:D2}:{1:D2}' -f (Get-Date).Hour, (Get-Date).Minute)
    Say ''
    Say ('فرستادن ساعت ویندوز به برد: ' + $stamp) 'Cyan'
    Send-Cmd $stamp
    SayLines (Drain 900)
    Say ''
    Say 'ساعت برد کوک شد.' 'Green'
    Say 'اگر برد روی پنجرهٔ شیفت مسلح شده باشد، در خط OK|TIME مقدار window و in' 'Green'
    Say 'پر است؛ اگر armed=0|schedule=0 بود، یعنی پروژهٔ فلش‌شده برنامهٔ شیفت' 'Yellow'
    Say 'ندارد یا خاموش است.' 'Yellow'
    try { $pico.Close() } catch { }
    return
}

# ---- ۳) مسلح کردن بیداری ----
$seconds = 60
$command = 'WAKE!' + $seconds
if ($dry) { $command = $command + '!dry' }

if ($window) {
    # مهلت واقعی را برنامه‌شیفت مسلح می‌کند، نه یک دستور دستی. یک مهلت دستی
    # تست (WAKE!) زودتر از پنجره می‌سوزد و ماکرو را شروع نمی‌کند، و بردِ بی‌مهلت
    # هم هیچ‌وقت خودش بیدار نمی‌شود. پس تا وقتی برد روی یک پنجرهٔ واقعی مسلح
    # نشده، ساعت را دوباره می‌فرستیم؛ همین یک دستور مهلت را از برنامهٔ شیفت پس
    # می‌گیرد.
    function Read-WakeState($text) {
        $state = [ordered]@{ armed = $false; manual = $false; dry = $false; synced = $false; target = 0 }
        if ($text -match 'armed=(\d)') { $state.armed = ($matches[1] -eq '1') }
        if ($text -match 'manual=(\d)') { $state.manual = ($matches[1] -eq '1') }
        if ($text -match 'dry=(\d)') { $state.dry = ($matches[1] -eq '1') }
        if ($text -match 'synced=(\d)') { $state.synced = ($matches[1] -eq '1') }
        if ($text -match 'target=(\d\d):(\d\d)') { $state.target = ([int]$matches[1] * 60) + [int]$matches[2] }
        return $state
    }
    function Test-OnRealWindow($state) {
        return ($state.armed -and (-not $state.manual) -and (-not $state.dry) -and
                $state.synced -and ($state.target -gt 0))
    }

    # وضعیت را از WAKE? تازه می‌خوانیم، نه از STATUS قدیمی.
    $state = Read-WakeState $wakeState
    if (-not (Test-OnRealWindow $state)) {
        Say ''
        if ($state.manual -or $state.dry) {
            Say 'برد روی یک مهلت دستی تست مسلح است، نه پنجرهٔ واقعی شیفت.' 'Yellow'
        } else {
            Say 'برد روی پنجرهٔ واقعی شیفت مسلح نیست.' 'Yellow'
        }
        Say 'ساعت را یک بار دیگر می‌فرستم تا مهلت از برنامهٔ شیفت مسلح شود.' 'Yellow'
        Send-Cmd ('TIME!|{0:D2}:{1:D2}' -f (Get-Date).Hour, (Get-Date).Minute)
        $clockReply = Drain 900
        SayLines $clockReply
        if ($clockReply -notmatch 'OK\|TIME') {
            # فریم‌ورهای قدیمی‌تر دستور TIME! را نمی‌شناسند و بی‌صدا نادیده
            # می‌گیرند؛ آن‌ها ساعت را فقط از چک هویت شیفت می‌گیرند، پس تست
            # پنجرهٔ واقعی با آن‌ها هرگز شروع نمی‌شود.
            Say ''
            Say 'برد به دستور TIME! جواب نداد.' 'Red'
            Say 'یعنی فریم‌ور جدید روی برد نیست. پوشهٔ native-runtime را با بستهٔ' 'Yellow'
            Say 'native-runtime-shift-wake-portable جایگزین کن، پروژه را دوباره' 'Yellow'
            Say '«Native UF2» بساز و برد را فلش کن؛ بعد دوباره همین فایل را اجرا کن.' 'Yellow'
            try { $pico.Close() } catch { }
            return
        }
        if ($clockReply -match 'armed=0\|schedule=0') {
            Say ''
            Say 'پروژهٔ فلش‌شده برنامهٔ شیفت ندارد (پنجرهٔ روز و شب خاموش است).' 'Red'
            Say 'در Classroom Studio برنامهٔ شیفت را روشن کن و دوباره فلش کن.' 'Yellow'
            try { $pico.Close() } catch { }
            return
        }
        Send-Cmd 'WAKE?'
        $wakeState = Drain 700
        SayLines $wakeState
        $state = Read-WakeState $wakeState
    }
    if (-not (Test-OnRealWindow $state)) {
        Say ''
        Say 'برد هنوز روی پنجرهٔ واقعی شیفت مسلح نیست.' 'Red'
        Say 'یعنی یا ساعتش کوک نشده، یا پروژهٔ فلش‌شده برنامهٔ شیفت ندارد.' 'Yellow'
        Say 'اول wake-set-clock.cmd را اجرا کن (ساعت را یک بار به برد می‌دهد)،' 'Yellow'
        Say 'بعد دوباره wake-test-window.cmd را اجرا کن.' 'Yellow'
        try { $pico.Close() } catch { }
        return
    }
    $armed = $state.armed
    $synced = $state.synced
    $targetMinute = $state.target
    $nowMinute = ((Get-Date).Hour * 60) + (Get-Date).Minute
    # $targetMinute و $nowMinute دقیقه‌اند، ولی بقیهٔ این ابزار ثانیه می‌شمارد.
    # قبلاً همین‌جا دقیقه به‌جای ثانیه استفاده می‌شد: تخمین ۶۰ برابر کوچک می‌شد و
    # حلقهٔ تماشا هم ۶۰ برابر زودتر رها می‌کرد، پس تست پنجرهٔ واقعی همیشه
    # «بیدار نشد» گزارش می‌داد در حالی که مهلت واقعی ساعت‌ها بعد بود.
    $seconds = ($targetMinute - $nowMinute) * 60
    if ($seconds -lt 0) { $seconds = $seconds + 1440 * 60 }
    $expect = $seconds - 120
    if ($expect -lt 0) { $expect = 0 }
    Say ''
    Say ('برد روی پنجرهٔ واقعی مسلح است: ' + ('{0:D2}:{1:D2}' -f [int][Math]::Floor($targetMinute / 60), ($targetMinute % 60))) 'Green'
    Say ('پالس ۲ دقیقه قبل از این ساعت می‌رود، یعنی حدود ' + (Format-Span $expect) + ' دیگر.') 'Green'
    Say ('این تست ' + (Format-Span $seconds) + ' طول می‌کشد؛ این پنجره را باز بگذار.') 'Yellow'
    Say 'برد این کار را خودش هم می‌کند؛ این پنجره فقط برای دیدن لاگ لازم است.' 'DarkGray'
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
Say ('حدود ' + (Format-Span $expect) + ' بعد باید خودش روشن شود.') 'White'
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
$blockReason = ''
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
            if ($line -match 'WAKE\|blocked') { $recoveryBlocked = $true; $blockReason = $line }
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
        if (-not $woke) { Say ('... ' + (Format-Span $elapsed) + ' گذشت، منتظر بیداری') 'DarkGray' }
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
    Say '  ERR|WAKE|blocked|reason=..  ← مسیر بیداری قفل بود (راند در حال اجرا، بازیابی یا کالیبراسیون)' 'Yellow'
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
if ($recoveryBlocked) { Say '  مسیر بیداری قفل بود            : بله' 'Red'; Say ('    ' + $blockReason) 'Red' }

if ($woke) {
    # ویندوز خودش می‌داند کدام دستگاه سیستم را بیدار کرده؛ همان یک خط،
    # مسیر بیداری را قطعی می‌کند: خودِ Pico یا مسیر پشتیبان برد آردوینو.
    Say ''
    Say 'منبع بیداری به گفتهٔ خود ویندوز (powercfg -lastwake):' 'Cyan'
    $lastWake = ''
    try { $lastWake = (powercfg -lastwake 2>&1 | Out-String) } catch { }
    foreach ($raw in ($lastWake -split "`r?`n")) {
        $line = $raw.TrimEnd()
        if ($line.Length -eq 0) { continue }
        Say ('   ' + $line) 'White'
        Add-Log ('   ' + $line)
    }
    if ($lastWake -match 'VID_7F5F') {
        Say '   → خودِ برد Pico (VID_7F5F) سیستم را بیدار کرده است.' 'Green'
    } elseif ($lastWake.Trim().Length -gt 0) {
        Say '   → اگر نام دستگاه ماوس/برد آردوینو است، مسیر پشتیبان آردوینو سیستم را بیدار کرده.' 'Yellow'
    }
}

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
