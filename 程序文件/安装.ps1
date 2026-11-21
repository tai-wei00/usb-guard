<#
 USB 哨兵 v2.1.0 · 安装、显式添加设备与策略维护
 -DryRun 仅预览；-AddDevice 逐项确认；-EnforcePolicy 策略校验；-Uninstall 撤销加固
 升级不自动信任任何设备；首次安装未明确确认信任设备时中止加固。
#>
param(
    [switch]$DryRun,
    [switch]$AddDevice,
    [switch]$EnforcePolicy,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$srcDir = $PSScriptRoot
$target = $srcDir
$projectRoot = [IO.Path]::GetFullPath((Join-Path $srcDir '..'))
$policy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions'
$allowKey = "$policy\AllowDeviceIDs"
$removable = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}'
$tWatch = 'USB哨兵看护'
$tPolicy = 'USB哨兵策略守护'
. (Join-Path $srcDir 'GuardControl.ps1')
. (Join-Path $srcDir '源码\发布检查.ps1')
# 依赖脚本的默认参数 Target/ProjectRoot 会覆盖同名变量；点源后恢复安装器路径。
$target = $srcDir
$projectRoot = [IO.Path]::GetFullPath((Join-Path $srcDir '..'))

function Step($m) { Write-Host "[*] $m" -ForegroundColor Cyan }
function Ok($m) { Write-Host "[OK] $m" -ForegroundColor Green }
function Warn($m) { Write-Host "[!] $m" -ForegroundColor Yellow }
function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Get-WhitePath { return (Join-Path $target 'usb-whitelist.json') }
function Test-ManagedId([string]$Id) { return $Id -match '^(USB|USBSTOR|HID)\\' }
function Get-Revision([string]$Text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $hash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes([string]$Text)))).Replace('-', '')
    $sha.Dispose(); return $hash
}
function Write-Event([string]$Message, [Diagnostics.EventLogEntryType]$Kind) {
    try {
        if (-not [Diagnostics.EventLog]::SourceExists('UsbGuard')) { [Diagnostics.EventLog]::CreateEventSource('UsbGuard', 'Application') }
        [Diagnostics.EventLog]::WriteEntry('UsbGuard', $Message, $Kind)
    } catch { }
}

function Save-WhiteSelection {
    param([string]$Path, [string[]]$SelectedIds, [string]$ExpectedRevision, [bool]$PreserveExisting = $true)
    $document = Read-WhiteDocument $Path
    if (-not $document.Success) { throw $document.Message }
    $actualRevision = Get-Revision $document.RawText
    if ($ExpectedRevision -and $actualRevision -ne $ExpectedRevision) { throw '名单已在确认期间发生变化，请刷新后重试；未覆盖较新内容' }
    $entries = New-Object System.Collections.Generic.List[object]
    if ($PreserveExisting) { foreach ($entry in $document.Entries) { $entries.Add($entry) } }
    $existing = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    if ($PreserveExisting) { foreach ($id in $document.ExactIds) { $null = $existing.Add($id) } }
    $now = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
    foreach ($raw in $SelectedIds) {
        $id = ([string]$raw).Trim()
        if (-not (Test-ExactInstanceId $id)) { throw "拒绝非完整实例身份：$id" }
        if ($existing.Contains($id)) { continue }
        $category = if ($id -match '^USBSTOR\\') { 'storage' } elseif ($id -match '^HID\\') { 'hid' } else { 'other' }
        $entries.Add([pscustomobject][ordered]@{ deviceIds = @($id); description = '用户明确确认的设备'; category = $category; trustedAt = $now })
        $null = $existing.Add($id)
    }
    $json = ConvertTo-Json -InputObject @($entries.ToArray()) -Depth 10
    $tmp = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    $backup = "$Path.last-valid.bak"
    try {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
        [IO.File]::WriteAllText($tmp, $json, (New-Object Text.UTF8Encoding($false)))
        if ([IO.File]::ReadAllText($tmp, [Text.Encoding]::UTF8) -ne $json) { throw '临时名单回读不一致' }
        if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($tmp, $Path, $backup, $true) }
        else { [IO.File]::Move($tmp, $Path) }
        if ([IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8) -ne $json) { throw '名单替换后重读不一致' }
        $verify = Read-WhiteDocument $Path
        if (-not $verify.Success) { throw '名单保存后解析失败' }
        return (Get-Revision $json)
    } catch {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
        throw "名单保存失败，原有效文件保留：$($_.Exception.Message)"
    }
}

function Get-CandidateUsbIds {
    $ids = New-Object System.Collections.Generic.List[string]
    Get-CimInstance Win32_PnPEntity -ErrorAction Stop | ForEach-Object {
        $id = ([string]$_.DeviceID).Trim()
        if (Test-ExactInstanceId $id) { $ids.Add($id) }
    }
    return @($ids.ToArray() | Sort-Object -Unique)
}

function Select-ExplicitDevices {
    param([string[]]$Candidates, [string[]]$AlreadyTrusted, [switch]$RequireOne)
    $selected = New-Object System.Collections.Generic.List[string]
    $trusted = @($AlreadyTrusted)
    foreach ($id in $Candidates) {
        if ($trusted -contains $id) { continue }
        Write-Host ''
        Write-Host '请核对以下完整实例（不会自动包含同型号或无关接口）：' -ForegroundColor Yellow
        Write-Host "  $id"
        $answer = Read-Host '只信任这个完整实例吗？输入 Y 确认，其他回答跳过'
        if ($answer -eq 'Y' -or $answer -eq 'y') { $selected.Add($id) }
    }
    if ($RequireOne -and $selected.Count -eq 0) { throw '未明确确认任何信任设备，安装未完成；未启用系统加固或看护。' }
    return $selected.ToArray()
}

function Get-AllowValues {
    if (-not (Test-Path $allowKey)) { return @() }
    $values = @()
    foreach ($name in (Get-Item $allowKey).GetValueNames()) { $values += [string](Get-ItemPropertyValue -Path $allowKey -Name $name) }
    return @($values)
}

function Get-TrustedInputIds($Document) {
    $ids = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($Document.Entries)) {
        if ($null -eq $entry -or $entry -is [string] -or $null -eq $entry.PSObject.Properties['deviceIds']) { continue }
        $entryIds = @($entry.deviceIds | ForEach-Object { ([string]$_).Trim() } | Where-Object { Test-ExactInstanceId $_ })
        $isInput = ([string]$entry.category -eq 'hid') -or (@($entryIds | Where-Object { $_ -match '^HID\\' }).Count -gt 0)
        if ($isInput) { foreach ($id in $entryIds) { $null = $ids.Add($id) } }
    }
    return @($ids | ForEach-Object { $_ })
}

function Get-UnconfiguredTrustedInput([string[]]$TrustedInputIds) {
    $ids = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($id in $TrustedInputIds) { $null = $ids.Add($id) }
    if ($ids.Count -eq 0) { return @() }
    return @(Get-CimInstance Win32_PnPEntity -ErrorAction Stop | Where-Object {
        $_.DeviceID -and $ids.Contains(([string]$_.DeviceID).Trim()) -and [int]$_.ConfigManagerErrorCode -ne 0
    })
}

function Get-TrustedStorageIds($Document) {
    $ids = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($Document.Entries)) {
        if ($null -eq $entry -or $entry -is [string] -or $null -eq $entry.PSObject.Properties['deviceIds']) { continue }
        $entryIds = @($entry.deviceIds | ForEach-Object { ([string]$_).Trim() } | Where-Object { Test-ExactInstanceId $_ })
        $isStorage = ([string]$entry.category -eq 'storage') -or (@($entryIds | Where-Object { $_ -match '^USBSTOR\\' }).Count -gt 0)
        if ($isStorage) { foreach ($id in $entryIds) { $null = $ids.Add($id) } }
    }
    return @($ids | ForEach-Object { $_ })
}

function Get-UnconfiguredTrustedStorage([string[]]$TrustedStorageIds) {
    $ids = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($id in $TrustedStorageIds) { $null = $ids.Add($id) }
    if ($ids.Count -eq 0) { return @() }
    $entities = @(Get-CimInstance Win32_PnPEntity -ErrorAction Stop)
    $serials = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($drive in @(Get-CimInstance Win32_DiskDrive -ErrorAction Stop)) {
        if ($drive.PNPDeviceID -and $ids.Contains(([string]$drive.PNPDeviceID).Trim()) -and $drive.SerialNumber) {
            $null = $serials.Add(([string]$drive.SerialNumber).Trim())
        }
    }
    $pending = New-Object System.Collections.Generic.List[object]
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entity in $entities) {
        $id = ([string]$entity.DeviceID).Trim()
        if (-not $id -or [int]$entity.ConfigManagerErrorCode -eq 0) { continue }
        $matched = $ids.Contains($id)
        if (-not $matched -and $id.StartsWith('STORAGE\VOLUME', [StringComparison]::OrdinalIgnoreCase)) {
            foreach ($serial in $serials) {
                if ($serial -and $id.IndexOf($serial, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $matched = $true; break }
            }
        }
        if ($matched -and $seen.Add($id)) { $pending.Add($entity) }
    }
    return @($pending.ToArray())
}

function Sync-AllowList([switch]$WhatIf) {
    $document = Read-WhiteDocument (Get-WhitePath)
    if (-not $document.Success) { throw "名单不可读，已中止策略写回：$($document.Message)" }
    $white = @($document.ExactIds | Where-Object { Test-ManagedId $_ })
    $trustedInputIds = Get-TrustedInputIds $document
    $trustedStorageIds = Get-TrustedStorageIds $document
    $keep = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($id in (Get-AllowValues)) { if (-not (Test-ManagedId $id)) { $null = $keep.Add($id) } }
    Get-CimInstance Win32_PnPEntity -ErrorAction Stop | ForEach-Object {
        $id = ([string]$_.DeviceID).Trim()
        if ($id -and -not (Test-ManagedId $id)) { $null = $keep.Add($id.ToUpperInvariant()) }
    }
    foreach ($id in $white) { $null = $keep.Add($id) }
    Step "允许列表：非受管设备安全兜底 + $($white.Count) 条精确 USB/HID 信任身份；删除的设备不再保留"
    if ($WhatIf) { return }
    if (-not (Test-Path $policy)) { New-Item -Path $policy -Force | Out-Null }
    if (-not (Test-Path $allowKey)) { New-Item -Path $allowKey -Force | Out-Null }
    $occupied = @{}
    $max = 0
    foreach ($name in (Get-Item $allowKey).GetValueNames()) {
        $occupied[$name] = $true
        $number = 0
        if ([int]::TryParse($name, [ref]$number) -and $number -gt $max) { $max = $number }
    }
    foreach ($value in $keep) {
        if ((Get-AllowValues) -contains $value) { continue }
        $next = $max + 1
        while ($occupied.ContainsKey([string]$next)) { $next++ }
        New-ItemProperty -Path $allowKey -Name ([string]$next) -Value $value -PropertyType String -ErrorAction Stop | Out-Null
        $occupied[[string]$next] = $true; $max = $next
    }
    foreach ($name in (Get-Item $allowKey).GetValueNames()) {
        $current = [string](Get-ItemPropertyValue -Path $allowKey -Name $name)
        if (-not $keep.Contains($current)) { Remove-ItemProperty -Path $allowKey -Name $name -Force -ErrorAction Stop }
    }
    $pendingInput = @(Get-UnconfiguredTrustedInput $trustedInputIds)
    $pendingStorage = @(Get-UnconfiguredTrustedStorage $trustedStorageIds)
    $pendingDevices = @($pendingInput) + @($pendingStorage)
    if ($pendingDevices.Count -gt 0) {
        Warn "检测到 $($pendingDevices.Count) 个已信任输入/存储设备尚未完成驱动安装；临时解除全局拒绝并扫描，绝不新增信任身份"
        New-ItemProperty -Path $policy -Name DenyUnspecified -Value 0 -PropertyType DWord -Force | Out-Null
        & pnputil /scan-devices | Out-Null
        Start-Sleep -Seconds 4
        $remainingInput = @(Get-UnconfiguredTrustedInput $trustedInputIds)
        $remainingStorage = @(Get-UnconfiguredTrustedStorage $trustedStorageIds)
        $remaining = @($remainingInput) + @($remainingStorage)
        if ($remaining.Count -gt 0) {
            $names = @($remaining | ForEach-Object { $_.Name + ' [' + $_.DeviceID + ']' })
            throw "输入/存储设备驱动仍未恢复：$($names -join '；')。为避免再次失效，DenyUnspecified 保持 0；请检查设备连接后重试。"
        }
        Step '已信任输入/存储设备驱动恢复完成，重新启用全局安装限制'
    }
    New-ItemProperty -Path $policy -Name DenyUnspecified -Value 1 -PropertyType DWord -Force | Out-Null
    try { Set-Content -LiteralPath (Join-Path $target 'last_enforce.txt') -Value ((Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')) -Encoding UTF8 } catch { }
}

function Set-ProtectedAcl([string]$Directory) {
    $acl = Get-Acl -LiteralPath $Directory
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $type = [Security.AccessControl.AccessControlType]::Allow
    foreach ($rule in @(@('BUILTIN\Administrators', 'FullControl'), @('NT AUTHORITY\SYSTEM', 'FullControl'), @('BUILTIN\Users', 'ReadAndExecute'))) {
        $acl.SetAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($rule[0], $rule[1], $inherit, $propagation, $type)))
    }
    Set-Acl -LiteralPath $Directory -AclObject $acl
}

function Stop-CurrentGuard {
    $expected = [IO.Path]::GetFullPath((Join-Path $target 'UsbGuard.exe'))
    Get-CimInstance Win32_Process -Filter "Name='UsbGuard.exe'" -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -eq $expected) { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    }
}

function Find-UpgradePackage {
    $rootExe = Join-Path $projectRoot 'UsbGuard.next.exe'
    $localExe = Join-Path $srcDir 'UsbGuard.next.exe'
    if (Test-Path -LiteralPath $rootExe) {
        return [pscustomobject]@{ Exe = $rootExe; Manifest = (Join-Path $projectRoot 'build-manifest.json') }
    }
    if (Test-Path -LiteralPath $localExe) {
        $rootManifest = Join-Path $projectRoot 'build-manifest.json'
        $manifest = if (Test-Path -LiteralPath $rootManifest) { $rootManifest } else { Join-Path $srcDir 'build-manifest.json' }
        return [pscustomobject]@{ Exe = $localExe; Manifest = $manifest }
    }
    return $null
}

function Write-Integrity {
    $hashes = [ordered]@{}
    foreach ($name in @('UsbGuard.exe', 'watchdog.vbs', '安装.ps1', '状态提示.ps1', '状态提示.vbs', 'GuardControl.ps1', 'build-manifest.json')) {
        $path = Join-Path $target $name
        if (Test-Path -LiteralPath $path) { $hashes[$name] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    }
    [IO.File]::WriteAllText((Join-Path $target 'integrity.json'), (ConvertTo-Json -InputObject $hashes), (New-Object Text.UTF8Encoding($false)))
}

function Register-GuardTasks {
    $failures = New-Object System.Collections.Generic.List[string]
    $me = "$env:USERDOMAIN\$env:USERNAME"
    $wsh = Join-Path $env:windir 'System32\wscript.exe'
    $psExe = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $definitions = @(
        @{ Name = $tWatch; Action = (New-ScheduledTaskAction -Execute $wsh -Argument ('//nologo "' + (Join-Path $target 'watchdog.vbs') + '"')); Principal = (New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited); Trigger = @((New-ScheduledTaskTrigger -AtLogOn -User $me), (New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5))) },
        @{ Name = $tPolicy; Action = (New-ScheduledTaskAction -Execute $psExe -Argument ('-NoProfile -WindowStyle Hidden -File "' + (Join-Path $target '安装.ps1') + '" -EnforcePolicy')); Principal = (New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest); Trigger = (New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(2) -RepetitionInterval (New-TimeSpan -Minutes 10)) },
        @{ Name = 'USB哨兵状态提示'; Action = (New-ScheduledTaskAction -Execute $wsh -Argument ('//nologo "' + (Join-Path $target '状态提示.vbs') + '"')); Principal = (New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited); Trigger = @((New-ScheduledTaskTrigger -AtLogOn -User $me), (New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5))) },
        @{ Name = 'USB哨兵实时进程'; Action = (New-ScheduledTaskAction -Execute (Join-Path $target 'UsbGuard.exe')); Principal = (New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited); Trigger = (New-ScheduledTaskTrigger -AtLogOn -User $me) }
    )
    foreach ($definition in $definitions) {
        try {
            Register-ScheduledTask -TaskName $definition.Name -Action $definition.Action -Principal $definition.Principal -Trigger $definition.Trigger -Force -ErrorAction Stop | Out-Null
            $task = Get-ScheduledTask -TaskName $definition.Name -ErrorAction Stop
            if ($task.State -eq 'Disabled') { throw '任务注册后为 Disabled' }
            Ok "任务已注册并校验：$($definition.Name)"
        } catch { $failures.Add("$($definition.Name)：$($_.Exception.Message)") }
    }
    return $failures
}

function Invoke-Install {
    Write-Host 'USB 哨兵 v2.1.0：保留有效名单；升级不扩信；首次安装必须逐项明确确认。' -ForegroundColor Cyan
    if ($DryRun) { Warn '预览结束，没有修改文件、任务或系统策略'; return }
    if (-not (Test-Admin)) { throw '需要管理员权限，请在 UAC 中选择“是”。' }
    $exeTarget = Join-Path $target 'UsbGuard.exe'
    $isUpgrade = Test-Path -LiteralPath $exeTarget
    $package = Find-UpgradePackage
    if ($null -ne $package) {
        $errors = Test-BuildPackage -Root $projectRoot -ExePath $package.Exe -ManifestPath $package.Manifest
        if ($errors.Count) { throw "候选包校验失败，旧 EXE 未替换：`n$($errors -join "`n")" }
        Step '候选 EXE、版本、源文件与说明哈希核验通过，准备替换'
        foreach ($name in $script:GuardTaskNames) { Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue }
        Stop-CurrentGuard
        Start-Sleep -Seconds 1
        if (Test-Path -LiteralPath $exeTarget) { [IO.File]::Replace($package.Exe, $exeTarget, (Join-Path $target 'UsbGuard.pre-v2.1.bak'), $true) }
        else { [IO.File]::Move($package.Exe, $exeTarget) }
        [IO.File]::Copy($package.Manifest, (Join-Path $target 'build-manifest.json'), $true)
        Ok '候选程序已替换；旧版已备份'
    }

    $whitePath = Get-WhitePath
    $document = Read-WhiteDocument $whitePath
    if (-not $document.Success) { throw "安装中止：$($document.Message)" }
    if (-not $isUpgrade -or $document.ExactIds.Count -eq 0) {
        Step '首次配置或当前无有效精确身份：必须逐项确认，取消将中止加固'
        $selected = Select-ExplicitDevices -Candidates (Get-CandidateUsbIds) -AlreadyTrusted @() -RequireOne
        $revision = Save-WhiteSelection -Path $whitePath -SelectedIds $selected -ExpectedRevision (Get-Revision $document.RawText) -PreserveExisting $isUpgrade
        Ok "明确确认的设备已保存（修订 $revision）"
    } else { Step '升级：保留已有有效条目和未识别历史条目，不新增信任' }

    Set-ProtectedAcl $target
    Sync-AllowList
    New-ItemProperty -Path $removable -Name Deny_Execute -Value 1 -PropertyType DWord -Force | Out-Null
    $failures = Register-GuardTasks
    Write-Integrity
    if (-not (Test-Path -LiteralPath $script:GuardStopFlag)) { Start-ScheduledTask -TaskName $tWatch -ErrorAction SilentlyContinue }
    if ($failures.Count) {
        Write-Event "USB 哨兵安装部分完成：$($failures -join '；')" ([Diagnostics.EventLogEntryType]::Warning)
        throw "安装部分完成，以下任务未校验成功：`n$($failures -join "`n")`n请修复后重试；不会显示全部成功。"
    }
    Write-Event 'USB 哨兵 v2.1.0 安装完成' ([Diagnostics.EventLogEntryType]::Information)
    Ok '安装完成。打开程序与设置可查看真实状态、设备、扫描和版本信息。'
}

function Invoke-AddDevice {
    if (-not (Test-Admin)) { throw '需要管理员权限。' }
    $path = Get-WhitePath
    $document = Read-WhiteDocument $path
    if (-not $document.Success) { throw $document.Message }
    $before = Get-Revision $document.RawText
    $selected = Select-ExplicitDevices -Candidates (Get-CandidateUsbIds) -AlreadyTrusted $document.ExactIds
    if ($selected.Count -eq 0) { Ok '未确认新设备，名单未变化'; return }
    if ($DryRun) { Warn '预览结束，未保存'; return }
    Save-WhiteSelection -Path $path -SelectedIds $selected -ExpectedRevision $before -PreserveExisting $true | Out-Null
    Sync-AllowList
    Ok "已保存 $($selected.Count) 个明确确认的完整实例；其余设备未信任。"
}

function Invoke-Enforce {
    $document = Read-WhiteDocument (Get-WhitePath)
    if (-not $document.Success) { throw "策略守护已中止：$($document.Message)" }
    if ($DryRun) { Step '名单读取成功；预览不修改策略'; return }
    Sync-AllowList
    $integrityPath = Join-Path $target 'integrity.json'
    if (Test-Path -LiteralPath $integrityPath) {
        try {
            $reference = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($integrityPath, [Text.Encoding]::UTF8))
            foreach ($name in @('UsbGuard.exe', 'watchdog.vbs', '安装.ps1', '状态提示.ps1', '状态提示.vbs', 'GuardControl.ps1', 'build-manifest.json')) {
                $path = Join-Path $target $name
                if ((Test-Path -LiteralPath $path) -and $reference.$name -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $reference.$name) {
                    Write-Event "程序文件被改动：$name" ([Diagnostics.EventLogEntryType]::Warning)
                }
            }
        } catch { Write-Event "指纹核验失败：$($_.Exception.Message)" ([Diagnostics.EventLogEntryType]::Warning) }
    }
}

function Invoke-Uninstall {
    if ($DryRun) { Step '预览：删除四个任务并撤销系统限制，保留名单、配置和程序文件'; return }
    if (-not (Test-Admin)) { throw '需要管理员权限。' }
    $failures = New-Object System.Collections.Generic.List[string]
    foreach ($name in $script:GuardTaskNames) {
        try { Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction Stop } catch { if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) { $failures.Add($name) } }
    }
    Stop-CurrentGuard
    Remove-ItemProperty -Path $policy -Name DenyUnspecified -ErrorAction SilentlyContinue
    Remove-Item -Path $allowKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $removable -Name Deny_Execute -ErrorAction SilentlyContinue
    if ($failures.Count) { throw '部分任务未删除：' + ($failures -join '、') }
    Ok '已撤销加固；名单、配置和程序文件均保留。'
}

if ($MyInvocation.InvocationName -ne '.') {
    Write-Host ''
    Write-Host '=== USB 哨兵 v2.1.0 ===' -ForegroundColor Cyan
    if ($AddDevice) { Invoke-AddDevice }
    elseif ($Uninstall) { Invoke-Uninstall }
    elseif ($EnforcePolicy) { Invoke-Enforce }
    else { Invoke-Install }
}