# UUIDv4 boot-loader cloning with no replacement of an existing object, no reboot, and no firmware edits.
function Export-BootGuidStore($path,[string]$file='') {
    if(Test-Path -LiteralPath $path){throw 'A new backup filename is required.'}
    if($file){[IO.File]::Copy($file,$path,$false)}
    elseif(!(Bcd-StoreReference '').ExportStore($path).ReturnValue){throw 'Full BCD export failed. No changes made.'}
    if(!(Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -lt 4096){throw 'BCD export is incomplete.'}
}
function Get-BootGuidManager([string]$file='') {
    $m=Bcd-EditableObject '{9dea862c-5cdd-4e70-acc1-f32b344d4795}' $file
    $d=Get-BcdElement $m ([uint32]0x23000003);$order=Get-BcdElement $m ([uint32]0x24000001)
    if(!$d -or !$order){throw 'Default and display order must be readable; no BCD changes are allowed.'}
    return @{Default=(Invariant-Guid $d.Id);Order=@($order.Ids|ForEach-Object{Invariant-Guid $_})}
}
function Write-BootGuidManager($state,[string]$file='') {
    $m=Bcd-EditableObject '{9dea862c-5cdd-4e70-acc1-f32b344d4795}' $file
    if(!$m.SetObjectElement([uint32]0x23000003,[string]$state.Default).ReturnValue){throw 'Cannot set boot default.'}
    if(!$m.SetObjectListElement([uint32]0x24000001,[string[]]$state.Order).ReturnValue){throw 'Cannot set boot display order.'}
    $read=Get-BootGuidManager $file
    if($read.Default -ne $state.Default -or ($read.Order -join ',') -ne ($state.Order -join ',')){throw 'Boot manager read-back mismatch.'}
}
function New-BootGuidPlan($entries,$selected,$allIds) {
    $lookup=@{};foreach($e in $entries){$id=Invariant-Guid $e.Id;if(!$id -or $lookup.ContainsKey($id)){throw 'Invalid loader list.'};$lookup[$id]=$e}
    $seen=@{};foreach($id in $allIds){$normal=Invariant-Guid $id;if($normal){$seen[$normal]=$true}}
    $chosen=@{};$result=@()
    foreach($oldValue in $selected){
        $old=Invariant-Guid $oldValue
        if(!$old -or !$lookup.ContainsKey($old) -or $chosen.ContainsKey($old)){throw 'Unknown or duplicate selected loader.'}
        $chosen[$old]=$true
        do{$new='{'+[Guid]::NewGuid().ToString('D')+'}'}while($seen.ContainsKey($new))
        $seen[$new]=$true;$label=[string]$lookup[$old].Name
        if($label.Length -gt 85){$label=$label.Substring(0,85)}
        $result+= [PSCustomObject]@{Old=$old;New=$new;Name=[string]$lookup[$old].Name;NewName=($label+' [NEW '+$new.Substring(1,8)+']');Fingerprint='';Tested=$false;TestedUtc=''}
    }
    if(!$result.Count -or $result.Count -gt 20){throw 'Select between 1 and 20 Windows boot loaders.'}
    return $result
}
function Save-BootGuidMap($path,$map,[switch]$New) {
    if($New){Save-IdentityJsonNew $path $map;return}
    # Atomic same-directory replacement, never truncate the only mapping.
    $temp=$path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    try{Save-IdentityJsonNew $temp $map;[IO.File]::Replace($temp,$path,$null)}finally{Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue}
}
function Write-BootGuidText($path,$map) {
    $lines=@('Windows Identity Report — نگاشت GUID بوت','GUIDها مربوط به ورودی بوت هستند؛ نه کاربر، دیسک یا MachineGuid.','Status: '+$map.Status,'Full BCD backup: '+$map.Backup,'هیچ ری‌استارت خودکاری انجام نشده. ورودی‌های قدیمی تا تکمیل تست باقی می‌مانند.','پس از تست، میان‌برهای سوییچ را به GUID جدید تغییر دهید.','')
    foreach($e in $map.Entries){$lines+=@('Name: '+$e.Name,'OLD: '+$e.Old,'NEW: '+$e.New,'New boot name: '+$e.NewName,'Tested: '+$e.Tested,'Shortcut target (NOT EXECUTED): %SystemRoot%\System32\cmd.exe /c "bcdedit /default '+$e.New+' && bcdedit /timeout 10"','')}
    [IO.File]::WriteAllLines($path,$lines,(New-Object Text.UTF8Encoding $true))
}
function Get-AllBootGuidIds([string]$file='') {
    $objects=(Bcd-StoreReference $file).EnumerateObjects([uint32]0)
    if(!$objects.ReturnValue){throw 'Cannot enumerate complete BCD store.'}
    return @($objects.Objects|ForEach-Object{Invariant-Guid $_.Id})
}
function Invoke-BootGuidCreate($plan,$folder,[string]$file='') {
    if(!(Test-Path -LiteralPath $folder -PathType Container)){throw 'Backup folder is absent.'}
    $tag='Boot-GUID-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
    $backup=Join-Path $folder ($tag+'-backup.bcd');$mapPath=Join-Path $folder ($tag+'-map.json');$text=Join-Path $folder ($tag+'-map.txt')
    $stage=Join-Path $PSScriptRoot ($tag+'-stage.bcd');$verify=Join-Path $PSScriptRoot ($tag+'-verify.bcd')
    $before=Get-BootGuidManager $file;$ids=Get-AllBootGuidIds $file
    foreach($e in $plan){if($ids -notcontains $e.Old -or $ids -contains $e.New){throw 'Boot entries changed; regenerate the plan.'};if((Read-BootDescription $e.Old $file) -cne $e.Name){throw 'Boot name changed; reopen the editor.'}}
    Export-BootGuidStore $backup $file
    $backupHash=Hash-File $backup
    [IO.File]::Copy($backup,$stage,$false)
    $map=@{Schema='WIR-BootGuid-1';Status='Planned';Backup=[IO.Path]::GetFileName($backup);BackupSha256=$backupHash;CreatedUtc=[DateTime]::UtcNow.ToString('o');Manager=$before;Entries=@($plan)}
    foreach($e in $plan){$e.Fingerprint=[BcdGuidStager]::Fingerprint($stage,$e.Old);[BcdGuidStager]::CloneObject($stage,$e.Old,$e.New);if([BcdGuidStager]::Fingerprint($stage,$e.New) -ne $e.Fingerprint){throw 'Staging clone did not match original.'}}
    Save-BootGuidMap $mapPath $map -New;Write-BootGuidText $text $map
    $created=New-Object 'System.Collections.Generic.List[string]';$managerTouched=$false
    try{
        $store=Bcd-StoreReference $file
        foreach($e in $plan){
            # Flag zero preserves the exact UUIDv4 from the staged copy. NEVER DeleteExistingObject.
            $copied=$store.CopyObject($stage,$e.New,[uint32]0)
            if(!$copied.ReturnValue){throw 'CopyObject rejected a new boot entry.'}
            $created.Add($e.New)
            if((Invariant-Guid $copied.Object.Id) -ne $e.New){throw 'Provider changed the requested UUIDv4.'}
            if(!(Write-BootDescription $e.New $e.NewName $file)){throw 'New boot description failed.'}
        }
        # Export after import and compare raw configuration, including unknown/device elements.
        Export-BootGuidStore $verify $file
        foreach($e in $plan){if([BcdGuidStager]::Fingerprint($verify,$e.Old) -ne $e.Fingerprint -or [BcdGuidStager]::Fingerprint($verify,$e.New) -ne $e.Fingerprint){throw 'Imported boot configuration differs from source.'}}
        $current=Get-BootGuidManager $file
        if($current.Default -ne $before.Default -or ($current.Order -join ',') -ne ($before.Order -join ',')){throw 'Boot manager changed concurrently.'}
        $next=@{Default=$before.Default;Order=@($before.Order)+@($plan.New)};$managerTouched=$true;Write-BootGuidManager $next $file
        $map.Status='Created';Save-BootGuidMap $mapPath $map;Write-BootGuidText $text $map
    }catch{
        $reason=$_.Exception.Message;$errors=@()
        if($managerTouched){try{Write-BootGuidManager $before $file}catch{$errors+=$_.Exception.Message}}
        $store=Bcd-StoreReference $file
        foreach($id in $created){try{if(!$store.DeleteObject($id).ReturnValue){throw 'Cannot remove newly-created entry.'}}catch{$errors+=$_.Exception.Message}}
        $map.Status=if($errors.Count){'RollbackFailed'}else{'RolledBack'}
        try{Save-BootGuidMap $mapPath $map;Write-BootGuidText $text $map}catch{$errors+=$_.Exception.Message}
        if($errors.Count){throw ('ROLLBACK_FAILED: '+($errors -join ' | ')+' | Backup: '+$backup)}
        throw ('ساخت GUID تأیید نشد؛ ورودی‌های تازه حذف و تنظیمات قبلی بازگردانده شدند. '+$reason)
    }finally{Remove-Item -LiteralPath $stage,$verify -Force -ErrorAction SilentlyContinue;Get-ChildItem $PSScriptRoot -Filter ($tag+'-*.*.LOG*') -ErrorAction SilentlyContinue|Remove-Item -Force -ErrorAction SilentlyContinue}
    return $mapPath
}
function Read-BootGuidMap($path) {
    if((Get-Item -LiteralPath $path).Length -gt 131072){throw 'Mapping too large.'}
    $map=Get-Content -LiteralPath $path -Raw -Encoding UTF8|ConvertFrom-Json
    if($map.Schema -ne 'WIR-BootGuid-1' -or $map.Status -ne 'Created' -or !$map.Entries -or @($map.Entries).Count -gt 20){throw 'Only a completed creation mapping is accepted.'}
    if($map.Backup -ne [IO.Path]::GetFileName($map.Backup) -or $map.Backup -notmatch '\.bcd$'){throw 'Invalid backup filename.'}
    $backup=Join-Path ([IO.Path]::GetDirectoryName($path)) $map.Backup
    if((Hash-File $backup) -ne $map.BackupSha256){throw 'Full BCD backup hash differs from mapping.'}
    $seen=@{};foreach($e in $map.Entries){foreach($key in @('Old','New')){
        $id=Invariant-Guid $e.$key;if(!$id -or $id -ne $e.$key -or $seen.ContainsKey($id)){throw 'Invalid or duplicate mapped GUID.'};$seen[$id]=$true
    };if($e.Fingerprint -notmatch '^[0-9a-f]{64}$' -or [BcdGuidStager]::Fingerprint($backup,$e.Old) -ne $e.Fingerprint){throw 'Source configuration fingerprint does not match backup.'}}
    return $map
}
function Get-CurrentBootGuid {
    $class=[wmiclass]'root\WMI:BcdStore';$class.Scope.Options.EnablePrivileges=$true
    $opened=$class.OpenStore('');if(!$opened.ReturnValue){throw 'Cannot resolve current boot loader.'}
    $object=(Bcd-StoreReference '').OpenObject('{fa926493-6f1c-4193-a414-58f0b2456d1e}')
    $id=if($object.ReturnValue){Invariant-Guid $object.Object.Id}else{$null}
    if($id -and $id -ne '{fa926493-6f1c-4193-a414-58f0b2456d1e}'){return $id}
    $lines=& "$env:SystemRoot\System32\bcdedit.exe" /enum '{current}' /v 2>&1
    if($LASTEXITCODE -ne 0){throw 'Cannot resolve current boot loader.'}
    foreach($line in $lines){if([string]$line -match '^\S+\s+(\{[0-9a-fA-F-]{36}\})\s*$'){return (Invariant-Guid $matches[1])}}
    throw 'Current boot GUID could not be established; no test can be recorded.'
}
function Record-BootGuidTest($path,$current) {
    $map=Read-BootGuidMap $path;$entry=@($map.Entries|Where-Object{$_.New -eq $current})
    if($entry.Count -ne 1){throw 'این ویندوز از یکی از GUIDهای جدید این نگاشت بوت نشده است؛ تست ثبت نمی‌شود.'}
    $tmp=Join-Path $PSScriptRoot ('test-check-'+[Guid]::NewGuid().ToString('N')+'.bcd')
    try{Export-BootGuidStore $tmp;if([BcdGuidStager]::Fingerprint($tmp,$current) -ne $entry[0].Fingerprint){throw 'Tested boot configuration differs from original.'}}
    finally{Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue}
    $entry[0].Tested=$true;$entry[0].TestedUtc=[DateTime]::UtcNow.ToString('o');Save-BootGuidMap $path $map;Write-BootGuidText ([IO.Path]::ChangeExtension($path,'.txt')) $map
}
function Assert-BootGuidCleanup($map,$current,[string]$file='') {
    $entries=@(Get-BootNameEntries $file);$allowed=@{};foreach($e in $entries){$allowed[$e.Id]=$true}
    foreach($e in $map.Entries){if($e.Tested -isnot [bool] -or !$e.Tested -or !$e.TestedUtc){throw 'ابتدا بوت موفق تک‌تک ورودی‌های جدید را داخل همان ویندوز ثبت کنید.'};if(!$allowed.ContainsKey($e.Old) -or !$allowed.ContainsKey($e.New)){throw 'Mapped old/new Windows loader is absent.'};if($e.Old -eq $current){throw 'هنوز از یک ورودی قدیمی بوت هستید؛ حذف ممنوع است.'}}
    $old=@($map.Entries.Old);$objects=(Bcd-StoreReference $file).EnumerateObjects([uint32]0)
    if(!$objects.ReturnValue){throw 'Cannot audit BCD references.'}
    foreach($object in @($objects.Objects)){
        $id=Invariant-Guid $object.Id;if($old -contains $id){continue}
        $elements=(Bcd-EditableObject $id $file).EnumerateElements()
        if(!$elements.ReturnValue){throw 'Cannot audit BCD object elements.'}
        foreach($element in @($elements.Elements)){
            $type=[uint32]$element.Type;$format=($type -shr 24) -band 15;$refs=@()
            if($format -eq 3){$refs=@($element.Id)}elseif($format -eq 4){$refs=@($element.Ids)}elseif($format -eq 1){
                # Device structures can contain nested additional-options GUIDs. Search their serialized provider text conservatively.
                $raw=[string]$element.Device.GetText([Management.TextFormat]::Mof)
                foreach($oldId in $old){if($raw.IndexOf($oldId,[StringComparison]::OrdinalIgnoreCase) -ge 0){$refs+=$oldId}}
            }
            foreach($ref in $refs){$normal=Invariant-Guid $ref;if($old -notcontains $normal){continue}
                if($id -eq '{9dea862c-5cdd-4e70-acc1-f32b344d4795}' -and $type -in @([uint32]0x23000003,[uint32]0x24000001)){continue}
                throw ('یک ارجاع BCD به GUID قدیمی باقی است؛ برای ایمنی حذف مسدود شد. Object: '+$id+' Element: 0x'+$type.ToString('x8'))
            }
        }
    }
}
function Invoke-BootGuidCleanup($path,$current,[string]$file='') {
    $map=Read-BootGuidMap $path;Assert-BootGuidCleanup $map $current $file
    $folder=[IO.Path]::GetDirectoryName($path);$backup=Join-Path $folder ('Boot-GUID-cleanup-'+[Guid]::NewGuid().ToString('N')+'.bcd')
    Export-BootGuidStore $backup $file
    foreach($e in $map.Entries){foreach($id in @($e.Old,$e.New)){if([BcdGuidStager]::Fingerprint($backup,$id) -ne $e.Fingerprint){throw 'Boot configuration changed after creation; cleanup is blocked.'}}}
    $before=Get-BootGuidManager $file;$replacement=@{};foreach($e in $map.Entries){$replacement[$e.Old]=$e.New}
    $default=$before.Default;if($replacement.ContainsKey($default)){$default=$replacement[$default]}
    $next=@{Default=$default;Order=@($before.Order|Where-Object{!$replacement.ContainsKey($_)})}
    if(!$next.Order.Count){throw 'Cannot empty boot menu.'}
    $deleted=New-Object 'System.Collections.Generic.List[string]';$managerTouched=$false
    try{
        $managerTouched=$true;Write-BootGuidManager $next $file;$store=Bcd-StoreReference $file
        foreach($e in $map.Entries){if(!$store.DeleteObject($e.Old).ReturnValue){throw 'Failed to delete a tested old loader.'};$deleted.Add($e.Old)}
        $remaining=Get-AllBootGuidIds $file
        foreach($e in $map.Entries){if($remaining -contains $e.Old -or $remaining -notcontains $e.New){throw 'Cleanup read-back mismatch.'}}
        $map.Status='Cleaned';$map|Add-Member NoteProperty CleanupBackup ([IO.Path]::GetFileName($backup)) -Force
        Save-BootGuidMap $path $map;Write-BootGuidText ([IO.Path]::ChangeExtension($path,'.txt')) $map
    }catch{
        $reason=$_.Exception.Message;$errors=@();$store=Bcd-StoreReference $file
        foreach($id in $deleted){try{if(!$store.CopyObject($backup,$id,[uint32]0).ReturnValue){throw 'Cannot restore original loader.'}}catch{$errors+=$_.Exception.Message}}
        if($managerTouched){try{Write-BootGuidManager $before $file}catch{$errors+=$_.Exception.Message}}
        if($errors.Count){throw ('ROLLBACK_FAILED: '+($errors -join ' | ')+' | Full backup: '+$backup)}
        throw ('حذف کامل نشد؛ ورودی‌های حذف‌شده و منوی قبلی بازگردانده شدند. '+$reason)
    }
}
function New-BootGuidWindow($entries) {
    Add-Type -AssemblyName System.Windows.Forms;Add-Type -AssemblyName System.Drawing
    $form=New-Object Windows.Forms.Form;$form.Text='GUID تصادفی بوت — ساخت، تست و حذف امن';$form.ClientSize=New-Object Drawing.Size(1060,550);$form.Font=New-Object Drawing.Font('Segoe UI',10);$form.StartPosition='CenterScreen'
    $label=New-Object Windows.Forms.Label;$label.SetBounds(15,12,1030,66);$label.TextAlign='MiddleRight';$label.Text="مرحلهٔ ۱: نسخهٔ جدید UUIDv4؛ ورودی‌های قبلی و پیش‌فرض حفظ می‌شوند. ری‌استارت خودکار نداریم.`r`nمرحلهٔ ۲: هر ویندوز را از ورودی NEW بوت کنید و تستش را ثبت کنید؛ سپس حذف قدیمی‌ها فعال می‌شود.";$form.Controls.Add($label)
    $grid=New-Object Windows.Forms.DataGridView;$grid.SetBounds(15,85,1030,230);$grid.AllowUserToAddRows=$false;$grid.AllowUserToDeleteRows=$false;$grid.RowHeadersVisible=$false;$grid.AutoSizeColumnsMode='Fill'
    $check=New-Object Windows.Forms.DataGridViewCheckBoxColumn;$check.Name='selected';$check.HeaderText='انتخاب';$check.FillWeight=10;[void]$grid.Columns.Add($check)
    foreach($p in @(@('name','نام بوت',25),@('old','GUID فعلی',35),@('new','GUID پیشنهادی UUIDv4',35))){$c=New-Object Windows.Forms.DataGridViewTextBoxColumn;$c.Name=$p[0];$c.HeaderText=$p[1];$c.FillWeight=$p[2];$c.ReadOnly=$true;$c.SortMode='NotSortable';[void]$grid.Columns.Add($c)}
    $grid.Columns['selected'].SortMode='NotSortable';foreach($e in $entries){[void]$grid.Rows.Add([object[]]@($false,$e.Name,$e.Id,''))};$form.Controls.Add($grid)
    $ack=New-Object Windows.Forms.CheckBox;$ack.SetBounds(15,325,1030,42);$ack.RightToLeft='Yes';$ack.Text='ماکرو متوقف است و اگر BitLocker فعال باشد، کلید بازیابی آن را خارج از برنامه در دسترس دارم.';$form.Controls.Add($ack)
    $buttons=@{};$specs=@(@('generate','تولید GUIDهای پیشنهادی',15,380,310),@('create','پشتیبان کامل و ساخت ورودی‌ها',345,380,330),@('open','بازکردن نگاشت / ثبت تست',695,380,350),@('cleanup','مرحلهٔ ۲: حذف قدیمی‌های تست‌شده',345,435,700))
    foreach($s in $specs){$b=New-Object Windows.Forms.Button;$b.Text=$s[1];$b.SetBounds($s[2],$s[3],$s[4],42);$buttons[$s[0]]=$b;$form.Controls.Add($b)}
    $status=New-Object Windows.Forms.Label;$status.SetBounds(15,490,1030,45);$status.TextAlign='MiddleRight';$status.Text='فایل نگاشت JSON و TXT و پشتیبان کامل BCD را در یک پوشهٔ قابل دسترسی از هر سه ویندوز نگه دارید.';$form.Controls.Add($status)
    return @{Form=$form;Grid=$grid;Ack=$ack;Buttons=$buttons;Status=$status}
}
function Show-BootGuidEditor {
    $entries=@(Get-BootNameEntries);$w=New-BootGuidWindow $entries;$state=@{Plan=@();Path='';Exit=22}
    $w.Buttons.generate.Add_Click({try{
        [void]$w.Grid.EndEdit();$ids=@();foreach($row in $w.Grid.Rows){if($row.Cells['selected'].Value -eq $true){$ids+=[string]$row.Cells['old'].Value}}
        $state.Plan=@(New-BootGuidPlan $entries $ids (Get-AllBootGuidIds))
        foreach($row in $w.Grid.Rows){$row.Cells['new'].Value='';foreach($e in $state.Plan){if($e.Old -eq $row.Cells['old'].Value){$row.Cells['new'].Value=$e.New}}}
    }catch{[void][Windows.Forms.MessageBox]::Show($w.Form,$_.Exception.Message,'خطا','OK','Error')}}.GetNewClosure())
    $w.Buttons.create.Add_Click({try{
        [void]$w.Grid.EndEdit();if(!$w.Ack.Checked){throw 'هشدار توقف ماکرو و BitLocker را تأیید کنید.'}
        $selected=@();foreach($row in $w.Grid.Rows){if($row.Cells['selected'].Value -eq $true){$selected+=[string]$row.Cells['old'].Value}}
        if(!$state.Plan.Count -or (($selected|Sort-Object)-join ',') -ne (($state.Plan.Old|Sort-Object)-join ',')){throw 'پس از انتخاب ورودی‌ها، دکمهٔ تولید GUID را بزنید.'}
        if([Windows.Forms.MessageBox]::Show($w.Form,'ورودی‌های جدید ساخته و به منو اضافه شوند؟ ورودی‌های قبلی و پیش‌فرض فعلی باقی می‌مانند. ممکن است BitLocker در بوت بعدی کلید بازیابی بخواهد.','تأیید ساخت','YesNo','Warning') -ne 'Yes'){return}
        $d=New-Object Windows.Forms.FolderBrowserDialog;$d.Description='پوشهٔ پشتیبان و نگاشت (قابل دسترسی از تمام ویندوزها)'
        try{if($d.ShowDialog($w.Form) -ne 'OK'){return};$folder=$d.SelectedPath}finally{$d.Dispose()}
        $w.Buttons.create.Enabled=$false;$state.Path=Invoke-BootGuidCreate $state.Plan $folder;$state.Exit=0;$state.Plan=@();$w.Status.Text='ساخته شد: '+$state.Path
        [void][Windows.Forms.MessageBox]::Show($w.Form,'ساخت و تطبیق تنظیمات تأیید شد. دستی ری‌استارت کنید، هر ورودی NEW را تست و سپس نگاشت JSON را در همان ویندوز باز کنید. قدیمی‌ها هنوز باقی‌اند.','ساخته شد')
    }catch{[void][Windows.Forms.MessageBox]::Show($w.Form,$_.Exception.Message,'ساخت ناموفق','OK','Error')}finally{$w.Buttons.create.Enabled=$true}}.GetNewClosure())
    $w.Buttons.open.Add_Click({try{
        $d=New-Object Windows.Forms.OpenFileDialog;$d.Filter='Boot GUID mapping (*.json)|*.json';try{if($d.ShowDialog($w.Form) -ne 'OK'){return};$path=$d.FileName}finally{$d.Dispose()}
        $map=Read-BootGuidMap $path;$state.Path=$path;$current=Get-CurrentBootGuid
        $summary=($map.Entries|ForEach-Object{$_.Name+' | New: '+$_.New+' | Tested: '+$_.Tested}) -join "`r`n"
        $w.Status.Text=(@($map.Entries|Where-Object{$_.Tested}).Count.ToString()+' / '+@($map.Entries).Count+' تست ثبت‌شده — '+$path)
        if(@($map.Entries.New) -contains $current){if([Windows.Forms.MessageBox]::Show($w.Form,($summary+"`r`n`r`nویندوز فعلی از GUID جدید بوت شده. ورود و کارکرد آن موفق بود؟ ثبت تست؟"),'تأیید تست همین ویندوز','YesNo','Question') -eq 'Yes'){Record-BootGuidTest $path $current;$state.Exit=0;$w.Status.Text='تست همین ویندوز ثبت شد؛ باقی ویندوزها را جداگانه تست کنید.'}}
        else{[void][Windows.Forms.MessageBox]::Show($w.Form,($summary+"`r`nبرای ثبت تست، از یکی از ورودی‌های NEW بوت شوید."),'وضعیت تست')}
    }catch{[void][Windows.Forms.MessageBox]::Show($w.Form,$_.Exception.Message,'خطای نگاشت','OK','Error')}}.GetNewClosure())
    $w.Buttons.cleanup.Add_Click({try{
        if(!$state.Path){throw 'ابتدا نگاشت JSON را باز کنید.'};if(!$w.Ack.Checked){throw 'هشدار را تأیید کنید.'}
        $map=Read-BootGuidMap $state.Path;$current=Get-CurrentBootGuid;Assert-BootGuidCleanup $map $current
        if([Windows.Forms.MessageBox]::Show($w.Form,'همهٔ تست‌ها ثبت شده‌اند. فقط ورودی‌های قدیمی همین نگاشت حذف شوند؟ اگر پیش‌فرض قدیمی باشد، به نسخهٔ جدید تست‌شده منتقل می‌شود. میان‌برها را با GUID جدید هماهنگ کنید.','تأیید نهایی حذف','YesNo','Warning') -ne 'Yes'){return}
        Invoke-BootGuidCleanup $state.Path $current;$state.Exit=0;$w.Status.Text='حذف قدیمی‌ها و بررسی نتیجه کامل شد؛ پشتیبان حذف نیز ذخیره شد.'
    }catch{[void][Windows.Forms.MessageBox]::Show($w.Form,$_.Exception.Message,'حذف مسدود / ناموفق','OK','Error')}}.GetNewClosure())
    try{[void]$w.Form.ShowDialog()}finally{$w.Form.Dispose()};return $state.Exit
}
function Test-BootGuidEditor {
    $id='{11111111-1111-1111-1111-111111111111}';$entry=[PSCustomObject]@{Id=$id;Name='Fixture'}
    $plan=@(New-BootGuidPlan @($entry) @($id) @($id));if($plan.Count -ne 1 -or $plan[0].Old -eq $plan[0].New -or $plan[0].New[15] -ne '4'){throw 'UUIDv4 clone plan is invalid.'}
    $blocked=$false;try{New-BootGuidPlan @($entry) @($id,$id) @($id)|Out-Null}catch{$blocked=$true};if(!$blocked){throw 'Duplicate GUID selection accepted.'}
    $folder=Join-Path $PSScriptRoot ('guid-fixture-'+[Guid]::NewGuid().ToString('N'));[void][IO.Directory]::CreateDirectory($folder);$fixture=Join-Path $folder 'fixture.bcd'
    try{
        $class=[wmiclass]'root\WMI:BcdStore';$class.Scope.Options.EnablePrivileges=$true;if(!$class.CreateStore($fixture).ReturnValue){throw 'GUID fixture store creation failed.'}
        $store=Bcd-StoreReference $fixture;if(!$store.CreateObject($id,[uint32]0x10200003).ReturnValue){throw 'Fixture loader creation failed.'}
        if(!(Write-BootDescription $id 'Fixture' $fixture)){throw 'Fixture description failed.'}
        $obj=Bcd-EditableObject $id $fixture;if(!$obj.SetStringElement([uint32]0x22000002,'\Windows').ReturnValue){throw 'Fixture Windows root failed.'}
        if(!$obj.SetIntegerElement([uint32]0x25000020,[uint64]3).ReturnValue){throw 'Fixture integer failed.'}
        $managerId='{9dea862c-5cdd-4e70-acc1-f32b344d4795}';if(!$store.CreateObject($managerId,[uint32]0x10100002).ReturnValue){throw 'Fixture manager failed.'}
        Write-BootGuidManager @{Default=$id;Order=@($id)} $fixture
        $mapPath=Invoke-BootGuidCreate $plan $folder $fixture;$map=Read-BootGuidMap $mapPath
        if(@(Get-BootNameEntries $fixture).Count -ne 2){throw 'New and old entries must coexist.'}
        $state=Get-BootGuidManager $fixture;if($state.Default -ne $id -or $state.Order.Count -ne 2){throw 'Creation altered default or lost original menu entry.'}
        $blocked=$false;try{Assert-BootGuidCleanup $map $plan[0].New $fixture}catch{$blocked=$true};if(!$blocked){throw 'Untested cleanup accepted.'}
        $map.Entries[0].Tested=$true;$map.Entries[0].TestedUtc=[DateTime]::UtcNow.ToString('o');Save-BootGuidMap $mapPath $map
        $blocked=$false;try{Assert-BootGuidCleanup $map $id $fixture}catch{$blocked=$true};if(!$blocked){throw 'Current old loader deletion accepted.'}
        Invoke-BootGuidCleanup $mapPath $plan[0].New $fixture
        $after=@(Get-BootNameEntries $fixture);if($after.Count -ne 1 -or $after[0].Id -ne $plan[0].New){throw 'Fixture cleanup failed.'}
        $state=Get-BootGuidManager $fixture;if($state.Default -ne $plan[0].New -or $state.Order.Count -ne 1){throw 'Fixture default migration failed.'}
        $w=New-BootGuidWindow @($entry);try{if(!$w.Grid.Columns['old'].ReadOnly -or !$w.Grid.Columns['new'].ReadOnly){throw 'GUID columns must be read-only.'}}finally{$w.Form.Dispose()}
    }finally{Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue}
    Write-Output 'PASS: UUIDv4, exact raw BCD cloning/import, preserved original/default, full backups/maps, untested/current guards and cleanup on isolated BCD only.'
}
