# Local current-Windows PnP registration cleanup, not forensic history erasure.
function Assert-DeviceInstanceId($id) {
    if(!$id -or $id.Length -gt 512 -or $id -notmatch '^[A-Za-z0-9_\\&.:{}#+ @%\-]+$' -or $id -notmatch '\\'){throw 'شناسهٔ دستگاه نامعتبر است؛ حذف مسدود شد.'}
}
function Test-PeripheralDeviceId($id) {return ([string]$id -match '^(USB\\VID_|HID\\|FTDIBUS\\|BTHENUM\\|BTHLEDEVICE\\)')}
function Get-DeviceCleanupDecision($row,$rows) {
    if($row.Present){return 'متصل / حاضر — محافظت‌شده'}
    try{Assert-DeviceInstanceId $row.Id}catch{return 'شناسهٔ غیرقابل تأیید'}
    if(!($row.Class -and $row.ClassGuid)){return 'کلاس دستگاه نامشخص — محافظت‌شده'}
    $protected=@('System','Computer','Processor','Firmware','SecurityDevices','DiskDrive','Volume','SCSIAdapter','HDC','Storage','StorageVolume','Net','Display','SoftwareDevice','SoftwareComponent','Battery','Bluetooth')
    if($row.Class -in $protected){return 'کلاس حساس / خارج از محدوده — محافظت‌شده'}
    if($row.Service -match '^(USBHUB3?|USBXHCI|USBEHCI|USBOHCI|USBUHCI|PCI|ACPI)$' -or $row.Id -match '^USB\\ROOT_HUB'){return 'هاب / کنترلر — محافظت‌شده'}
    if(!(Test-PeripheralDeviceId $row.Id)){return 'خارج از دستگاه‌های جانبی USB/HID/COM'}
    if(!$row.ParentKnown -or !$row.Parent){return 'والد دستگاه قابل تأیید نیست — محافظت‌شده'}
    $index=@{};foreach($item in $rows){$index[$item.Id]=$item}
    # A present descendant or peripheral ancestor makes the whole affected family protected.
    foreach($item in $rows){if(!$item.Present){continue};$at=$item.Parent;$seen=@{}
        for($i=0;$at -and $i -lt 64;$i++){
            if($at -eq $row.Id){return 'دارای فرزند متصل — محافظت‌شده'}
            if($seen.ContainsKey($at)){return 'ساختار والد مبهم — محافظت‌شده'};$seen[$at]=$true
            if(!$index.ContainsKey($at)){break};$at=$index[$at].Parent
        }
    }
    if(@($rows|Where-Object{$_.Parent -eq $row.Id}).Count){return 'دارای ثبت فرزند — ابتدا فرزندهای غیرمتصل، سپس اسکن مجدد'}
    $at=$row.Parent;$seen=@{}
    for($i=0;$at -and $i -lt 64;$i++){
        if($seen.ContainsKey($at)){return 'ساختار والد مبهم — محافظت‌شده'};$seen[$at]=$true
        if(!$index.ContainsKey($at)){return 'زنجیرهٔ والد ناقص — محافظت‌شده'}
        $parent=$index[$at]
        if($parent.Present -and (Test-PeripheralDeviceId $parent.Id)){return 'عضو خانوادهٔ دستگاه متصل — محافظت‌شده'}
        # A present host controller/root hub is normal and is never itself removed.
        if($parent.Present){return ''}
        if(!$parent.ParentKnown -or !$parent.Parent){return 'زنجیرهٔ والد غیرمتصل قابل تأیید نیست — محافظت‌شده'}
        $at=$parent.Parent
    }
    if($at){return 'زنجیرهٔ والد طولانی — محافظت‌شده'}
    return ''
}
function Get-DeviceCleanupSnapshot {
    $rows=@([LocalDeviceInventory]::Snapshot())
    foreach($r in $rows){$reason=Get-DeviceCleanupDecision $r $rows;$r|Add-Member NoteProperty BlockReason $reason -Force;$r|Add-Member NoteProperty Eligible ([string]::IsNullOrEmpty($reason)) -Force}
    return $rows
}
function Export-DeviceRegistration($row,$path) {
    Assert-DeviceInstanceId $row.Id
    if(Test-Path -LiteralPath $path){throw 'New export filename required.'}
    $key='HKLM\SYSTEM\CurrentControlSet\Enum\'+$row.Id
    $output=@(& "$env:SystemRoot\System32\reg.exe" export $key $path 2>&1)
    if($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $path)){throw 'خواندن / ذخیرهٔ ثبت رجیستری ممکن نشد؛ این دستگاه حذف نمی‌شود.'}
    return (Hash-File $path)
}
function Remove-DisconnectedDeviceInstance($id) {
    Assert-DeviceInstanceId $id
    # Final present-only recheck immediately before the native call; no /force or subtree.
    $last=@(Get-DeviceCleanupSnapshot);$item=@($last|Where-Object{$_.Id -eq $id})
    if($item.Count -ne 1 -or !$item[0].Eligible){throw 'وضعیت / خانوادهٔ دستگاه تغییر کرد؛ حذف کنار گذاشته شد.'}
    if(@([LocalDeviceInventory]::PresentIds()) -contains $id){throw 'دستگاه دوباره حاضر شد؛ حذف کنار گذاشته شد.'}
    $output=@(& "$env:SystemRoot\System32\pnputil.exe" /remove-device $id 2>&1);$exit=$LASTEXITCODE
    if($exit -notin @(0,3010)){throw ('PnPUtil حذف را نپذیرفت؛ کد '+$exit+'. '+($output -join ' '))}
    return @{Exit=$exit;NeedsReboot=($exit -eq 3010);Output=($output -join "`r`n")}
}
function Invoke-DeviceCleanupBatch($selected,$folder,[scriptblock]$snapshot,[scriptblock]$export,[scriptblock]$remove) {
    if(!$selected.Count -or $selected.Count -gt 100){throw 'بین ۱ تا ۱۰۰ دستگاه را انتخاب کنید.'}
    $seen=@{};foreach($r in $selected){Assert-DeviceInstanceId $r.Id;if($seen.ContainsKey($r.Id)){throw 'Duplicate selected instance.'};$seen[$r.Id]=$true}
    $directory=Join-Path $folder ('Device-Cleanup-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
    [void][IO.Directory]::CreateDirectory($directory)
    $report=Join-Path $directory 'cleanup-audit.json';$text=Join-Path $directory 'cleanup-report.txt'
    $audit=@{Schema='WIR-DeviceCleanup-1';WindowsRoot=$env:SystemRoot;Computer=[Environment]::MachineName;CreatedUtc=[DateTime]::UtcNow.ToString('o');DriverPackagesDeleted=$false;AutomaticRestart=$false;RestoreGuaranteed=$false;Items=@()}
    foreach($r in $selected){$audit.Items+=@{Id=$r.Id;Name=$r.Name;Class=$r.Class;ClassGuid=$r.ClassGuid;Service=$r.Service;DriverKey=$r.DriverKey;HardwareIds=@($r.HardwareIds);Parent=$r.Parent;Status='Planned';Message='';RegistryExport='';ExportSha256='';NeedsReboot=$false}}
    Save-IdentityJsonNew $report $audit
    $counter=0
    foreach($item in $audit.Items){$counter++
        try{
            $now=@(& $snapshot);$matches=@($now|Where-Object{$_.Id -eq $item.Id})
            if(!$matches.Count){$item.Status='AlreadyAbsent';$item.Message='ثبت دیگر موجود نیست.';continue}
            if($matches.Count -ne 1){throw 'شناسه دیگر یکتا نیست.'};$r=$matches[0]
            $reason=Get-DeviceCleanupDecision $r $now
            if($reason){$item.Status='Skipped';$item.Message=$reason;continue}
            if($r.ClassGuid -ne $item.ClassGuid -or $r.Service -ne $item.Service -or (($r.HardwareIds|Sort-Object)-join ',') -ne (($item.HardwareIds|Sort-Object)-join ',')){throw 'هویت دستگاه از زمان انتخاب تغییر کرده است.'}
            $filename=('Device-{0:D3}.reg' -f $counter);$path=Join-Path $directory $filename
            $item.ExportSha256=& $export $r $path;$item.RegistryExport=$filename;$item.Status='Exported'
            Save-BootGuidMap $report $audit
            # Full policy recheck after export; a reconnection during backup is skipped.
            $now=@(& $snapshot);$latest=@($now|Where-Object{$_.Id -eq $item.Id})
            if(!$latest.Count){$item.Status='AlreadyAbsent';continue}
            if($latest.Count -ne 1 -or (Get-DeviceCleanupDecision $latest[0] $now)){$item.Status='Skipped';$item.Message='وضعیت / اتصال پس از پشتیبان تغییر کرد.';continue}
            if($latest[0].ClassGuid -ne $item.ClassGuid -or $latest[0].Service -ne $item.Service -or (($latest[0].HardwareIds|Sort-Object)-join ',') -ne (($item.HardwareIds|Sort-Object)-join ',')){throw 'هویت دستگاه پس از پشتیبان تغییر کرده است.'}
            $result=& $remove $item.Id;$item.NeedsReboot=[bool]$result.NeedsReboot;$item.Message=[string]$result.Output
            $after=@(& $snapshot)
            if(@($after|Where-Object{$_.Id -eq $item.Id}).Count){$item.Status='Remaining';$item.Message+=' | ثبت هنوز دیده می‌شود؛ نیاز به بررسی / ری‌استارت دستی ممکن است.'}
            else{$item.Status='Removed'}
        }catch{$item.Status='Failed';$item.Message=$_.Exception.Message}
        finally{
            Save-BootGuidMap $report $audit
            $lines=@('گزارش پاکسازی ثبت دستگاه‌های غیرمتصل — فقط ویندوز فعال','بسته‌های درایور حذف نشده‌اند. ری‌استارت خودکار نشده. فایل REG تضمین بازگردانی PnP یا شمارهٔ COM نیست.','')
            foreach($entry in $audit.Items){$lines+=@($entry.Status+' | '+$entry.Name,'Instance: '+$entry.Id,'Registry export: '+$entry.RegistryExport,'Result: '+$entry.Message,'')}
            [IO.File]::WriteAllLines($text,$lines,(New-Object Text.UTF8Encoding $true))
        }
    }
    return @{Audit=$audit;Report=$text;Json=$report;Folder=$directory}
}
function New-DeviceCleanupWindow {
    Add-Type -AssemblyName System.Windows.Forms;Add-Type -AssemblyName System.Drawing
    $f=New-Object Windows.Forms.Form;$f.Text='پاکسازی ثبت دستگاه‌های غیرمتصل — فقط ویندوز فعال';$f.ClientSize=New-Object Drawing.Size(1120,650);$f.Font=New-Object Drawing.Font('Segoe UI',10);$f.StartPosition='CenterScreen'
    $note=New-Object Windows.Forms.Label;$note.SetBounds(15,10,1090,66);$note.TextAlign='MiddleRight';$note.Text=('ویندوز فعال: '+$env:SystemRoot+"`r`nکیبورد، موس، برد و پورت‌های COM غیرمتصل قابل بررسی‌اند؛ پیکو استثنا نیست. بستهٔ درایور حذف نمی‌شود. دستگاه‌های حاضر و کنترلرها محافظت می‌شوند.");$f.Controls.Add($note)
    $grid=New-Object Windows.Forms.DataGridView;$grid.SetBounds(15,85,1090,390);$grid.AllowUserToAddRows=$false;$grid.AllowUserToDeleteRows=$false;$grid.RowHeadersVisible=$false;$grid.AutoSizeColumnsMode='Fill';$grid.SelectionMode='FullRowSelect';$grid.MultiSelect=$false
    $c=New-Object Windows.Forms.DataGridViewCheckBoxColumn;$c.Name='selected';$c.HeaderText='انتخاب';$c.FillWeight=8;[void]$grid.Columns.Add($c)
    foreach($s in @(@('name','نام',23),@('class','نوع',12),@('id','شناسهٔ نمونه',34),@('status','وضعیت / محافظت',23))){$c=New-Object Windows.Forms.DataGridViewTextBoxColumn;$c.Name=$s[0];$c.HeaderText=$s[1];$c.FillWeight=$s[2];$c.ReadOnly=$true;$c.SortMode='NotSortable';[void]$grid.Columns.Add($c)};$grid.Columns['selected'].SortMode='NotSortable';$f.Controls.Add($grid)
    $show=New-Object Windows.Forms.CheckBox;$show.SetBounds(15,486,360,30);$show.Text='نمایش موارد محافظت‌شدهٔ مرتبط';$f.Controls.Add($show)
    $ack=New-Object Windows.Forms.CheckBox;$ack.SetBounds(390,483,715,44);$ack.RightToLeft='Yes';$ack.Text='ماکرو متوقف است؛ شمارهٔ COM / تنظیمات اتصال ممکن است پس از اتصال مجدد تغییر کند. بازگردانی کامل تضمین نیست.';$f.Controls.Add($ack)
    $scan=New-Object Windows.Forms.Button;$scan.SetBounds(15,537,250,42);$scan.Text='اسکن / تازه‌سازی فقط‌خواندنی';$f.Controls.Add($scan)
    $preview=New-Object Windows.Forms.Button;$preview.SetBounds(285,537,300,42);$preview.Text='پیش‌نمایش انتخاب‌ها — بدون حذف';$f.Controls.Add($preview)
    $apply=New-Object Windows.Forms.Button;$apply.SetBounds(605,537,500,42);$apply.Text='پشتیبان و حذف ثبتِ موارد انتخاب‌شده';$f.Controls.Add($apply)
    $status=New-Object Windows.Forms.Label;$status.SetBounds(15,590,1090,48);$status.TextAlign='MiddleRight';$status.Text='ابتدا اسکن کنید. هیچ دستگاهی پیش‌فرض انتخاب نیست. اسکن تاریخچهٔ کامل یا زمان آخرین اتصال را حدس نمی‌زند.';$f.Controls.Add($status)
    return @{Form=$f;Grid=$grid;Show=$show;Ack=$ack;Scan=$scan;Preview=$preview;Apply=$apply;Status=$status}
}
function Show-DeviceCleanup {
    $w=New-DeviceCleanupWindow;$state=@{Rows=@();Exit=22}
    $scanAction={try{
        $rows=@(Get-DeviceCleanupSnapshot);$state.Rows=@($rows|Where-Object{$_.Eligible -or ($w.Show.Checked -and (Test-PeripheralDeviceId $_.Id))}|Sort-Object Class,Name,Id)
        $w.Grid.Rows.Clear();foreach($r in $state.Rows){$status=if($r.Eligible){'غیرمتصل — قابل انتخاب'}else{$r.BlockReason};$i=$w.Grid.Rows.Add([object[]]@($false,$r.Name,$r.Class,$r.Id,$status));$w.Grid.Rows[$i].Cells['selected'].ReadOnly=!$r.Eligible;if(!$r.Eligible){$w.Grid.Rows[$i].DefaultCellStyle.ForeColor=[Drawing.Color]::Gray}}
        $w.Status.Text=('موارد قابل انتخاب: '+@($rows|Where-Object{$_.Eligible}).Count+' | همهٔ موارد: '+$rows.Count+' | هیچ حذف یا تغییر رجیستری انجام نشده.')
    }catch{[void][Windows.Forms.MessageBox]::Show($w.Form,$_.Exception.Message,'اسکن ناموفق','OK','Error')}}.GetNewClosure()
    $getSelected={ [void]$w.Grid.EndEdit();$result=@();foreach($row in $w.Grid.Rows){if($row.Cells['selected'].Value -eq $true){$r=$state.Rows[$row.Index];if(!$r.Eligible){throw 'مورد محافظت‌شده انتخاب شده است.'};$result+=$r}};return $result }.GetNewClosure()
    $w.Scan.Add_Click($scanAction);$w.Show.Add_CheckedChanged($scanAction)
    $w.Preview.Add_Click({try{$selected=@(& $getSelected);if(!$selected.Count){throw 'ابتدا موارد را انتخاب کنید.'};$summary=($selected|ForEach-Object{$_.Name+' | '+$_.Class+"`r`n"+$_.Id})-join "`r`n`r`n";[void][Windows.Forms.MessageBox]::Show($w.Form,$summary,'پیش‌نمایش — هیچ حذف انجام نمی‌شود')}catch{[void][Windows.Forms.MessageBox]::Show($w.Form,$_.Exception.Message)}}.GetNewClosure())
    $w.Apply.Add_Click({try{
        $selected=@(& $getSelected);if(!$selected.Count){throw 'موردی انتخاب نشده است.'};if(!$w.Ack.Checked){throw 'توقف ماکرو و محدودیت اتصال مجدد را تأیید کنید.'}
        $summary=($selected|ForEach-Object{$_.Name+' | '+$_.Id})-join "`r`n"
        if([Windows.Forms.MessageBox]::Show($w.Form,('ثبت این دستگاه‌ها در ویندوز فعال حذف شود؟ دستگاهِ دوباره متصل‌شده کنار گذاشته می‌شود. پکیج درایور حذف نمی‌شود. بازگردانی خودکار نداریم؛ گزارش و خروجی REG تضمین بازگردانی نیست.'+"`r`n`r`n"+$summary),'تأیید حذف انتخاب‌شده‌ها','YesNo','Warning') -ne 'Yes'){return}
        $d=New-Object Windows.Forms.FolderBrowserDialog;$d.Description='پوشهٔ محلیِ امن برای گزارش و خروجی رجیستری (اطلاعات دستگاه‌ها خصوصی‌اند)'
        try{if($d.ShowDialog($w.Form) -ne 'OK'){return};$folder=$d.SelectedPath}finally{$d.Dispose()}
        $w.Apply.Enabled=$false;$w.Scan.Enabled=$false;$w.Preview.Enabled=$false;$w.Show.Enabled=$false
        $result=Invoke-DeviceCleanupBatch $selected $folder {Get-DeviceCleanupSnapshot} {param($r,$p)Export-DeviceRegistration $r $p} {param($id)Remove-DisconnectedDeviceInstance $id}
        $state.Exit=0;$removed=@($result.Audit.Items|Where-Object{$_.Status -eq 'Removed'}).Count
        [void][Windows.Forms.MessageBox]::Show($w.Form,('حذفِ تأییدشده: '+$removed+' / '+$selected.Count+"`r`nنتیجهٔ هر مورد در گزارش ثبت شده. ری‌استارت خودکار نشده؛ موارد Remaining/Failed را بررسی کنید.`r`n"+$result.Report),'گزارش عملیات')
        $w.Form.Close()
    }catch{[void][Windows.Forms.MessageBox]::Show($w.Form,$_.Exception.Message,'عملیات کامل نشد','OK','Error')}finally{$w.Apply.Enabled=$true;$w.Scan.Enabled=$true;$w.Preview.Enabled=$true;$w.Show.Enabled=$true}}.GetNewClosure())
    try{[void]$w.Form.ShowDialog()}finally{$w.Form.Dispose()};return $state.Exit
}
function Test-DeviceCleanup {
    $class='{4d36e96b-e325-11ce-bfc1-08002be10318}'
    $hub=[PSCustomObject]@{Id='USB\ROOT_HUB30\FIXTURE';Name='Hub';Class='USB';ClassGuid=$class;Present=$true;Parent='';ParentKnown=$false;Service='USBHUB3';HardwareIds=@('USB\ROOT_HUB30');DriverKey='fixture'}
    $ghost=[PSCustomObject]@{Id='USB\VID_0001&PID_0002\FIXTURE';Name='Disconnected board';Class='Ports';ClassGuid=$class;Present=$false;Parent=$hub.Id;ParentKnown=$true;Service='usbser';HardwareIds=@('USB\VID_0001&PID_0002');DriverKey='fixture'}
    if((Get-DeviceCleanupDecision $ghost @($hub,$ghost))){throw 'Ordinary disconnected board was blocked.'}
    $ghost.Present=$true;if(!(Get-DeviceCleanupDecision $ghost @($hub,$ghost))){throw 'Present device was eligible.'};$ghost.Present=$false
    foreach($protected in @('System','DiskDrive','Volume','Net','Display','Firmware')){$ghost.Class=$protected;if(!(Get-DeviceCleanupDecision $ghost @($hub,$ghost))){throw 'Critical class was eligible.'}};$ghost.Class='Ports'
    $ghost.ParentKnown=$false;if(!(Get-DeviceCleanupDecision $ghost @($hub,$ghost))){throw 'Unknown ancestry was eligible.'};$ghost.ParentKnown=$true
    $child=[PSCustomObject]@{Id='HID\VID_0001&PID_0002\CHILD';Name='Keyboard';Class='Keyboard';ClassGuid=$class;Present=$true;Parent=$ghost.Id;ParentKnown=$true;Service='kbdhid';HardwareIds=@('HID\VID_0001&PID_0002');DriverKey='fixture'}
    if(!(Get-DeviceCleanupDecision $ghost @($hub,$ghost,$child))){throw 'Parent with present child was eligible.'}
    $child.Present=$false;if(!(Get-DeviceCleanupDecision $ghost @($hub,$ghost,$child))){throw 'Non-leaf parent was eligible.'}
    if((Get-DeviceCleanupDecision $child @($hub,$ghost,$child))){throw 'Disconnected leaf child was blocked.'}
    $ghost.Present=$true;if(!(Get-DeviceCleanupDecision $child @($hub,$ghost,$child))){throw 'Child of present peripheral was eligible.'};$ghost.Present=$false
    $ghost.ParentKnown=$false;if(!(Get-DeviceCleanupDecision $child @($hub,$ghost,$child))){throw 'Uncertain non-present ancestor was eligible.'};$ghost.ParentKnown=$true
    foreach($id in @("USB\VID_1\bad`nline",'USB\VID_1\"quote','USB\VID_1\;cmd')){$blocked=$false;try{Assert-DeviceInstanceId $id}catch{$blocked=$true};if(!$blocked){throw 'Unsafe device argument accepted.'}}
    $folder=Join-Path $PSScriptRoot ('device-fixture-'+[Guid]::NewGuid().ToString('N'));[void][IO.Directory]::CreateDirectory($folder)
    $state=@{Rows=@($hub,$ghost);ExportCalls=0;RemoveCalls=0;ExportFails=$false;Reconnect=$false;PresentAtStart=$false;KeepAfter=$false}
    $snapshot={return $state.Rows}.GetNewClosure()
    $export={param($row,$path)$state.ExportCalls++;if($state.ExportFails){throw 'Injected export failure.'};[IO.File]::WriteAllText($path,'Fixture export');if($state.Reconnect){$row.Present=$true};return (Hash-File $path)}.GetNewClosure()
    $remove={param($id)$state.RemoveCalls++;if(!$state.KeepAfter){$state.Rows=@($state.Rows|Where-Object{$_.Id -ne $id})};return @{Output='Fixture removal';NeedsReboot=$false}}.GetNewClosure()
    try{
        $ghost.Present=$true;$result=Invoke-DeviceCleanupBatch @($ghost) $folder $snapshot $export $remove
        if($state.RemoveCalls -or $state.ExportCalls -or $result.Audit.Items[0].Status -ne 'Skipped'){throw 'Connected device was touched.'}
        $ghost.Present=$false;$state.ExportFails=$true;$result=Invoke-DeviceCleanupBatch @($ghost) $folder $snapshot $export $remove
        if($state.RemoveCalls -or $result.Audit.Items[0].Status -ne 'Failed'){throw 'Delete occurred without backup.'}
        $state.ExportFails=$false;$state.Reconnect=$true;$result=Invoke-DeviceCleanupBatch @($ghost) $folder $snapshot $export $remove
        if($state.RemoveCalls -or $result.Audit.Items[0].Status -ne 'Skipped'){throw 'Reconnection during export was ignored.'}
        $ghost.Present=$false;$state.Reconnect=$false;$state.KeepAfter=$true;$result=Invoke-DeviceCleanupBatch @($ghost) $folder $snapshot $export $remove
        if($result.Audit.Items[0].Status -ne 'Remaining'){throw 'Unverified removal was reported successful.'}
        $state.KeepAfter=$false;$result=Invoke-DeviceCleanupBatch @($ghost) $folder $snapshot $export $remove
        if($result.Audit.Items[0].Status -ne 'Removed' -or !(Test-Path $result.Json) -or !(Test-Path $result.Report) -or $result.Audit.DriverPackagesDeleted){throw 'Cleanup audit/result contract failed.'}
        $w=New-DeviceCleanupWindow;try{if(!$w.Grid.Columns['id'].ReadOnly -or !$w.Grid.Columns['status'].ReadOnly){throw 'Device identity/status columns are editable.'}}finally{$w.Form.Dispose()}
        # READ ONLY actual enumeration. No actual PnPUtil removal or real registry export.
        $actual=@([LocalDeviceInventory]::Snapshot());$present=@([LocalDeviceInventory]::PresentIds())
        if(!$actual.Count -or !$present.Count){throw 'Actual SetupAPI inventory is empty.'}
        $unique=@{};foreach($r in $actual){if(!$r.Id -or $unique.ContainsKey($r.Id)){throw 'Inventory identifiers are missing/duplicate.'};$unique[$r.Id]=$true;if($r.Present -and !(Get-DeviceCleanupDecision $r $actual)){throw 'Actual present device was eligible.'}}
        Write-Output ('PASS: device cleanup policy, present/critical/family/leaf guards, safe IDs, mandatory export, reconnection skip, verified results and audit; real inventory READ ONLY ('+$actual.Count+' instances). No actual device removal.')
    }finally{Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue}
}
