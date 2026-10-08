# Only the Description string (0x12000004) can be written by this editor.
function Bcd-StoreReference([string]$file) {
    $class=[wmiclass]'root\WMI:BcdStore';$class.Scope.Options.EnablePrivileges=$true
    $opened=$class.OpenStore($file);if(!$opened.ReturnValue){throw 'Cannot open selected store.'}
    $file=[string]$opened.Store.FilePath
    $escaped=$file.Replace('\','\\').Replace('"','\"')
    $store=[wmi]('root\WMI:BcdStore.FilePath="'+$escaped+'"')
    $store.Scope.Options.EnablePrivileges=$true
    return $store
}
function Bcd-EditableObject([string]$guid,[string]$file='') {
    $guid=Invariant-Guid $guid;if(!$guid){throw 'Invalid boot GUID.'}
    $class=[wmiclass]'root\WMI:BcdStore';$class.Scope.Options.EnablePrivileges=$true
    $opened=$class.OpenStore($file);if(!$opened.ReturnValue){throw 'Cannot open selected store.'}
    $file=[string]$opened.Store.FilePath
    $escaped=$file.Replace('\','\\').Replace('"','\"')
    $object=[wmi]('root\WMI:BcdObject.Id="'+$guid+'",StoreFilePath="'+$escaped+'"')
    $object.Scope.Options.EnablePrivileges=$true
    return $object
}
function Get-BootNameEntries([string]$file='') {
    $class=[wmiclass]'root\WMI:BcdStore';$class.Scope.Options.EnablePrivileges=$true
    $opened=$class.OpenStore($file);if(!$opened.ReturnValue){throw 'Cannot open the boot store.'}
    $objects=(Bcd-StoreReference $file).EnumerateObjects([uint32]0x10200003)
    if(!$objects.ReturnValue){throw 'Cannot enumerate Windows boot loader entries.'}
    foreach($entry in @($objects.Objects)) {
        $id=Invariant-Guid $entry.Id;if(!$id){throw 'Boot store returned an invalid identifier.'}
        $object=Bcd-EditableObject $id $file
        $element=Get-BcdElement $object ([uint32]0x12000004)
        if(!$element){throw ('Cannot read the existing boot name: '+$id)}
        [PSCustomObject]@{Id=$id;Name=[string]$element.String}
    }
}
function New-BootRenamePlan($entries,$requested) {
    $allowed=@{};foreach($entry in $entries){
        $id=Invariant-Guid $entry.Id
        if(!$id -or $allowed.ContainsKey($id)){throw 'Invalid or duplicate source GUID.'}
        $allowed[$id]=[string]$entry.Name
    }
    $plan=@();$seen=@{}
    foreach($idValue in $requested.Keys) {
        $id=Invariant-Guid $idValue
        if(!$id -or !$allowed.ContainsKey($id) -or $seen.ContainsKey($id)){throw 'Unknown or duplicate target GUID.'}
        $seen[$id]=$true;$name=([string]$requested[$idValue]).Trim()
        if(!$name -or $name.Length -gt 120 -or $name -match '[\x00-\x1f\x7f]'){throw 'نام جدید باید ۱ تا ۱۲۰ کاراکتر و بدون خط جدید یا نویسهٔ کنترلی باشد.'}
        if(![string]::Equals($allowed[$id],$name,[StringComparison]::Ordinal)){
            $plan += [PSCustomObject]@{Id=$id;Old=$allowed[$id];New=$name}
        }
    }
    return $plan
}
function Invoke-BootRenamePlan($plan,[string]$backupPath,[scriptblock]$reader,[scriptblock]$writer) {
    if(!$plan.Count){return}
    # Recheck all changed descriptions before any write, so stale UI cannot overwrite concurrent changes.
    foreach($change in $plan){
        if(![string]::Equals([string](& $reader $change.Id),$change.Old,[StringComparison]::Ordinal)){
            throw 'نام ورودی بوت از زمان بازشدن پنجره تغییر کرده است؛ پنجره را ببندید و دوباره باز کنید.'
        }
    }
    $lines=New-Object 'System.Collections.Generic.List[string]'
    $lines.Add('پشتیبان نام‌های منوی بوت — قبل از ویرایش')
    $lines.Add('فقط نام‌ها ذخیره شده‌اند؛ این فایل پشتیبان کامل BCD نیست.')
    $lines.Add('زمان: '+(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
    foreach($change in $plan){$lines.Add('');$lines.Add('GUID: '+$change.Id);$lines.Add('Old name: '+$change.Old);$lines.Add('Requested name: '+$change.New)}
    # A new backup is mandatory and is completed before invoking a setter.
    $file=[IO.File]::Open($backupPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $textWriter=$null
    try{$textWriter=New-Object IO.StreamWriter($file,(New-Object Text.UTF8Encoding $true));foreach($line in $lines){$textWriter.WriteLine($line)};$textWriter.Flush()}
    finally{if($textWriter){$textWriter.Dispose()}else{$file.Dispose()}}
    $touched=New-Object 'System.Collections.Generic.List[object]'
    try {
        foreach($change in $plan){
            $touched.Add($change)
            if(!(& $writer $change.Id $change.New)){throw ('Windows rejected the name change: '+$change.Id)}
            if(![string]::Equals([string](& $reader $change.Id),$change.New,[StringComparison]::Ordinal)){throw ('Name read-back did not match: '+$change.Id)}
        }
    }catch{
        $original=$_.Exception.Message;$rollbackErrors=@()
        for($i=$touched.Count-1;$i -ge 0;$i--){
            $change=$touched[$i]
            try{
                if(!(& $writer $change.Id $change.Old)){throw 'Rollback setter failed.'}
                if(![string]::Equals([string](& $reader $change.Id),$change.Old,[StringComparison]::Ordinal)){throw 'Rollback verification failed.'}
            }catch{$rollbackErrors+=($change.Id+': '+$_.Exception.Message)}
        }
        if($rollbackErrors.Count){throw ('ROLLBACK_FAILED: ممکن است بخشی از نام‌ها تغییر کرده باشند. پشتیبان: '+$backupPath+' | '+($rollbackErrors -join ' | '))}
        throw ('تغییر نام انجام نشد؛ نام‌های قبلی بازگردانده شدند. پشتیبان: '+$backupPath+' | '+$original)
    }
}
function Write-BootDescription([string]$id,[string]$name,[string]$file='') {
    $object=Bcd-EditableObject $id $file
    # This is the sole BCD write operation. Never set default/order/device/root/identifier.
    $result=$object.SetStringElement([uint32]0x12000004,$name)
    return [bool]$result.ReturnValue
}
function Read-BootDescription([string]$id,[string]$file='') {
    $element=Get-BcdElement (Bcd-EditableObject $id $file) ([uint32]0x12000004)
    if(!$element){throw 'Boot name could not be read.'}
    return [string]$element.String
}
function New-BootEditorWindow($entries) {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    $form=New-Object Windows.Forms.Form
    $form.Text='ویرایش نام‌های منوی بوت — Windows Identity Report'
    $form.ClientSize=New-Object Drawing.Size(980,450)
    $form.StartPosition='CenterScreen';$form.Font=New-Object Drawing.Font('Segoe UI',10)
    $label=New-Object Windows.Forms.Label;$label.Text='فقط نام جدید را ویرایش کنید. GUID، نام یوزر، بوت پیش‌فرض و ترتیب منو تغییر نمی‌کنند.'
    $label.SetBounds(15,12,950,45);$label.TextAlign='MiddleRight';$label.Anchor='Top,Left,Right';$form.Controls.Add($label)
    $grid=New-Object Windows.Forms.DataGridView;$grid.SetBounds(15,65,950,270);$grid.Anchor='Top,Bottom,Left,Right'
    $grid.AllowUserToAddRows=$false;$grid.AllowUserToDeleteRows=$false;$grid.RowHeadersVisible=$false
    $grid.AutoSizeColumnsMode='Fill';$grid.SelectionMode='CellSelect'
    foreach($spec in @(@('id','GUID',38,$true),@('old','نام فعلی',25,$true),@('new','نام جدید',37,$false))){
        $column=New-Object Windows.Forms.DataGridViewTextBoxColumn;$column.Name=$spec[0];$column.HeaderText=$spec[1]
        $column.FillWeight=$spec[2];$column.ReadOnly=$spec[3];$column.SortMode='NotSortable';$column.MaxInputLength=120
        [void]$grid.Columns.Add($column)
    }
    foreach($entry in $entries){[void]$grid.Rows.Add([object[]]@($entry.Id,$entry.Name,$entry.Name))}
    $form.Controls.Add($grid)
    $notice=New-Object Windows.Forms.Label;$notice.Text='پیش از اعمال تغییر، تأیید و محل ذخیرهٔ پشتیبان TXT نام‌های قبلی گرفته می‌شود.'
    $notice.SetBounds(15,345,950,38);$notice.Anchor='Bottom,Left,Right';$notice.TextAlign='MiddleRight';$form.Controls.Add($notice)
    $save=New-Object Windows.Forms.Button;$save.Text='تأیید و ذخیرهٔ نام‌ها';$save.SetBounds(730,395,235,40);$save.Anchor='Bottom,Right'
    $cancel=New-Object Windows.Forms.Button;$cancel.Text='انصراف';$cancel.SetBounds(565,395,150,40);$cancel.Anchor='Bottom,Right';$cancel.DialogResult='Cancel'
    $form.Controls.Add($save);$form.Controls.Add($cancel);$form.CancelButton=$cancel
    return [PSCustomObject]@{Form=$form;Grid=$grid;Save=$save}
}
function Show-BootNameEditor {
    $entries=@(Get-BootNameEntries)
    if(!$entries.Count){throw 'هیچ ورودی Windows Boot Loader قابل‌ویرایش پیدا نشد.'}
    $window=New-BootEditorWindow $entries;$form=$window.Form;$grid=$window.Grid;$save=$window.Save
    $editorState=@{Exit=22}
    $save.Add_Click({
        try{
            [void]$grid.EndEdit();$requested=@{}
            foreach($row in $grid.Rows){$requested[[string]$row.Cells['id'].Value]=[string]$row.Cells['new'].Value}
            $plan=@(New-BootRenamePlan $entries $requested)
            if(!$plan.Count){[void][Windows.Forms.MessageBox]::Show($form,'نامی تغییر نکرده است.','بدون تغییر');$editorState.Exit=23;$form.Close();return}
            $summary=($plan|ForEach-Object{$_.Old+' → '+$_.New+'  '+$_.Id}) -join "`r`n"
            $confirm=[Windows.Forms.MessageBox]::Show($form,("فقط نام این ورودی‌ها تغییر کند؟`r`n`r`n"+$summary),'تأیید تغییر نام','YesNo','Warning')
            if($confirm -ne 'Yes'){return}
            $dialog=New-Object Windows.Forms.SaveFileDialog
            $dialog.Filter='Text backup (*.txt)|*.txt';$dialog.FileName='Boot-Names-Backup-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.txt'
            $dialog.Title='فایل جدید برای پشتیبان نام‌های قبلی انتخاب کنید';$dialog.OverwritePrompt=$true
            try{if($dialog.ShowDialog($form) -ne 'OK'){return};$backup=$dialog.FileName}finally{$dialog.Dispose()}
            Invoke-BootRenamePlan $plan $backup {param($id)Read-BootDescription $id} {param($id,$name)Write-BootDescription $id $name}
            $editorState.Exit=0
            [void][Windows.Forms.MessageBox]::Show($form,("نام‌ها ذخیره و دوباره خوانده شدند.`r`nGUIDها و بوت پیش‌فرض تغییر نکرده‌اند.`r`nپشتیبان: "+$backup),'ذخیره شد','OK','Information')
            $form.Close()
        }catch{
            [void][Windows.Forms.MessageBox]::Show($form,$_.Exception.Message,'تغییر نام ناموفق','OK','Error')
            if($_.Exception.Message -like '*ROLLBACK_FAILED*'){$editorState.Exit=24;$save.Enabled=$false}
        }
    }.GetNewClosure())
    try{[void]$form.ShowDialog()}finally{$form.Dispose()}
    return $editorState.Exit
}

function Test-BootNameEditor {
    $ids=@('{11111111-1111-1111-1111-111111111111}','{22222222-2222-2222-2222-222222222222}','{33333333-3333-3333-3333-333333333333}')
    $entries=@([PSCustomObject]@{Id=$ids[0];Name='Old A'},[PSCustomObject]@{Id=$ids[1];Name='Old B'},[PSCustomObject]@{Id=$ids[2];Name='Old C'})
    $wanted=@{};for($i=0;$i -lt 3;$i++){$wanted[$ids[$i]]='شیفت '+$i}
    $plan=@(New-BootRenamePlan $entries $wanted);if($plan.Count -ne 3){throw 'Rename plan is incomplete.'}
    foreach($invalid in @('',"bad`nname",('x'*121))){$bad=@{};$bad[$ids[0]]=$invalid;$blocked=$false;try{New-BootRenamePlan $entries $bad|Out-Null}catch{$blocked=$true};if(!$blocked){throw 'Invalid name accepted.'}}
    $unknown=@{'{44444444-4444-4444-4444-444444444444}'='Unknown'};$blocked=$false
    try{New-BootRenamePlan $entries $unknown|Out-Null}catch{$blocked=$true};if(!$blocked){throw 'Unknown GUID accepted.'}
    $memory=@{};foreach($entry in $entries){$memory[$entry.Id]=$entry.Name};$state=@{Calls=0;FailSecond=$false}
    $read={param($id)$memory[$id]}.GetNewClosure()
    $write={param($id,$name)$state.Calls++;if($state.FailSecond -and $state.Calls -eq 2){return $false};$memory[$id]=$name;return $true}.GetNewClosure()
    $blocked=$false
    try{Invoke-BootRenamePlan $plan (Join-Path $PSScriptRoot 'missing-directory\backup.txt') $read $write}catch{$blocked=$true}
    if(!$blocked -or $state.Calls -ne 0){throw 'Writes occurred without a completed backup.'}
    $memory[$ids[0]]='Changed elsewhere';$blocked=$false
    try{Invoke-BootRenamePlan $plan (Join-Path $PSScriptRoot 'stale.txt') $read $write}catch{$blocked=$true}
    if(!$blocked -or $state.Calls -ne 0){throw 'Stale UI was allowed to overwrite current names.'}
    $memory[$ids[0]]=$entries[0].Name
    $backup=Join-Path $PSScriptRoot 'rename-success.txt'
    try{Invoke-BootRenamePlan $plan $backup $read $write;for($i=0;$i -lt 3;$i++){if($memory[$ids[$i]] -ne $wanted[$ids[$i]]){throw 'Fake rename mismatch.'}}}finally{Remove-Item $backup -Force -ErrorAction SilentlyContinue}
    foreach($entry in $entries){$memory[$entry.Id]=$entry.Name};$state.Calls=0;$state.FailSecond=$true
    $backup=Join-Path $PSScriptRoot 'rename-rollback.txt';$failed=$false
    try{try{Invoke-BootRenamePlan $plan $backup $read $write}catch{$failed=$true};if(!$failed){throw 'Writer failure was ignored.'};foreach($entry in $entries){if($memory[$entry.Id] -ne $entry.Name){throw 'Rollback failed.'}}}finally{Remove-Item $backup -Force -ErrorAction SilentlyContinue}
    # Real setters are exercised ONLY against a disposable BCD file, never the system store.
    $fixture=Join-Path $PSScriptRoot 'editor-test.bcd';$backup=Join-Path $PSScriptRoot 'editor-test-backup.txt'
    try{
        $class=[wmiclass]'root\WMI:BcdStore';$class.Scope.Options.EnablePrivileges=$true
        if(!$class.CreateStore($fixture).ReturnValue){throw 'Could not create isolated BCD fixture.'}
        $store=Bcd-StoreReference $fixture
        foreach($entry in $entries){if(!$store.CreateObject($entry.Id,[uint32]0x10200003).ReturnValue){throw 'Could not create test loader.'};if(!(Write-BootDescription $entry.Id $entry.Name $fixture)){throw 'Could not seed test description.'}}
        $managerId='{9dea862c-5cdd-4e70-acc1-f32b344d4795}'
        if(!$store.CreateObject($managerId,[uint32]0x10100002).ReturnValue){throw 'Could not create test manager.'}
        $manager=Bcd-EditableObject $managerId $fixture
        if(!$manager.SetObjectElement([uint32]0x23000003,$ids[0]).ReturnValue){throw 'Could not seed default fixture.'}
        if(!$manager.SetObjectListElement([uint32]0x24000001,[string[]]$ids).ReturnValue){throw 'Could not seed menu order fixture.'}
        $before=@(Get-BootNameEntries $fixture);if($before.Count -ne 3){throw 'Isolated loader enumeration failed.'}
        $realRead={param($id)Read-BootDescription $id $fixture}.GetNewClosure()
        $realWrite={param($id,$name)Write-BootDescription $id $name $fixture}.GetNewClosure()
        Invoke-BootRenamePlan $plan $backup $realRead $realWrite
        $after=@(Get-BootNameEntries $fixture)
        foreach($entry in $after){if($entry.Name -ne $wanted[$entry.Id]){throw 'Isolated BCD read-back mismatch.'}}
        if((Get-BcdElement $manager ([uint32]0x23000003)).Id -ne $ids[0]){throw 'Boot default changed during rename.'}
        if(((Get-BcdElement $manager ([uint32]0x24000001)).Ids -join ',') -ne ($ids -join ',')){throw 'Boot order changed during rename.'}
        if((($after.Id|Sort-Object) -join ',') -ne (($ids|Sort-Object) -join ',')){throw 'Boot GUID set changed during rename.'}

        $window=New-BootEditorWindow $after
        try{if($window.Grid.Rows.Count -ne 3 -or !$window.Grid.Columns['id'].ReadOnly -or !$window.Grid.Columns['old'].ReadOnly -or $window.Grid.Columns['new'].ReadOnly){throw 'Editor column safety contract failed.'}}finally{$window.Form.Dispose()}
    }finally{Remove-Item $fixture,$backup -Force -ErrorAction SilentlyContinue}
    Write-Output 'PASS: boot editor validation, mandatory backup, rollback, real isolated BCD Unicode rename and read-only GUID columns.'
}
