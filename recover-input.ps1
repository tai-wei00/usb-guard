#Requires -RunAsAdministrator
param(
    [switch]$Rollback,
    [string]$ResultPath = (Join-Path $env:LOCALAPPDATA 'USBGuard\maintenance\input-recovery-result.json')
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$programDir = Join-Path $root '程序文件'
$policy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions'
$classKey = Join-Path $policy 'DenyDeviceClasses'
$stateDir = Join-Path $env:LOCALAPPDATA 'USBGuard\maintenance'
$statePath = Join-Path $stateDir 'input-recovery-rollback.json'
$guardNames = @('USB哨兵看护', 'USB哨兵策略守护', 'USB哨兵状态提示', 'USB哨兵实时进程')
$legacyClasses = @('{36fc9e60-c465-11cf-8056-444553540000}', '{745a17a0-74d3-11d0-b6fe-00a0c90f57da}')

function Get-DenyValue {
    $key = Get-Item -LiteralPath $policy -ErrorAction SilentlyContinue
    if ($null -eq $key) { return [pscustomobject]@{ Exists = $false; Value = $null } }
    $value = $key.GetValue('DenyUnspecified', $null)
    return [pscustomobject]@{ Exists = ($null -ne $value); Value = $value }
}
function Get-TaskSnapshot {
    return @($guardNames | ForEach-Object {
        $task = Get-ScheduledTask -TaskName $_ -ErrorAction SilentlyContinue
        [pscustomobject]@{ Name = $_; Exists = ($null -ne $task); State = $(if ($null -ne $task) { [string]$task.State } else { '' }) }
    })
}
function Get-LegacyClassSnapshot {
    if (-not (Test-Path -LiteralPath $classKey)) { return @() }
    $key = Get-Item -LiteralPath $classKey
    return @($key.GetValueNames() | ForEach-Object {
        $value = [string]$key.GetValue($_, '')
        if ($legacyClasses -contains $value.ToLowerInvariant()) { [pscustomobject]@{ Name = $_; Value = $value } }
    })
}
function Get-AllowSnapshot {
    $path = Join-Path $policy 'AllowDeviceIDs'
    $exists = Test-Path -LiteralPath $path
    $values = @()
    if ($exists) {
        $key = Get-Item -LiteralPath $path
        $values = @($key.GetValueNames() | ForEach-Object { [pscustomobject]@{ Name = $_; Value = [string]$key.GetValue($_, '') } })
    }
    return [pscustomobject]@{ Exists = $exists; Values = $values }
}
function Stop-ProjectGuard {
    $psExe = Join-Path $env:windir 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $psExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $programDir 'GuardControl.ps1') -Target Stopped | Out-Null
    if ($LASTEXITCODE -notin @(0, 2)) { throw "停止 Guard 控制任务失败，exit=$LASTEXITCODE" }
    if (-not (Test-Path -LiteralPath (Join-Path $programDir 'usb-guard-disabled.flag'))) { throw 'Guard 停止标记未写入，已中止恢复' }
    $expected = [IO.Path]::GetFullPath((Join-Path $programDir 'UsbGuard.exe'))
    Get-CimInstance Win32_Process -Filter "Name='UsbGuard.exe'" -ErrorAction SilentlyContinue | Where-Object {
        $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -eq $expected
    } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop }
}
function Mount-TrustedStorage($Document) {
    $storageIds = Get-TrustedStorageIds $Document
    $ids = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($id in $storageIds) { $null = $ids.Add($id) }
    $assigned = New-Object System.Collections.Generic.List[object]
    $volumes = New-Object System.Collections.Generic.List[object]
    if ($ids.Count -eq 0) { return [pscustomobject]@{ Assigned = @(); Volumes = @() } }
    $drives = @(Get-CimInstance Win32_DiskDrive -ErrorAction Stop | Where-Object {
        $_.PNPDeviceID -and $ids.Contains(([string]$_.PNPDeviceID).Trim())
    })
    foreach ($drive in $drives) {
        if (-not $drive.SerialNumber) { continue }
        $serial = ([string]$drive.SerialNumber).Trim()
        $disk = @(Get-Disk -ErrorAction Stop | Where-Object { ([string]$_.SerialNumber).Trim() -eq $serial })[0]
        if ($null -eq $disk) { continue }
        $diskVolumeCount = 0
        $partitions = @(Get-Partition -DiskNumber $disk.Number -ErrorAction Stop | Where-Object {
            $_.Size -gt 0 -and $_.Type -notin @('Reserved', 'System', 'Recovery')
        })
        foreach ($item in $partitions) {
            $partition = $item
            if (-not $partition.DriveLetter) {
                Add-PartitionAccessPath -DiskNumber $disk.Number -PartitionNumber $partition.PartitionNumber -AssignDriveLetter -Confirm:$false -ErrorAction Stop
                Start-Sleep -Seconds 1
                $partition = Get-Partition -DiskNumber $disk.Number -PartitionNumber $partition.PartitionNumber -ErrorAction Stop
                if (-not $partition.DriveLetter) { throw "系统未能为已信任 USB 磁盘 $($disk.Number) 分区 $($partition.PartitionNumber) 分配盘符" }
                $assigned.Add([pscustomobject]@{ DiskNumber = [int]$disk.Number; PartitionNumber = [int]$partition.PartitionNumber; DriveLetter = [string]$partition.DriveLetter })
            }
            $volume = @($partition | Get-Volume -ErrorAction SilentlyContinue)[0]
            if ($null -ne $volume -and $volume.DriveLetter) {
                $diskVolumeCount++
                $volumes.Add([pscustomobject]@{
                    DiskNumber = [int]$disk.Number; PartitionNumber = [int]$partition.PartitionNumber
                    DriveLetter = [string]$volume.DriveLetter; FileSystem = [string]$volume.FileSystem
                    HealthStatus = [string]$volume.HealthStatus; OperationalStatus = [string]$volume.OperationalStatus
                })
            }
        }
        if ($diskVolumeCount -eq 0) { throw "已信任 USB 磁盘 $($disk.Number) 已识别，但仍无可用卷或盘符" }
    }
    return [pscustomobject]@{ Assigned = @($assigned.ToArray()); Volumes = @($volumes.ToArray()) }
}
function Write-Result([bool]$Success, [string]$Message, [object]$Extra = $null) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($ResultPath)) | Out-Null
    $result = [pscustomobject]@{ Success = $Success; Message = $Message; Timestamp = (Get-Date).ToString('o'); Extra = $Extra }
    [IO.File]::WriteAllText($ResultPath, (ConvertTo-Json -InputObject $result -Depth 10), (New-Object Text.UTF8Encoding($false)))
    Write-Output (ConvertTo-Json -InputObject $result -Depth 10)
}

if ($Rollback) {
    try {
        $state = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($statePath, [Text.Encoding]::UTF8)) -ErrorAction Stop
        Stop-ProjectGuard
        foreach ($item in @($state.AssignedPartitions)) {
            if ($null -eq $item) { continue }
            $partition = Get-Partition -DiskNumber ([int]$item.DiskNumber) -PartitionNumber ([int]$item.PartitionNumber) -ErrorAction SilentlyContinue
            if ($null -ne $partition -and $partition.DriveLetter -eq [string]$item.DriveLetter) {
                Remove-PartitionAccessPath -DiskNumber ([int]$item.DiskNumber) -PartitionNumber ([int]$item.PartitionNumber) -AccessPath ("$($item.DriveLetter):\") -Confirm:$false -ErrorAction Stop
            }
        }
        if ($state.DenyUnspecified.Exists) {
            New-ItemProperty -Path $policy -Name DenyUnspecified -Value ([int]$state.DenyUnspecified.Value) -PropertyType DWord -Force | Out-Null
        } else {
            Remove-ItemProperty -Path $policy -Name DenyUnspecified -ErrorAction SilentlyContinue
        }
        foreach ($item in @($state.LegacyClassDenies)) {
            if (-not (Test-Path -LiteralPath $classKey)) { New-Item -Path $classKey -Force | Out-Null }
            New-ItemProperty -Path $classKey -Name ([string]$item.Name) -Value ([string]$item.Value) -PropertyType String -Force | Out-Null
        }
        $allowPath = Join-Path $policy 'AllowDeviceIDs'
        if ($state.AllowDeviceIDs.Exists) {
            if (-not (Test-Path -LiteralPath $allowPath)) { New-Item -Path $allowPath -Force | Out-Null }
            $allowKey = Get-Item -LiteralPath $allowPath
            foreach ($name in $allowKey.GetValueNames()) { Remove-ItemProperty -Path $allowPath -Name $name -Force -ErrorAction Stop }
            foreach ($item in @($state.AllowDeviceIDs.Values)) {
                New-ItemProperty -Path $allowPath -Name ([string]$item.Name) -Value ([string]$item.Value) -PropertyType String -Force | Out-Null
            }
        } else { Remove-Item -LiteralPath $allowPath -Recurse -Force -ErrorAction SilentlyContinue }
        $enforcePath = Join-Path $programDir 'last_enforce.txt'
        if ($state.LastEnforce.Exists) { [IO.File]::WriteAllBytes($enforcePath, [Convert]::FromBase64String([string]$state.LastEnforce.Base64)) }
        else { Remove-Item -LiteralPath $enforcePath -Force -ErrorAction SilentlyContinue }
        foreach ($item in @($state.Tasks)) {
            if (-not $item.Exists) { continue }
            $task = Get-ScheduledTask -TaskName $item.Name -ErrorAction SilentlyContinue
            if ($null -eq $task) { continue }
            if ($item.State -eq 'Disabled') { Disable-ScheduledTask -InputObject $task -ErrorAction Stop | Out-Null }
            else { Enable-ScheduledTask -InputObject $task -ErrorAction Stop | Out-Null }
        }
        $flag = Join-Path $programDir 'usb-guard-disabled.flag'
        if ($state.StopFlagPresent) {
            if (-not (Test-Path -LiteralPath $flag)) { [IO.File]::WriteAllText($flag, (Get-Date).ToString('o'), (New-Object Text.UTF8Encoding($false))) }
        } else { Remove-Item -LiteralPath $flag -Force -ErrorAction SilentlyContinue }
        Write-Result $true '已恢复恢复前策略与既有 Guard 任务状态；未禁用任何输入设备。'
        exit 0
    } catch { Write-Result $false ('回滚失败：' + $_.Exception.Message); exit 1 }
}

try {
    [IO.Directory]::CreateDirectory($stateDir) | Out-Null
    $whitePath = Join-Path $programDir 'usb-whitelist.json'
    $configPath = Join-Path $programDir 'usb-guard-config.json'
    $keyboardBefore = @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.PNPClass -eq 'Keyboard' } | ForEach-Object {
        [pscustomobject]@{ DeviceID = $_.DeviceID; ErrorCode = [int]$_.ConfigManagerErrorCode }
    })
    $state = [pscustomobject]@{
        Timestamp = (Get-Date).ToString('o')
        DenyUnspecified = Get-DenyValue
        LegacyClassDenies = Get-LegacyClassSnapshot
        AllowDeviceIDs = Get-AllowSnapshot
        LastEnforce = [pscustomobject]@{
            Exists = (Test-Path -LiteralPath (Join-Path $programDir 'last_enforce.txt'))
            Base64 = $(if (Test-Path -LiteralPath (Join-Path $programDir 'last_enforce.txt')) { [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $programDir 'last_enforce.txt'))) } else { '' })
        }
        Tasks = Get-TaskSnapshot
        StopFlagPresent = (Test-Path -LiteralPath (Join-Path $programDir 'usb-guard-disabled.flag'))
        WhitelistSHA256 = (Get-FileHash -LiteralPath $whitePath -Algorithm SHA256).Hash
        ConfigSHA256 = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash
    }
    if (-not (Test-Path -LiteralPath $statePath)) {
        [IO.File]::WriteAllText($statePath, (ConvertTo-Json -InputObject $state -Depth 8), (New-Object Text.UTF8Encoding($false)))
    }

    Stop-ProjectGuard
    if (Test-Path -LiteralPath $classKey) {
        $key = Get-Item -LiteralPath $classKey
        foreach ($name in $key.GetValueNames()) {
            $value = [string]$key.GetValue($name, '')
            if ($legacyClasses -contains $value.ToLowerInvariant()) {
                Remove-ItemProperty -Path $classKey -Name $name -Force -ErrorAction Stop
            }
        }
    }

    $requestedResultPath = $ResultPath
    . (Join-Path $programDir '安装.ps1')
    # 依赖脚本默认参数会覆盖 Target/ProjectRoot/ResultPath；恢复救援与安装器自身变量。
    $target = $programDir
    $projectRoot = $root
    $ResultPath = $requestedResultPath
    $storagePhysicalMissing = $false
    $storageWaitSeconds = 0
    $recoveryDocument = Read-WhiteDocument (Get-WhitePath)
    $recoveryStorageIds = @(Get-TrustedStorageIds $recoveryDocument)
    if ($recoveryStorageIds.Count -gt 0) {
        $storageIdSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($id in $recoveryStorageIds) { $null = $storageIdSet.Add($id) }
        $trustedDrives = @(Get-CimInstance Win32_DiskDrive -ErrorAction Stop | Where-Object {
            $_.PNPDeviceID -and $storageIdSet.Contains(([string]$_.PNPDeviceID).Trim())
        })
        if ($trustedDrives.Count -eq 0) {
            $deadline = [DateTime]::UtcNow.AddSeconds(60)
            while ([DateTime]::UtcNow -lt $deadline -and $trustedDrives.Count -eq 0) {
                Start-Sleep -Seconds 2
                $storageWaitSeconds += 2
                $trustedDrives = @(Get-CimInstance Win32_DiskDrive -ErrorAction Stop | Where-Object {
                    $_.PNPDeviceID -and $storageIdSet.Contains(([string]$_.PNPDeviceID).Trim())
                })
            }
            $storagePhysicalMissing = ($trustedDrives.Count -eq 0)
        }
    }
    Invoke-Enforce
    if ((Get-FileHash -LiteralPath $whitePath -Algorithm SHA256).Hash -ne $state.WhitelistSHA256) { throw '恢复过程中信任名单发生变化，已中止' }
    if ((Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash -ne $state.ConfigSHA256) { throw '恢复过程中 Guard 配置发生变化，已中止' }

    Start-Sleep -Seconds 2
    $verifiedDocument = Read-WhiteDocument (Get-WhitePath)
    $pendingInput = @(Get-UnconfiguredTrustedInput (Get-TrustedInputIds $verifiedDocument))
    if ($pendingInput.Count -gt 0) {
        $pendingNames = @($pendingInput | ForEach-Object { $_.Name + ' [' + $_.DeviceID + ']' })
        throw "已信任输入设备仍未配置：$($pendingNames -join '；')"
    }
    $pendingStorage = @(Get-UnconfiguredTrustedStorage (Get-TrustedStorageIds $verifiedDocument))
    if ($pendingStorage.Count -gt 0) {
        $pendingNames = @($pendingStorage | ForEach-Object { $_.Name + ' [' + $_.DeviceID + ']' })
        throw "已信任 USB 存储卷仍未配置：$($pendingNames -join '；')"
    }
    $storageResult = Mount-TrustedStorage $verifiedDocument
    if (@($storageResult.Assigned).Count -gt 0) {
        $rollbackDocument = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($statePath, [Text.Encoding]::UTF8)) -ErrorAction Stop
        $assignedBefore = @($rollbackDocument.AssignedPartitions | Where-Object { $null -ne $_ })
        $rollbackDocument | Add-Member -NotePropertyName AssignedPartitions -NotePropertyValue (@($assignedBefore) + @($storageResult.Assigned)) -Force
        [IO.File]::WriteAllText($statePath, (ConvertTo-Json -InputObject $rollbackDocument -Depth 10), (New-Object Text.UTF8Encoding($false)))
    }
    $mouse = @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.PNPClass -eq 'Mouse' -or $_.Service -eq 'mouhid' } | ForEach-Object {
        [pscustomobject]@{ Name = $_.Name; DeviceID = $_.DeviceID; Status = $_.Status; Service = $_.Service; ErrorCode = [int]$_.ConfigManagerErrorCode }
    })
    $badMouse = @($mouse | Where-Object { $_.ErrorCode -ne 0 })
    if ($mouse.Count -eq 0 -or $badMouse.Count -gt 0) { throw "鼠标设备尚未全部恢复：$($badMouse.Count) 个异常" }
    $keyboardAfter = @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.PNPClass -eq 'Keyboard' })
    foreach ($before in $keyboardBefore) {
        if ($before.ErrorCode -ne 0) { continue }
        $current = $keyboardAfter | Where-Object { $_.DeviceID -eq $before.DeviceID }
        if ($null -eq $current -or [int]$current.ConfigManagerErrorCode -ne 0) { throw '键盘状态发生退化，已报告失败' }
    }
    $denyAfter = Get-DenyValue
    if (-not $denyAfter.Exists -or [int]$denyAfter.Value -ne 1) { throw '恢复后 DenyUnspecified 未回到安全值 1' }

    $extra = [pscustomobject]@{
        Mouse = $mouse
        Storage = $storageResult
        StoragePhysicalMissing = $storagePhysicalMissing
        StorageWaitSeconds = $storageWaitSeconds
        KeyboardCount = $keyboardAfter.Count
        DenyUnspecified = [int]$denyAfter.Value
        WhitelistUnchanged = $true
        ConfigUnchanged = $true
        GuardLeftStopped = $true
        RollbackCommand = 'recover-input.ps1 -Rollback'
    }
    $message = if ($storagePhysicalMissing) {
        '鼠标与防复发策略已恢复；名单中的 U 盘当前物理不可见，未宣称 U 盘已恢复。Guard 保持停止，名单与配置未改。'
    } else {
        '鼠标、必要输入设备与已连接的信任 USB 存储已恢复；Guard 保持停止，名单与配置未改。'
    }
    Write-Result $true $message $extra
    exit 0
} catch {
    Write-Result $false ('输入设备恢复失败：' + $_.Exception.Message)
    exit 1
}