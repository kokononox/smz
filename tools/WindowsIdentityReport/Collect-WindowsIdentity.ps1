param([string]$InputFile,[string]$OutputFile,[switch]$SelfTest)
$ErrorActionPreference='Stop'
# An indirect launch from PowerShell 7 can inherit its module path; prefer Windows PS modules.
$systemModules=Join-Path $PSHOME 'Modules'
$env:PSModulePath=$systemModules+';'+$env:PSModulePath
function Hash-File([string]$path) {
    $sha=[Security.Cryptography.SHA256]::Create();$stream=[IO.File]::OpenRead($path)
    try{return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}
    finally{$stream.Dispose();$sha.Dispose()}
}
Add-Type -Path (Join-Path $PSScriptRoot 'RegistryNames.cs')
function Invariant-Guid($value) {
    $g=[Guid]::Empty
    if([Guid]::TryParse([string]$value,[ref]$g)){return '{'+$g.ToString()+'}'}
    return $null
}
function Normal-WindowsPath([string]$value) {
    $value=$value.Trim().Trim('"').TrimEnd('\')
    if($value -match '^[A-Za-z]:$'){$value+='\Windows'}
    elseif($value -match '^[A-Za-z]:\\$'){$value+='Windows'}
    if($value -notmatch '^[A-Za-z]:\\[^\r\n]+$'){throw 'Use a local drive or absolute Windows folder, e.g. D:\Windows.'}
    return [IO.Path]::GetFullPath($value).TrimEnd('\')
}
function Get-BcdElement($object,[uint32]$type) {
    $r=$object.GetElement($type)
    if($r.ReturnValue){return $r.Element}
    return $null
}
function Bcd-Object([string]$guid) {
    $guid=Invariant-Guid $guid;if(!$guid){throw 'Invalid BCD identifier.'}
    $o=[wmi]('root\WMI:BcdObject.Id="'+$guid+'",StoreFilePath=""')
    $o.Scope.Options.EnablePrivileges=$true
    return $o
}
function Device-Matches([string]$bcdPath,[string]$ntPath,[string]$drive) {
    if(!$bcdPath){return $false}
    $value=$bcdPath.TrimEnd('\')
    if($value.StartsWith('partition=',[StringComparison]::OrdinalIgnoreCase)){$value=$value.Substring(10)}
    return ($ntPath -and $value.Equals($ntPath,[StringComparison]::OrdinalIgnoreCase)) -or $value.Equals($drive,[StringComparison]::OrdinalIgnoreCase) -or $value.Equals('\??\'+$drive,[StringComparison]::OrdinalIgnoreCase)
}
if($SelfTest) {
    if((Normal-WindowsPath 'd:') -ne 'D:\Windows'){throw 'Drive normalization failed.'}
    if((Invariant-Guid '{9dea862c-5cdd-4e70-acc1-f32b344d4795}') -ne '{9dea862c-5cdd-4e70-acc1-f32b344d4795}'){throw 'GUID normalization failed.'}
    if(!(Device-Matches '\Device\HarddiskVolume3' '\Device\HarddiskVolume3' 'D:')){throw 'Device match failed.'}
    if(Device-Matches '\Device\HarddiskVolume4' '\Device\HarddiskVolume3' 'D:'){throw 'False volume match.'}
    # A Windows-created temporary fixture validates the reader against real hive layout.
    $testKey='Software\WIR-Reader-Test-'+[Guid]::NewGuid().ToString('N')
    $fixture=Join-Path $PSScriptRoot 'fixture.hive'
    try {
        $k=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testKey+'\SAM\Domains\Account\Users\Names\day-user');$k.Close()
        $k=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testKey+'\SAM\Domains\Account\Users\Names\کاربرشب');$k.Close()
        $k=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testKey+'\Select');$k.SetValue('Current',1,[Microsoft.Win32.RegistryValueKind]::DWord);$k.Close()
        $k=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testKey+'\ControlSet001\Control\ComputerName\ComputerName');$k.SetValue('ComputerName','TEST-PC');$k.Close()
        & "$env:SystemRoot\System32\reg.exe" save ('HKCU\'+$testKey) $fixture /y | Out-Null
        if($LASTEXITCODE -ne 0){throw 'Cannot save Windows test fixture.'}
        $before=(Hash-File $fixture)
        $h=New-Object ReadOnlyHive $fixture
        try {
            $names=$h.Names('SAM\Domains\Account\Users\Names')
            if($names.Count -ne 2 -or $names -notcontains 'day-user' -or $names -notcontains 'کاربرشب'){throw 'Windows hive names mismatch.'}
            if($h.DwordValue('Select','Current') -ne 1 -or $h.StringValue('ControlSet001\Control\ComputerName\ComputerName','ComputerName') -ne 'TEST-PC'){throw 'Windows hive metadata mismatch.'}
        }finally{$h.Dispose()}
        if((Hash-File $fixture) -ne $before){throw 'Reader modified a hive.'}
    }finally{[Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($testKey,$false);Remove-Item -LiteralPath $fixture -Force -ErrorAction SilentlyContinue}
    Write-Output 'PASS: Windows fixture, Unicode names, DWORD/string values, read-only SHA256, drive/GUID mapping.' 
    exit 0
}
if(!$InputFile -or !$OutputFile){throw 'InputFile and OutputFile are required.'}
$report=New-Object 'System.Collections.Generic.List[string]'
function Line([string]$text){$report.Add($text)}
function Safe([object]$text){return ([string]$text -replace '[\r\n\t]',' ')}
Line 'گزارش کمکی هویت ویندوز و ورودی‌های بوت'
Line ('زمان تهیه: '+(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Line 'GUID متعلق به ورودی بوت ویندوز است، نه به یوزر. نام کاربر هدف را خودتان انتخاب کنید.'
Line 'این ابزار فقط خواندنی است؛ بوت پیش‌فرض، رجیستری و فایل‌های ویندوز را تغییر نمی‌دهد.'
Line 'فقط BCD فعال همین کامپیوتر بررسی می‌شود؛ ورودی‌های BCD دیگر، VHD/دستگاه‌های پشتیبانی‌نشده ممکن است تطبیق قطعی نداشته باشند.'
Line ''
$boots=@();$default=$null;$order=@();$bcdError=$null
try {
    $class=[wmiclass]'root\WMI:BcdStore';$class.Scope.Options.EnablePrivileges=$true
    $opened=$class.OpenStore('');if(!$opened.ReturnValue){throw 'Cannot open the active BCD store.'}
    $store=[wmi]'root\WMI:BcdStore.FilePath=""';$store.Scope.Options.EnablePrivileges=$true
    $objects=$store.EnumerateObjects([uint32]0x10200003)
    if(!$objects.ReturnValue){throw 'Cannot enumerate Windows boot loaders.'}
    $manager=Bcd-Object '{9dea862c-5cdd-4e70-acc1-f32b344d4795}'
    $def=Get-BcdElement $manager ([uint32]0x23000003);if($def){$default=Invariant-Guid $def.Id}
    $display=Get-BcdElement $manager ([uint32]0x24000001);if($display){$order=@($display.Ids|ForEach-Object{Invariant-Guid $_})}
    foreach($entry in @($objects.Objects)) {
        $id=Invariant-Guid $entry.Id;if(!$id){continue};$obj=Bcd-Object $id
        $desc=Get-BcdElement $obj ([uint32]0x12000004)
        $root=Get-BcdElement $obj ([uint32]0x22000002)
        $os=Get-BcdElement $obj ([uint32]0x21000001)
        $device=Get-BcdElement $obj ([uint32]0x11000001)
        $boots += [PSCustomObject]@{Id=$id;Name=[string]$desc.String;Root=[string]$root.String;OsDevice=[string]$os.Device.Path;Device=[string]$device.Device.Path;Default=($id -eq $default);Displayed=($order -contains $id)}
    }
    Line ('GUID بوت پیش‌فرض: '+$default)
}catch{$bcdError=$_.Exception.Message;Line ('هشدار: اطلاعات BCD قطعی خوانده نشد: '+(Safe $bcdError))}
$live=([Environment]::GetEnvironmentVariable('SystemRoot')).TrimEnd('\')
$inputs=@([IO.File]::ReadAllLines($InputFile)|Where-Object{!([string]::IsNullOrWhiteSpace($_))})
$seen=@{};$index=0
foreach($inputLine in $inputs) {
    $role='تعیین نشده';$path=$inputLine.Trim()
    if($path -match '^(day|night|روز|شب)\s*=\s*(.+)$'){$tag=$Matches[1];$path=$Matches[2];$role=if($tag -in @('day','روز')){'روز'}else{'شب'}}
    $index++;Line '';Line ('================ ویندوز '+$index+' — شیفت: '+$role+' ================')
    try {
        $path=Normal-WindowsPath $path
        if($seen.ContainsKey($path)){throw 'Duplicate Windows path; do not assign one installation to both shifts.'};$seen[$path]=$true
        if(!(Test-Path -LiteralPath (Join-Path $path 'System32\config\SYSTEM') -PathType Leaf)){throw 'No Windows SYSTEM hive found. Drive may be wrong, offline or BitLocker-locked.'}
        Line ('مسیر ویندوز: '+$path);$drive=$path.Substring(0,2).ToUpperInvariant();$nt=[NativeVolume]::DevicePath($drive)
        Line ('شناسهٔ دستگاه در همین بوت: '+$nt)
        $liveOS=$path.Equals($live,[StringComparison]::OrdinalIgnoreCase)
        $users=@();$source=''
        if($liveOS) {
            Line ('نام کامپیوتر: '+[Environment]::MachineName)
            $console=(Get-WmiObject Win32_ComputerSystem).UserName
            Line ('کاربر گزارش‌شدهٔ نشست کنسول: '+(Safe $console))
            Line ('کاربر فرایند ابزارِ Administrator: '+[Environment]::UserName+' — ممکن است با کاربر بریج متفاوت باشد.')
            $users=@(Get-WmiObject Win32_UserAccount -Filter 'LocalAccount=True' |Sort-Object Name|ForEach-Object{[string]$_.Name})
            $source='حساب‌های محلی ویندوز فعال (WMI)'
        } else {
            $h=$null
            try {
                $h=New-Object ReadOnlyHive (Join-Path $path 'System32\config\SYSTEM')
                $current=$h.DwordValue('Select','Current')
                $name=$h.StringValue(('ControlSet{0:D3}\Control\ComputerName\ComputerName' -f $current),'ComputerName')
                Line ('نام کامپیوتر: '+(Safe $name))
            }catch{Line ('نام کامپیوتر: نامشخص — '+(Safe $_.Exception.Message))}finally{if($h){$h.Dispose()}}
            $h=$null
            try {
                $h=New-Object ReadOnlyHive (Join-Path $path 'System32\config\SAM')
                try{$users=@($h.Names('SAM\Domains\Account\Users\Names'))}
                catch [IO.InvalidDataException] {$users=@($h.Names('Domains\Account\Users\Names'))}
                $source='نام حساب‌های محلی در SAM آفلاین؛ فقط نام کلیدها، بدون خواندن دادهٔ رمز عبور'
            }catch{Line ('نام کاربران: نامشخص — '+(Safe $_.Exception.Message));Line 'برای نام قطعی، ابزار را داخل همین ویندوز اجرا کنید؛ نام پوشهٔ Users جایگزین Username نیست.'}
            finally{if($h){$h.Dispose()}}
        }
        if($users.Count) {Line ('منبع نام کاربران: '+$source);Line 'Usernameهای قابل‌شناسایی:';foreach($user in $users){Line ('  - '+(Safe $user))}}
        Line 'کاربر هدف برای تنظیمات روز/شب: خودتان از فهرست انتخاب کنید؛ ابزار آن را حدس نمی‌زند.'
        $relative=$path.Substring(2).TrimEnd('\');$matches=@()
        foreach($boot in $boots) {
            if($boot.Root -and $boot.Root.TrimEnd('\').Equals($relative,[StringComparison]::OrdinalIgnoreCase) -and (Device-Matches $boot.OsDevice $nt $drive)){$matches+=$boot}
        }
        if(!$matches.Count){Line 'تطبیق ورودی بوت: نامشخص — GUID را از روی نام یوزر یا نام نمایشی حدس نزنید.'}
        elseif($matches.Count -gt 1){Line 'چند ورودی بوت به همین ویندوز اشاره می‌کنند؛ مقصد موردنظر را خودتان انتخاب کنید.'}
        foreach($boot in $matches) {
            Line ('نام در منوی بوت: '+(Safe $boot.Name));Line ('GUID: '+$boot.Id)
            Line ('پیش‌فرض بوت: '+$boot.Default+' | در DisplayOrder منوی بوت: '+$boot.Displayed)
            Line ('فرمان کمکی — اجرا نشده: bcdedit /default '+$boot.Id)
            Line ('هدف میان‌بر نمونه — اجرا نشده: %SystemRoot%\System32\cmd.exe /c "bcdedit /default '+$boot.Id+' && bcdedit /timeout 10"')
        }
    }catch{Line ('خطا در این مسیر: '+(Safe $_.Exception.Message))}
}
Line '';Line '================ همهٔ ورودی‌های Windows Boot Loader در BCD فعال ================'
if($boots.Count -eq 0){Line 'نامشخص / ورودی قابل‌خواندن وجود ندارد.'}
foreach($boot in $boots) {Line ('Name: '+(Safe $boot.Name));Line ('GUID: '+$boot.Id);Line ('OS device: '+$boot.OsDevice+' | Windows root: '+$boot.Root+' | Application device: '+$boot.Device);Line ('Default: '+$boot.Default+' | Boot menu: '+$boot.Displayed);Line ''}
Line 'یادآوری: برای بریج، Username واقعی حسابی که وارد آن می‌شوید را تنظیم کنید، نه نام کامپیوتر، نام بوت یا Display name.'
Line 'این گزارش برای کمک به چیدن استپ‌هاست؛ هیچ فرمان bcdedit تغییردهنده‌ای اجرا نشده و هیچ انتخاب روز/شب خودکار انجام نشده است.'
[IO.File]::WriteAllLines($OutputFile,$report.ToArray(),(New-Object Text.UTF8Encoding $true))
Write-Output 'REPORT_SAVED'
