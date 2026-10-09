# Only the running Windows installation is editable. No offline hive writes.
function New-IdentityGuid { return [Guid]::NewGuid().ToString('D') }
function Assert-IdentityValue([string]$kind,[string]$value) {
    switch($kind) {
        'ComputerName' {if($value -notmatch '^[A-Za-z0-9][A-Za-z0-9-]{0,14}$' -or $value -match '^\d+$' -or $value.EndsWith('-')){throw 'نام کامپیوتر: ۱ تا ۱۵ حرف انگلیسی/عدد/خط تیره، نه فقط عدد و نه با خط تیره در انتها.'}}
        'VolumeLabel' {if($value.Length -gt 32 -or $value -match '[\x00-\x1f\\/:*?"<>|]'){throw 'برچسب پارتیشن نامعتبر است (حداکثر ۳۲ کاراکتر).'}}
        'MachineGuid' {$g=[Guid]::Empty;if(![Guid]::TryParseExact($value,'D',[ref]$g) -or $g -eq [Guid]::Empty){throw 'GUID باید معتبر، غیرصفر و به شکل xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx باشد.'}}
        'InstallDate' {$d=[DateTime]::MinValue;if(![DateTime]::TryParseExact($value,'yyyy-MM-dd',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::None,[ref]$d) -or $d -lt [datetime]'1970-01-01' -or $d -gt [DateTime]::UtcNow.Date){throw 'تاریخ میلادی از 1970 تا امروز، به شکل yyyy-MM-dd لازم است.'}}
        'MacAddress' {$v=$value.Replace('-','').Replace(':','').ToUpperInvariant();if($v -notmatch '^[0-9A-F]{12}$' -or (([Convert]::ToInt32($v.Substring(0,2),16) -band 3) -ne 2)){throw 'MAC باید ۱۲ رقم هگز، locally administered و unicast باشد؛ دکمهٔ تولید را استفاده کنید.'}}
        default {throw 'Unsupported identity field.'}
    }
}
function New-IdentityMac {
    $bytes=New-Object byte[] 6;$rng=[Security.Cryptography.RandomNumberGenerator]::Create()
    try{$rng.GetBytes($bytes)}finally{$rng.Dispose()}
    $bytes[0]=($bytes[0] -band 252) -bor 2
    return [BitConverter]::ToString($bytes).Replace('-','')
}
function Normalize-IdentityValue($kind,$value) {
    Assert-IdentityValue $kind $value
    if($kind -eq 'MachineGuid'){return ([Guid]$value).ToString('D')}
    if($kind -eq 'MacAddress'){return $value.Replace('-','').Replace(':','').ToUpperInvariant()}
    return $value
}
function Get-IdentityWindowsKey {
    $drive=[IO.Path]::GetPathRoot($env:SystemRoot).TrimEnd('\')
    $disk=Get-CimInstance Win32_LogicalDisk -Filter ("DeviceID='"+$drive+"'")
    if(!$disk.VolumeSerialNumber){throw 'Cannot establish Windows volume identity.'}
    return ($disk.VolumeSerialNumber+'|'+$env:SystemRoot.ToLowerInvariant())
}
function Get-IdentityRegSpec($kind) {
    switch($kind){
        'MachineGuid' {return @{Path='SOFTWARE\Microsoft\Cryptography';Name='MachineGuid';Type='String'}}
        'InstallDate' {return @{Path='SOFTWARE\Microsoft\Windows NT\CurrentVersion';Name='InstallDate';Type='DWord'}}
        default {throw 'No registry writer for this field.'}
    }
}
function Read-IdentityRegistry($kind) {
    $spec=Get-IdentityRegSpec $kind
    $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
    $key=$base.OpenSubKey($spec.Path,$false)
    try{
        if(!$key){throw 'Required Windows registry key is absent.'}
        $exists=@($key.GetValueNames()) -contains $spec.Name
        if(!$exists){throw 'Required Windows registry value is absent; this tool will not invent it.'}
        $type=$key.GetValueKind($spec.Name).ToString()
        if($type -ne $spec.Type){throw 'Unexpected registry value type.'}
        return @{Exists=$true;Type=$type;Value=$key.GetValue($spec.Name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}
    }finally{if($key){$key.Dispose()};$base.Dispose()}
}
function Write-IdentityRegistry($kind,$value) {
    $spec=Get-IdentityRegSpec $kind
    $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
    $key=$base.OpenSubKey($spec.Path,$true)
    try{
        if(!$key){throw 'Required registry key is absent.'}
        $typed=$value;if($kind -eq 'InstallDate'){$typed=[int]$value}
        $key.SetValue($spec.Name,$typed,([Microsoft.Win32.RegistryValueKind][Enum]::Parse([Microsoft.Win32.RegistryValueKind],$spec.Type)));$key.Flush()
    }finally{if($key){$key.Dispose()};$base.Dispose()}
}
function Get-IdentityAdapter($guid) {
    $g=Invariant-Guid $guid;if(!$g){throw 'Invalid adapter identity.'}
    $found=@(Get-NetAdapter -IncludeHidden -ErrorAction Stop |Where-Object{(Invariant-Guid $_.InterfaceGuid) -eq $g})
    if($found.Count -ne 1){throw 'کارت شبکهٔ انتخاب‌شده دیگر به‌صورت یکتا موجود نیست.'}
    return $found[0]
}
function Get-IdentityMacProperty($guid) {
    $adapter=Get-IdentityAdapter $guid
    $props=@(Get-NetAdapterAdvancedProperty -Name ([WildcardPattern]::Escape($adapter.Name)) -AllProperties -ErrorAction Stop |Where-Object{$_.RegistryKeyword -eq 'NetworkAddress'})
    if($props.Count -ne 1){throw 'درایور این کارت گزینهٔ NetworkAddress را ارائه نمی‌کند؛ تغییر MAC انجام نمی‌شود.'}
    Assert-IdentityMacPropertyTarget $props[0] $guid
    return $props[0]
}
function Assert-IdentityMacPropertyTarget($property,$guid) {
    $id=Invariant-Guid $guid;$parts=([string]$property.InstanceID) -split '::'
    if(!$id -or $parts.Count -ne 2 -or (Invariant-Guid $parts[0]) -ne $id -or $parts[1] -ne 'NetworkAddress' -or $property.RegistryKeyword -ne 'NetworkAddress' -or $property.RegistryDataType -ne 1){throw 'ویژگی MAC به کارت انتخاب‌شده و مقدار متنی NetworkAddress متصل نیست؛ تغییر مسدود شد.'}
}
function Assert-IdentityMacRestorable($property) {
    $v=@($property.RegistryValue);if($v.Count -gt 1){throw 'Unexpected multi-value MAC override.'}
    $text=if($v.Count){[string]$v[0]}else{''}
    if($text){if($text -notmatch '^[0-9a-fA-F]{12}$' -or (([Convert]::ToInt32($text.Substring(0,2),16) -band 1) -ne 0)){throw 'مقدار قبلی MAC برای بازگردانی ایمن قابل شناسایی نیست.'}}
    elseif([string]::IsNullOrWhiteSpace([string]$property.DisplayName) -or $null -eq $property.DefaultRegistryValue){throw 'این ویژگی MAC مقدار قبلی یا پیش‌فرض قابل بازگردانی ندارد؛ تغییر برای ایمنی ارائه نمی‌شود.'}
}
function Get-IdentityMacOverride($guid) {
    $p=Get-IdentityMacProperty $guid
    $values=@($p.RegistryValue)
    if($values.Count -gt 1){throw 'Unexpected multi-value MAC override.'}
    $value=if($values.Count){[string]$values[0]}else{''}
    return $value
}
function Get-IdentityFields {
    $rows=New-Object 'System.Collections.Generic.List[object]'
    $rows.Add([PSCustomObject]@{Kind='ComputerName';Target='';Label='نام کامپیوتر';Value=[Environment]::MachineName;Warning='نام قبلی پشتیبان می‌شود. نام تازه بعد از ری‌استارت کامل اعمال می‌شود.';Advanced=$false})
    $drive=[IO.Path]::GetPathRoot($env:SystemRoot).Substring(0,1)
    $disk=Get-CimInstance Win32_LogicalDisk -Filter ("DeviceID='"+$drive+":'")
    $rows.Add([PSCustomObject]@{Kind='VolumeLabel';Target=$drive;Label=('برچسب پارتیشن ویندوز ('+$drive+':)');Value=[string]$disk.VolumeName;Warning='ویژگی پارتیشن است و از سایر ویندوزها هم دیده می‌شود؛ فقط پارتیشن همین ویندوز ویرایش می‌شود.';Advanced=$false})
    try{foreach($a in @(Get-NetAdapter -Physical -ErrorAction Stop)){
        try{if($a.Status -eq 'Disabled'){continue};$property=Get-IdentityMacProperty $a.InterfaceGuid;Assert-IdentityMacRestorable $property;$rows.Add([PSCustomObject]@{Kind='MacAddress';Target=(Invariant-Guid $a.InterfaceGuid);Label=('MAC مؤثر — '+$a.Name);Value=([string]$a.MacAddress).Replace('-','').Replace(':','');Warning='اتصال شبکه هنگام اعمال قطع می‌شود. MAC دائمی سخت‌افزار عوض نمی‌شود. مقدار مؤثر و override پس از تغییر بررسی می‌شوند.';Advanced=$true})}catch{}
    }}catch{}
    foreach($kind in @('MachineGuid','InstallDate')) {
        try{
            $reg=Read-IdentityRegistry $kind;$v=[string]$reg.Value
            if($kind -eq 'InstallDate'){$v=([datetime]'1970-01-01').AddSeconds([uint32]$reg.Value).ToString('yyyy-MM-dd')}
            $label=if($kind -eq 'InstallDate'){'تاریخ نصب ثبت‌شده (UTC)'}else{'MachineGuid ویندوز — نه GUID بوت'}
            $warning=if($kind -eq 'InstallDate'){'فقط InstallDate رجیستری تغییر می‌کند؛ تاریخ واقعی نصب، InstallTime و سایر تاریخ‌ها تغییر نمی‌کنند. نمایش WMI ممکن است متفاوت باشد.'}else{'پیشرفته: نرم‌افزارها ممکن است به این شناسه وابسته باشند. تضمینی برای جداسازی سخت‌افزاری یا سازگاری همهٔ نرم‌افزارها نیست.'}
            $rows.Add([PSCustomObject]@{Kind=$kind;Target='';Label=$label;Value=$v;Warning=$warning;Advanced=$true})
        }catch{}
    }
    return $rows.ToArray()
}
function Read-IdentityState($field) {
    switch($field.Kind){
        'ComputerName' {
            $active=[Environment]::MachineName
            $configured=(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName' -Name ComputerName -ErrorAction Stop).ComputerName
            if($active -ne $configured){throw 'یک تغییر نام کامپیوتر در انتظار ری‌استارت است؛ ابتدا ری‌استارت کنید.'}
            return @{Value=$active;Target=''}
        }
        'VolumeLabel' {
            $drive=[IO.Path]::GetPathRoot($env:SystemRoot).Substring(0,1)
            if($field.Target -ne $drive){throw 'Only the current Windows volume may be edited.'}
            $disk=Get-CimInstance Win32_LogicalDisk -Filter ("DeviceID='"+$drive+":'")
            return @{Value=[string]$disk.VolumeName;Target=$drive}
        }
        'MacAddress' {
            $a=Get-IdentityAdapter $field.Target
            if($a.Status -eq 'Disabled'){throw 'ابتدا کارت شبکه را دستی فعال کنید؛ ابزار وضعیت فعال/غیرفعال کارت را عوض نمی‌کند.'}
            return @{Value=(Get-IdentityMacOverride $field.Target);Effective=([string]$a.MacAddress).Replace('-','').Replace(':','').ToUpperInvariant();Target=(Invariant-Guid $a.InterfaceGuid);Pnp=[string]$a.PnPDeviceID}
        }
        default {return (Read-IdentityRegistry $field.Kind)}
    }
}
function Set-IdentityState($kind,$target,$value) {
    switch($kind){
        'ComputerName' {Assert-IdentityValue $kind $value;Rename-Computer -NewName $value -Force -ErrorAction Stop |Out-Null}
        'VolumeLabel' {Assert-IdentityValue $kind $value;$drive=[IO.Path]::GetPathRoot($env:SystemRoot).Substring(0,1);if($target -ne $drive){throw 'Wrong Windows volume.'};Set-Volume -DriveLetter $drive -NewFileSystemLabel $value -ErrorAction Stop}
        'MacAddress' {
            $adapter=Get-IdentityAdapter $target;$property=Get-IdentityMacProperty $target
            Assert-IdentityMacPropertyTarget $property $target
            if($value){
                if($value -notmatch '^[0-9a-fA-F]{12}$' -or (([Convert]::ToInt32($value.Substring(0,2),16) -band 1) -ne 0)){throw 'Invalid MAC override.'}
                # Bind the exact instance returned with AllProperties. A ByName query
                # otherwise filters hidden/no-DisplayName values and reports no match.
                Set-NetAdapterAdvancedProperty -InputObject $property -RegistryValue ([string[]]@($value)) -ErrorAction Stop
            }else{
                if([string]::IsNullOrWhiteSpace([string]$property.DisplayName) -or $null -eq $property.DefaultRegistryValue){throw 'برای این ویژگی پنهان، بازنشانی پیش‌فرض قابل اتکا نیست؛ هیچ نوشتنی انجام نشد.'}
                Reset-NetAdapterAdvancedProperty -InputObject $property -ErrorAction Stop
            }
        }
        default {Write-IdentityRegistry $kind $value}
    }
}
function Verify-IdentityState($kind,$target,$value,$expectedEffective='') {
    switch($kind){
        'ComputerName' {return ((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName' -Name ComputerName).ComputerName -eq $value)}
        'VolumeLabel' {return ((Read-IdentityState ([PSCustomObject]@{Kind=$kind;Target=$target})).Value -ceq $value)}
        'MacAddress' {
            for($i=0;$i -lt 40;$i++){
                Start-Sleep -Milliseconds 500
                try{
                    $a=Get-IdentityAdapter $target;$live=([string]$a.MacAddress).Replace('-','').Replace(':','').ToUpperInvariant()
                    $override=Get-IdentityMacOverride $target
                    $desired=if($expectedEffective){$expectedEffective}else{$value}
                    if($override -eq $value -and $live -eq $desired){return $true}
                }catch{
                    # NIC restart/provider refresh can temporarily hide the instance.
                    # This is bounded read retry only; never retry a write blindly.
                }
            };return $false
        }
        default {return (([string](Read-IdentityRegistry $kind).Value) -ceq ([string]$value))}
    }
}
function Save-IdentityJsonNew($path,$object) {
    $file=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $writer=$null;try{$writer=New-Object IO.StreamWriter($file,(New-Object Text.UTF8Encoding $true));$writer.Write(($object|ConvertTo-Json -Depth 12));$writer.Flush();$file.Flush($true)}finally{if($writer){$writer.Dispose()}else{$file.Dispose()}}
}
function Invoke-IdentityTransaction($record,$backup,[scriptblock]$read,[scriptblock]$write,[scriptblock]$verify) {
    $now=& $read
    if(($now|ConvertTo-Json -Depth 6 -Compress) -cne ($record.Before|ConvertTo-Json -Depth 6 -Compress)){throw 'مقدار از زمان نمایش تغییر کرده؛ پنجره را دوباره باز کنید.'}
    Save-IdentityJsonNew $backup $record
    try{
        & $write $record.After
        if(!(& $verify $record.After)){throw 'مقدار پس از اعمال تأیید نشد.'}
    }catch{
        $errorText=$_.Exception.Message
        $alreadyBefore=$false
        try{$alreadyBefore=[bool](& $verify $record.Before.Value)}catch{}
        if($alreadyBefore){throw ('تغییر تأیید نشد؛ وضعیت قبلی دوباره بررسی شد و همچنان برقرار است. نوشتنِ بازگردانی لازم نبود. علت: '+$errorText)}
        try{& $write $record.Before.Value;if(!(& $verify $record.Before.Value)){throw 'Read-back mismatch.'}}
        catch{throw ('ROLLBACK_FAILED: تغییر یا بازگردانی کامل تأیید نشد. پشتیبان: '+$backup+' | Original: '+$errorText+' | Rollback: '+$_.Exception.Message)}
        throw ('تغییر تأیید نشد و مقدار قبلی بازگردانده شد. '+$errorText)
    }
}
function Apply-IdentityField($field,$value,$backup) {
    $value=Normalize-IdentityValue $field.Kind $value
    $before=Read-IdentityState $field
    if($field.Kind -eq 'MacAddress'){Assert-IdentityMacRestorable (Get-IdentityMacProperty $field.Target)}
    $display=[string]$before.Value
    if($field.Kind -eq 'InstallDate'){$display=([datetime]'1970-01-01').AddSeconds([uint32]$before.Value).ToString('yyyy-MM-dd')}
    if($value -ceq $display){throw 'مقدار تغییر نکرده است.'}
    $after=$value
    if($field.Kind -eq 'InstallDate'){$date=[datetime]::ParseExact($value,'yyyy-MM-dd',[Globalization.CultureInfo]::InvariantCulture);$after=[int]($date-([datetime]'1970-01-01')).TotalSeconds}
    $record=@{Schema='WIR-Identity-1';WindowsKey=(Get-IdentityWindowsKey);Kind=$field.Kind;Target=$field.Target;Before=$before;After=$after;CreatedUtc=[DateTime]::UtcNow.ToString('o')}
    $read={Read-IdentityState $field}.GetNewClosure()
    $write={param($v)Set-IdentityState $field.Kind $field.Target $v}.GetNewClosure()
    $verify={param($v)$expected='';if($field.Kind -eq 'MacAddress' -and [string]$v -eq [string]$before.Value){$expected=$before.Effective};Verify-IdentityState $field.Kind $field.Target $v $expected}.GetNewClosure()
    Invoke-IdentityTransaction $record $backup $read $write $verify
}
function Restore-IdentityBackup($path,$newBackup) {
    if((Get-Item $path).Length -gt 65536){throw 'Backup is too large.'}
    $record=Get-Content $path -Raw -Encoding UTF8 |ConvertFrom-Json
    if($record.Schema -ne 'WIR-Identity-1' -or $record.WindowsKey -cne (Get-IdentityWindowsKey)){throw 'این پشتیبان متعلق به ویندوز فعال نیست.'}
    $field=[PSCustomObject]@{Kind=[string]$record.Kind;Target=[string]$record.Target}
    if($field.Kind -notin @('ComputerName','VolumeLabel','MacAddress','MachineGuid','InstallDate')){throw 'Unsupported backup field.'}
    if($field.Kind -eq 'MacAddress'){
        $adapter=Get-IdentityAdapter $field.Target
        if([string]$adapter.PnPDeviceID -cne [string]$record.Before.Pnp){throw 'Adapter identity differs from backup.'}
        if($record.Before.Value -and ([string]$record.Before.Value -notmatch '^[0-9a-fA-F]{12}$' -or (([Convert]::ToInt32(([string]$record.Before.Value).Substring(0,2),16) -band 1) -ne 0))){throw 'Invalid MAC backup.'}
    }elseif($field.Kind -eq 'InstallDate'){
        if($record.Before.Type -ne 'DWord'){throw 'Wrong backup value type.'};$n=[long]$record.Before.Value;if($n -lt 0 -or $n -gt [int]::MaxValue){throw 'Invalid InstallDate backup.'}
    }else{Assert-IdentityValue $field.Kind ([string]$record.Before.Value)}
    $current=Read-IdentityState $field
    if([string]$current.Value -cne [string]$record.After){throw 'مقدار فعلی با نتیجهٔ این پشتیبان برابر نیست؛ بازگردانی خودکار مسدود شد.'}
    $reverse=@{Schema='WIR-Identity-1';WindowsKey=$record.WindowsKey;Kind=$field.Kind;Target=$field.Target;Before=$current;After=$record.Before.Value;CreatedUtc=[DateTime]::UtcNow.ToString('o')}
    $read={Read-IdentityState $field}.GetNewClosure()
    $write={param($v)Set-IdentityState $field.Kind $field.Target $v}.GetNewClosure()
    $verify={param($v)$expected='';if($field.Kind -eq 'MacAddress'){if([string]$v -eq [string]$record.Before.Value){$expected=$record.Before.Effective}else{$expected=$current.Effective}};Verify-IdentityState $field.Kind $field.Target $v $expected}.GetNewClosure()
    Invoke-IdentityTransaction $reverse $newBackup $read $write $verify
}
function New-IdentityEditorWindow($fields) {
    Add-Type -AssemblyName System.Windows.Forms;Add-Type -AssemblyName System.Drawing
    $form=New-Object Windows.Forms.Form;$form.Text='ویرایش هویت نرم‌افزاری — فقط ویندوز فعال';$form.ClientSize=New-Object Drawing.Size(1000,550);$form.StartPosition='CenterScreen';$form.Font=New-Object Drawing.Font('Segoe UI',10)
    $note=New-Object Windows.Forms.Label;$note.SetBounds(15,10,970,52);$note.TextAlign='MiddleRight';$note.Text=('ویندوز فعال: '+$env:SystemRoot+' | کاربر فرایند: '+[Environment]::UserName+"`r`nبرای هر ویندوز، ابزار را داخل همان ویندوز اجرا کنید. هیچ ری‌استارت خودکاری انجام نمی‌شود.");$form.Controls.Add($note)
    $grid=New-Object Windows.Forms.DataGridView;$grid.SetBounds(15,70,970,205);$grid.ReadOnly=$true;$grid.AllowUserToAddRows=$false;$grid.AllowUserToDeleteRows=$false;$grid.RowHeadersVisible=$false;$grid.MultiSelect=$false;$grid.SelectionMode='FullRowSelect';$grid.AutoSizeColumnsMode='Fill'
    foreach($p in @(@('label','مشخصه'),@('value','مقدار فعلی'),@('level','سطح'))){[void]$grid.Columns.Add($p[0],$p[1]);$grid.Columns[$p[0]].SortMode='NotSortable'}
    foreach($f in $fields){$level=if($f.Advanced){'پیشرفته'}else{'معمولی'};[void]$grid.Rows.Add([object[]]@($f.Label,$f.Value,$level))};$form.Controls.Add($grid)
    $warning=New-Object Windows.Forms.Label;$warning.SetBounds(15,280,970,68);$warning.TextAlign='MiddleRight';$form.Controls.Add($warning)
    $input=New-Object Windows.Forms.TextBox;$input.SetBounds(15,355,670,32);$input.MaxLength=100;$form.Controls.Add($input)
    $generate=New-Object Windows.Forms.Button;$generate.SetBounds(700,350,285,40);$generate.Text='تولید مقدار جدید';$form.Controls.Add($generate)
    $ack=New-Object Windows.Forms.CheckBox;$ack.SetBounds(15,397,970,38);$ack.Text='ریسک گزینهٔ پیشرفته و وابستگی احتمالی نرم‌افزارها / قطع شبکه را پذیرفته‌ام.';$ack.RightToLeft='Yes';$form.Controls.Add($ack)
    $apply=New-Object Windows.Forms.Button;$apply.SetBounds(700,450,285,40);$apply.Text='پشتیبان و اعمال گزینهٔ انتخاب‌شده';$form.Controls.Add($apply)
    $restore=New-Object Windows.Forms.Button;$restore.SetBounds(390,450,295,40);$restore.Text='بازگردانی از پشتیبان JSON';$form.Controls.Add($restore)
    $close=New-Object Windows.Forms.Button;$close.SetBounds(15,450,150,40);$close.Text='بستن';$close.DialogResult='Cancel';$form.Controls.Add($close);$form.CancelButton=$close
    $footer=New-Object Windows.Forms.Label;$footer.SetBounds(15,500,970,35);$footer.TextAlign='MiddleRight';$footer.Text='ساعت، کیبورد، ProductId، SID، BIOS، TPM و سریال سخت‌افزار تغییر نمی‌کنند.';$form.Controls.Add($footer)
    return @{Form=$form;Grid=$grid;Input=$input;Warning=$warning;Generate=$generate;Ack=$ack;Apply=$apply;Restore=$restore}
}
function Select-IdentityBackupFile($form,$save) {
    if($save){$d=New-Object Windows.Forms.SaveFileDialog;$d.FileName='Identity-Backup-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.json';$d.Title='یک فایل جدید برای پشتیبان انتخاب کنید'}else{$d=New-Object Windows.Forms.OpenFileDialog;$d.Title='پشتیبان همین ویندوز را انتخاب کنید'}
    $d.Filter='Identity backup (*.json)|*.json'
    try{if($d.ShowDialog($form) -eq 'OK'){return $d.FileName};return $null}finally{$d.Dispose()}
}
function Show-IdentityEditor {
    $fields=@(Get-IdentityFields);$w=New-IdentityEditorWindow $fields;$state=@{Field=$null;Exit=22}
    $select={
        if(!$w.Grid.SelectedRows.Count){return};$index=$w.Grid.SelectedRows[0].Index
        $state.Field=$fields[$index];$w.Input.Text=$state.Field.Value;$w.Warning.Text=$state.Field.Warning;$w.Ack.Checked=$false;$w.Ack.Enabled=$state.Field.Advanced;$w.Generate.Enabled=($state.Field.Kind -in @('MachineGuid','MacAddress'))
    }.GetNewClosure()
    $w.Grid.Add_SelectionChanged($select);& $select
    $w.Generate.Add_Click({if($state.Field.Kind -eq 'MachineGuid'){$w.Input.Text=New-IdentityGuid}else{$w.Input.Text=New-IdentityMac}}.GetNewClosure())
    $w.Apply.Add_Click({
        try{
            $f=$state.Field;if(!$f){return};$value=Normalize-IdentityValue $f.Kind $w.Input.Text.Trim()
            if($f.Advanced -and !$w.Ack.Checked){throw 'ابتدا هشدار گزینهٔ پیشرفته را تأیید کنید.'}
            $text=$f.Label+"`r`n"+$f.Value+' → '+$value+"`r`n`r`n"+$f.Warning+"`r`nماکرو و برنامه‌های حساس را متوقف کنید. ادامه؟"
            if([Windows.Forms.MessageBox]::Show($w.Form,$text,'تأیید تغییر','YesNo','Warning') -ne 'Yes'){return}
            $path=Select-IdentityBackupFile $w.Form $true;if(!$path){return}
            $w.Apply.Enabled=$false;$w.Restore.Enabled=$false
            Apply-IdentityField $f $value $path
            $state.Exit=0
            [void][Windows.Forms.MessageBox]::Show($w.Form,('مقدار مقصد دوباره خوانده و تأیید شد. پشتیبان: '+$path+"`r`nبرای نام کامپیوتر ری‌استارت لازم است. سایر نمایش‌ها را بعد از بازکردن دوباره ابزار بررسی کنید."),'انجام شد');$w.Form.Close()
        }catch{[void][Windows.Forms.MessageBox]::Show($w.Form,$_.Exception.Message,'خطای تغییر','OK','Error');if($_.Exception.Message -like '*ROLLBACK_FAILED*'){$state.Exit=24;$w.Form.Close()}}
        finally{$w.Apply.Enabled=$true;$w.Restore.Enabled=$true}
    }.GetNewClosure())
    $w.Restore.Add_Click({
        try{
            $path=Select-IdentityBackupFile $w.Form $false;if(!$path){return}
            if([Windows.Forms.MessageBox]::Show($w.Form,'مقدار قبلی فقط در همین ویندوز و فقط اگر مقدار فعلی با نتیجهٔ پشتیبان برابر باشد بازگردانده شود؟','تأیید بازگردانی','YesNo','Warning') -ne 'Yes'){return}
            $undo=Select-IdentityBackupFile $w.Form $true;if(!$undo){return}
            Restore-IdentityBackup $path $undo;$state.Exit=0;[void][Windows.Forms.MessageBox]::Show($w.Form,'بازگردانی و بررسی نتیجه انجام شد. برای نام کامپیوتر ری‌استارت لازم است.');$w.Form.Close()
        }catch{[void][Windows.Forms.MessageBox]::Show($w.Form,$_.Exception.Message,'خطای بازگردانی','OK','Error')}
    }.GetNewClosure())
    try{[void]$w.Form.ShowDialog()}finally{$w.Form.Dispose()};return $state.Exit
}
function Test-IdentityEditor {
    foreach($case in @(@('ComputerName','bad name'),@('ComputerName','12345'),@('ComputerName','bad-'),@('MacAddress','010000000001'),@('MachineGuid','00000000-0000-0000-0000-000000000000'),@('InstallDate','2099-01-01'),@('VolumeLabel','bad/name'))){$blocked=$false;try{Assert-IdentityValue $case[0] $case[1]}catch{$blocked=$true};if(!$blocked){throw 'Invalid identity value accepted.'}}
    Assert-IdentityValue ComputerName 'DAY-PC';Assert-IdentityValue VolumeLabel 'روز';Assert-IdentityValue InstallDate '2020-01-01'
    for($i=0;$i -lt 50;$i++){Assert-IdentityValue MacAddress (New-IdentityMac);$g=New-IdentityGuid;Assert-IdentityValue MachineGuid $g;if($g[14] -ne '4'){throw 'GUID is not UUIDv4.'}}
    $state=@{Value='old';Calls=0;Fail=$false};$record=@{Schema='WIR-Identity-1';Before=@{Value='old'};After='new'}
    $read={@{Value=$state.Value}}.GetNewClosure();$write={param($v)$state.Calls++;$state.Value=$v;if($state.Fail -and $v -eq 'new'){throw 'Injected write failure.'}}.GetNewClosure();$verify={param($v)$state.Value -ceq $v}.GetNewClosure()
    $path=Join-Path $PSScriptRoot 'identity-fixture.json'
    try{
        $blocked=$false;try{Invoke-IdentityTransaction $record (Join-Path $PSScriptRoot 'missing-folder\identity.json') $read $write $verify}catch{$blocked=$true};if(!$blocked -or $state.Calls){throw 'Write occurred without backup.'}
        $state.Value='stale';$blocked=$false;try{Invoke-IdentityTransaction $record $path $read $write $verify}catch{$blocked=$true};if(!$blocked -or $state.Calls){throw 'Stale value overwritten.'}
        $state.Value='old';$state.Fail=$true;$blocked=$false;try{Invoke-IdentityTransaction $record $path $read $write $verify}catch{$blocked=$true};if(!$blocked -or $state.Value -ne 'old'){throw 'Transaction rollback failed.'}
        Remove-Item $path -Force;$state.Fail=$false;Invoke-IdentityTransaction $record $path $read $write $verify;if($state.Value -ne 'new'){throw 'Transaction read-back failed.'}
        Remove-Item $path -Force;$state.Value='old';$state.Calls=0
        $failNoChange={param($v)$state.Calls++;throw 'No matching property; no change.'}.GetNewClosure()
        $blocked=$false;$message='';try{Invoke-IdentityTransaction $record $path $read $failNoChange $verify}catch{$blocked=$true;$message=$_.Exception.Message}
        if(!$blocked -or $state.Value -ne 'old' -or $state.Calls -ne 1 -or $message -like '*ROLLBACK_FAILED*'){throw 'No-change failure caused false rollback or false success.'}
        $w=New-IdentityEditorWindow @([PSCustomObject]@{Label='Fixture';Value='old';Advanced=$false})
        try{if(!$w.Grid.ReadOnly -or $w.Grid.Rows.Count -ne 1){throw 'Identity grid is unsafe.'}}finally{$w.Form.Dispose()}
    }finally{Remove-Item $path -Force -ErrorAction SilentlyContinue}
    Write-Output 'PASS: identity input validation, UUIDv4/LAA generation, backup-before-write, stale guard, rollback and UI contract. No live identity writes.'
}

function Test-IdentityMacInstanceBinding {
    # Scoped mocks exercise selection and safeguards. No real NIC write or restart.
    $guid='{11111111-1111-4111-8111-111111111111}'
    $property=[PSCustomObject]@{InstanceID=($guid+'::NetworkAddress');RegistryKeyword='NetworkAddress';RegistryDataType=1;RegistryValue=@('020000000001');DisplayName='';DefaultRegistryValue=$null}
    Assert-IdentityMacPropertyTarget $property $guid;Assert-IdentityMacRestorable $property
    $state=@{Property=$property;Calls=0;ResetCalls=0;Value='';Guid=$guid}
    function Get-IdentityAdapter($target){return [PSCustomObject]@{Name='Fixture [literal]';InterfaceGuid=$state.Guid}}
    function Get-IdentityMacProperty($target){return $state.Property}
    function Set-NetAdapterAdvancedProperty {param($InputObject,$RegistryValue,$ErrorAction)
        if(![object]::ReferenceEquals($InputObject,$state.Property)){throw 'Setter did not bind exact property instance.'}
        $state.Calls++;$state.Value=[string]$RegistryValue[0]
    }
    function Reset-NetAdapterAdvancedProperty {param($InputObject,$ErrorAction)$state.ResetCalls++}
    Set-IdentityState MacAddress $guid '020000000002'
    if($state.Calls -ne 1 -or $state.Value -ne '020000000002'){throw 'Hidden MAC property setter failed.'}
    $blocked=$false;try{Set-IdentityState MacAddress $guid ''}catch{$blocked=$true}
    if(!$blocked -or $state.ResetCalls){throw 'Hidden property without defaults was reset.'}
    $property.RegistryValue=@('');$blocked=$false;try{Assert-IdentityMacRestorable $property}catch{$blocked=$true};if(!$blocked){throw 'Nonrestorable hidden property was editable.'}
    $property.InstanceID='{22222222-2222-4222-8222-222222222222}::NetworkAddress'
    $blocked=$false;try{Set-IdentityState MacAddress $guid '020000000003'}catch{$blocked=$true};if(!$blocked -or $state.Calls -ne 1){throw 'Wrong adapter property was written.'}
    Write-Output 'PASS: exact hidden MAC CIM-instance binding, target GUID/type guards and refusal of unsafe hidden default reset; no real NIC mutation.'
}
