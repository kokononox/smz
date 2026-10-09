<#
  wake-env.ps1 — وضعیت بیدارکردن ویندوز از خواب را یکجا جمع می‌کند

  استفاده:
    wake-env.cmd      ← دوبار کلیک

  این ابزار هیچ چیزی را تغییر نمی‌دهد؛ فقط می‌خواند و در فایل wake-env.txt
  کنار خودش ذخیره می‌کند. آن فایل را برای من بفرست.

  چرا لازم است: بعضی از شرط‌های بیداری در بایوس و در خود ویندوز هستند و از
  داخل فریم‌ور قابل تشخیص نیستند. مهم‌ترینشان این‌ها هستند:
    - دستگاه‌هایی که ویندوز اجازهٔ بیدارکردن به آن‌ها داده (wake_armed)
    - دستگاهی که آخرین بار واقعاً سیستم را بیدار کرده (lastwake)
    - اینکه بیدارکردن برای هر دستگاه HID جداگانه فعال است یا نه
    - تنظیم USB selective suspend و مهلت خوابِ بی‌مراقب
#>
$ErrorActionPreference = 'Continue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$outPath = Join-Path (Get-Location) 'wake-env.txt'
function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }
function Add-Line($text) { try { Add-Content -LiteralPath $outPath -Value $text -Encoding UTF8 } catch { } }
function Run($title, [scriptblock]$job) {
    Say ''
    Say $title 'Cyan'
    Add-Line ('=== ' + $title + ' ===')
    $text = ''
    try { $text = (& $job 2>&1 | Out-String) } catch { $text = 'ERROR: ' + $_.Exception.Message }
    $any = $false
    foreach ($raw in ($text -split "`r?`n")) {
        $line = $raw.TrimEnd()
        if ($line.Length -eq 0) { continue }
        $any = $true
        Say ('   ' + $line) 'White'
        Add-Line ('   ' + $line)
    }
    if (-not $any) { Say '   (خالی)' 'DarkGray'; Add-Line '   (empty)' }
}
try { Set-Content -LiteralPath $outPath -Value ('# wake-env ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) -Encoding UTF8 } catch { }

Say ''
Say '=====================================================' 'White'
Say '  وضعیت بیدارکردن ویندوز از خواب — گزارش محیطی' 'White'
Say '=====================================================' 'White'

Run '۱) حالت‌های خوابی که این سیستم پشتیبانی می‌کند' { powercfg /a }
Run '۲) دستگاه‌هایی که ویندوز اجازهٔ بیدارکردن دارد (wake_armed)' { powercfg -devicequery wake_armed }
Run '۳) دستگاهی که آخرین بار سیستم را بیدار کرده (lastwake)' { powercfg -lastwake }
Run '۴) بیدارکردن برای هر دستگاه فعال است؟ (MSPower_DeviceWakeEnable)' {
    Get-CimInstance -Namespace root/wmi -ClassName MSPower_DeviceWakeEnable -ErrorAction Stop |
        Select-Object InstanceName, Enable | Format-Table -AutoSize
}
Run '۵) دستگاه‌های HID و شناسهٔ سخت‌افزاری آن‌ها' {
    Get-PnpDevice -Class HIDClass -PresentOnly -ErrorAction Stop |
        Select-Object Status, FriendlyName, InstanceId | Format-Table -AutoSize
}
Run '۶) دستگاه‌های USB این سیستم' {
    Get-PnpDevice -PresentOnly -ErrorAction Stop |
        Where-Object { $_.InstanceId -like 'USB\*' } |
        Select-Object Status, Class, FriendlyName, InstanceId | Format-Table -AutoSize
}
Run '۷) تنظیم USB selective suspend' { powercfg /q SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 }
Run '۸) زیرگروه خواب (شامل مهلت خوابِ بی‌مراقب)' { powercfg /q SCHEME_CURRENT SUB_SLEEP }

Say ''
Say '-----------------------------------------------------' 'White'
Say 'نکته‌ها:' 'Cyan'
Say '  - VID_7F5F در شناسهٔ دستگاه = برد Pico (هویت BF14).' 'White'
Say '  - در بخش ۳ اگر نامی آمد، همان دستگاه سیستم را بیدار کرده است؛' 'White'
Say '    اگر خالی بود یعنی از زمان آخرین بوت، خواب و بیداری‌ای ثبت نشده.' 'White'
Say '  - بخش ۴ اگر Enable=False باشد، یعنی تیک «Allow this device to wake the' 'White'
Say '    computer» برای آن دستگاه در Device Manager خورده نشده است.' 'White'
Say ''
Say ('گزارش ذخیره شد در: ' + $outPath) 'Cyan'
Say 'همین فایل wake-env.txt را برای من بفرست.' 'Cyan'
Say ''
Say 'پایان. کلید را بزن تا بسته شود.' 'White'
