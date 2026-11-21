param(
    [ValidateSet('Get', 'Stopped', 'Enabled')]
    [string]$Target = 'Get',
    [string]$ResultPath = ''
)

$ErrorActionPreference = 'Stop'
$script:GuardTaskNames = @('USB哨兵看护', 'USB哨兵策略守护', 'USB哨兵状态提示', 'USB哨兵实时进程')
$script:GuardStopFlag = Join-Path $PSScriptRoot 'usb-guard-disabled.flag'

function Test-ExactInstanceId([string]$Id) {
    if ([string]::IsNullOrWhiteSpace($Id)) { return $false }
    $value = $Id.Trim()
    if ($value -notmatch '^(USB|USBSTOR|HID)\\[^\\]+\\[^\\]+') { return $false }
    if ($value -match '[*?]' -or $value -match '^USB\\ROOT_HUB') { return $false }
    return $true
}

function Read-WhiteDocument([string]$Path) {
    $result = [ordered]@{ Success = $false; Exists = $false; Entries = @(); ExactIds = @(); RawText = ''; Message = '' }
    try {
        if (-not (Test-Path -LiteralPath $Path)) {
            $result.Success = $true
            $result.Message = '名单不存在，按空名单处理'
            return [pscustomobject]$result
        }
        $result.Exists = $true
        $text = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
        $result.RawText = $text
        if ([string]::IsNullOrWhiteSpace($text)) {
            $result.Success = $true
            $result.Message = '名单为空'
            return [pscustomobject]$result
        }
        $json = ConvertFrom-Json -InputObject $text -ErrorAction Stop
        $entries = @($json)
        $exact = New-Object System.Collections.Generic.List[string]
        foreach ($entry in $entries) {
            if ($entry -is [string]) {
                if (Test-ExactInstanceId $entry) { $exact.Add($entry.Trim()) }
                continue
            }
            if ($null -ne $entry -and $null -ne $entry.PSObject.Properties['deviceIds']) {
                foreach ($id in @($entry.deviceIds)) {
                    if (Test-ExactInstanceId ([string]$id)) { $exact.Add(([string]$id).Trim()) }
                }
            }
        }
        $result.Success = $true
        $result.Entries = $entries
        $result.ExactIds = @($exact | Sort-Object -Unique)
        $result.Message = '名单读取成功'
        return [pscustomobject]$result
    } catch {
        $result.Message = '名单读取失败：' + $_.Exception.Message
        return [pscustomobject]$result
    }
}

function Get-GuardControlState {
    $tasks = New-Object System.Collections.Generic.List[object]
    $failed = New-Object System.Collections.Generic.List[object]
    foreach ($name in $script:GuardTaskNames) {
        try {
            $task = Get-ScheduledTask -TaskName $name -ErrorAction Stop
            $tasks.Add([pscustomobject]@{ Name = $name; ExpectedState = 'Readable'; ActualState = [string]$task.State; ErrorCode = ''; Message = '已读取'; Success = $true })
        } catch {
            $item = [pscustomobject]@{ Name = $name; ExpectedState = 'Readable'; ActualState = 'MissingOrUnreadable'; ErrorCode = $_.Exception.HResult.ToString('X8'); Message = $_.Exception.Message; Success = $false }
            $tasks.Add($item); $failed.Add($item)
        }
    }
    $stopped = Test-Path -LiteralPath $script:GuardStopFlag
    $kind = if ($failed.Count -gt 0) { 'Partial' } else { 'Success' }
    return [pscustomobject]@{
        Action = 'Enable'; ActualMode = $(if ($stopped) { 'Stopped' } else { 'Protecting' }); Result = $kind
        Message = $(if ($failed.Count -gt 0) { '部分看护任务缺失或不可读，请运行安装修复' } else { '看护状态已读取' })
        StopFlagPresent = $stopped; FailedItems = @($failed.ToArray()); Tasks = @($tasks.ToArray())
    }
}

function Set-GuardControlState {
    param([ValidateSet('Stopped', 'Enabled')][string]$Target, [object]$OperationRequest = $null)
    $failed = New-Object System.Collections.Generic.List[object]
    $tasks = New-Object System.Collections.Generic.List[object]
    $completed = 0
    if ($Target -eq 'Stopped') {
        if (Test-Path -LiteralPath $script:GuardStopFlag) {
            $completed++
        } else {
            try {
                $content = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
                $tmp = "$script:GuardStopFlag.$([guid]::NewGuid().ToString('N')).tmp"
                [IO.File]::WriteAllText($tmp, $content, (New-Object Text.UTF8Encoding($false)))
                [IO.File]::Move($tmp, $script:GuardStopFlag)
                $completed++
            } catch {
                $failed.Add([pscustomobject]@{ Name = 'usb-guard-disabled.flag'; ExpectedState = 'Present'; ActualState = 'WriteFailed'; ErrorCode = $_.Exception.HResult.ToString('X8'); Message = $_.Exception.Message; Success = $false })
                return [pscustomobject]@{ Action = 'Stop'; ActualMode = 'Unknown'; Result = 'Failed'; Message = '停止标记写入失败，未禁用任务'; StopFlagPresent = (Test-Path -LiteralPath $script:GuardStopFlag); FailedItems = @($failed.ToArray()); Tasks = @() }
            }
        }
    }
    foreach ($name in $script:GuardTaskNames) {
        $expected = if ($Target -eq 'Stopped') { 'Disabled' } else { 'Enabled' }
        try {
            $existing = Get-ScheduledTask -TaskName $name -ErrorAction Stop
            if ($Target -eq 'Stopped') { Disable-ScheduledTask -InputObject $existing -ErrorAction Stop | Out-Null }
            else { Enable-ScheduledTask -InputObject $existing -ErrorAction Stop | Out-Null }
            $actualTask = Get-ScheduledTask -TaskName $name -ErrorAction Stop
            $actual = [string]$actualTask.State
            $ok = if ($Target -eq 'Stopped') { $actual -eq 'Disabled' } else { $actual -ne 'Disabled' }
            $item = [pscustomobject]@{ Name = $name; ExpectedState = $expected; ActualState = $actual; ErrorCode = $(if ($ok) { '' } else { 'STATE_MISMATCH' }); Message = $(if ($ok) { '已校验' } else { '切换后状态不符' }); Success = $ok }
            $tasks.Add($item)
            if ($ok) { $completed++ } else { $failed.Add($item) }
        } catch {
            $item = [pscustomobject]@{ Name = $name; ExpectedState = $expected; ActualState = 'MissingOrUnchanged'; ErrorCode = $_.Exception.HResult.ToString('X8'); Message = $_.Exception.Message; Success = $false }
            $tasks.Add($item); $failed.Add($item)
        }
    }
    if ($Target -eq 'Enabled' -and $failed.Count -eq 0) {
        try { Remove-Item -LiteralPath $script:GuardStopFlag -Force -ErrorAction Stop; $completed++ }
        catch { $failed.Add([pscustomobject]@{ Name = 'usb-guard-disabled.flag'; ExpectedState = 'Absent'; ActualState = 'Present'; ErrorCode = $_.Exception.HResult.ToString('X8'); Message = $_.Exception.Message; Success = $false }) }
    }
    $flag = Test-Path -LiteralPath $script:GuardStopFlag
    $kind = if ($failed.Count -eq 0) { 'Success' } elseif ($completed -gt 0) { 'Partial' } else { 'Failed' }
    $mode = if ($flag) { 'Stopped' } elseif ($Target -eq 'Enabled' -and $kind -eq 'Success') { 'Protecting' } else { 'Unknown' }
    return [pscustomobject]@{
        Action = $(if ($Target -eq 'Stopped') { 'Stop' } else { 'Enable' }); ActualMode = $mode; Result = $kind
        Message = $(if ($kind -eq 'Success') { '全部必要步骤已校验' } else { '状态切换未完成，请按失败项重试或运行安装修复' })
        StopFlagPresent = $flag; FailedItems = @($failed.ToArray()); Tasks = @($tasks.ToArray())
    }
}

function Write-ControlResult([object]$Result, [string]$Path) {
    $json = ConvertTo-Json -InputObject $Result -Depth 8
    if ([string]::IsNullOrWhiteSpace($Path)) { Write-Output $json; return }
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'USBGuard\maintenance')).TrimEnd('\') + '\'
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw '结果路径越出固定维护目录' }
    [IO.Directory]::CreateDirectory($root) | Out-Null
    [IO.File]::WriteAllText($full, $json, (New-Object Text.UTF8Encoding($false)))
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $result = if ($Target -eq 'Get') { Get-GuardControlState } else { Set-GuardControlState -Target $Target }
        Write-ControlResult -Result $result -Path $ResultPath
        if ($result.Result -eq 'Success') { exit 0 } elseif ($result.Result -eq 'Partial') { exit 2 } else { exit 1 }
    } catch {
        $failure = [pscustomobject]@{ Action = $Target; ActualMode = 'Unknown'; Result = 'Failed'; Message = $_.Exception.Message; StopFlagPresent = (Test-Path -LiteralPath $script:GuardStopFlag); FailedItems = @(); Tasks = @() }
        Write-ControlResult -Result $failure -Path $ResultPath
        exit 1
    }
}