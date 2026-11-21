# USB 哨兵 v2.1.0 冗余状态提示：统一状态、完整路径、暂停仍检活、异常去噪。
param([switch]$Heartbeat)
$ErrorActionPreference = 'SilentlyContinue'
$scriptDir = $PSScriptRoot
$guardExe = Join-Path $scriptDir 'UsbGuard.exe'
$stopFlag = Join-Path $scriptDir 'usb-guard-disabled.flag'
if (Test-Path -LiteralPath $stopFlag) { exit 0 }
$localDir = Join-Path $env:LOCALAPPDATA 'USBGuard'
[IO.Directory]::CreateDirectory($localDir) | Out-Null
$logFile = Join-Path $localDir '防护日志.txt'
$fingerprintFile = Join-Path $localDir 'status-fingerprint.txt'
$stateHistory = Join-Path $localDir 'last_guard_state.txt'

function Append-Log([string]$Line) {
    try { Add-Content -LiteralPath $logFile -Value ("[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Line") -Encoding UTF8 -ErrorAction Stop } catch { }
}
function Show-Notice([string]$Title, [string]$Body) {
    $shown = $false
    try {
        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
        $template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
        $text = $template.GetElementsByTagName('text')
        $text.Item(0).AppendChild($template.CreateTextNode($Title)) | Out-Null
        $text.Item(1).AppendChild($template.CreateTextNode($Body)) | Out-Null
        $document = New-Object Windows.Data.Xml.Dom.XmlDocument
        $document.LoadXml($template.OuterXml)
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('USB哨兵').Show($document)
        $shown = $true
    } catch { }
    if (-not $shown) { try { (New-Object -ComObject WScript.Shell).Popup($Body, 30, $Title, 48) | Out-Null } catch { } }
}
function Get-CurrentGuardProcesses {
    $expected = [IO.Path]::GetFullPath($guardExe)
    return @(Get-CimInstance Win32_Process -Filter "Name='UsbGuard.exe'" -ErrorAction SilentlyContinue | Where-Object {
        $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -eq $expected
    })
}
function Test-Reg([string]$Path, [string]$Name, [int]$Expected) {
    try { return ((Get-ItemProperty -Path $Path -Name $Name -ErrorAction Stop).$Name -eq $Expected) } catch { return $false }
}

$stateText = '状态未知'
$stateDetail = '统一状态文件暂不可读'
try {
    $statePath = Join-Path $localDir 'guard-state.json'
    if (Test-Path -LiteralPath $statePath) {
        $state = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($statePath, [Text.Encoding]::UTF8)) -ErrorAction Stop
        $stateText = [string]$state.displayName
        $stateDetail = [string]$state.detail
    }
} catch { }

$wasRunning = $false
if (Test-Path -LiteralPath $stateHistory) { $wasRunning = ([IO.File]::ReadAllText($stateHistory, [Text.Encoding]::UTF8)).Trim() -eq '1' }
$processes = @(Get-CurrentGuardProcesses)
$running = $processes.Count -gt 0
$problems = New-Object System.Collections.Generic.List[string]
if ($processes.Count -gt 1) { $problems.Add("发现 $($processes.Count) 个当前目录 UsbGuard 实例，请检查单实例激活") }
if (-not $running) {
    if (Test-Path -LiteralPath $guardExe) {
        try { Start-Process -FilePath $guardExe -WindowStyle Hidden -ErrorAction Stop; Start-Sleep -Seconds 4 } catch { $problems.Add('实时进程拉起失败：' + $_.Exception.Message) }
        $processes = @(Get-CurrentGuardProcesses)
        $running = $processes.Count -gt 0
    } else { $problems.Add('UsbGuard.exe 缺失，请运行安装修复') }
}
# 临时暂停和维修只改变报警/锁屏，不允许进程退出；因此这里不因暂停跳过检活。
if (-not $running) { $problems.Add($(if ($wasRunning) { '实时进程已消失且自动恢复失败' } else { '实时进程未运行且自动拉起失败' })) }
try { [IO.File]::WriteAllText($stateHistory, $(if ($running) { '1' } else { '0' }), [Text.Encoding]::UTF8) } catch { }

$restrict = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions'
if (-not (Test-Reg $restrict 'DenyUnspecified' 1)) { $problems.Add('系统设备限制 DenyUnspecified 未启用') }
$allow = Join-Path $restrict 'AllowDeviceIDs'
$allowEmpty = (-not (Test-Path $allow) -or @((Get-Item $allow).GetValueNames()).Count -eq 0)
if ($allowEmpty) {
    $heartbeatRecent = $false
    try {
        $heartbeat = Join-Path $scriptDir 'last_enforce.txt'
        $time = [DateTime]::MinValue
        if ((Test-Path -LiteralPath $heartbeat) -and [DateTime]::TryParse(([IO.File]::ReadAllText($heartbeat, [Text.Encoding]::UTF8)), [ref]$time)) {
            $heartbeatRecent = ((Get-Date) - $time).TotalSeconds -lt 90
        }
    } catch { }
    if (-not $heartbeatRecent) {
        Start-Sleep -Seconds 5
        if (-not (Test-Path $allow) -or @((Get-Item $allow).GetValueNames()).Count -eq 0) { $problems.Add('设备允许列表为空或缺失') }
    }
}
$removable = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}'
if (-not (Test-Reg $removable 'Deny_Execute' 1)) { $problems.Add('可移动存储 Deny_Execute 未启用') }

$fingerprint = ($problems.ToArray() -join '|')
$previous = ''
try { if (Test-Path -LiteralPath $fingerprintFile) { $previous = ([IO.File]::ReadAllText($fingerprintFile, [Text.Encoding]::UTF8)).Trim() } } catch { }
if ($problems.Count -eq 0) {
    if ($previous -or $Heartbeat) {
        Show-Notice 'USB哨兵 · 状态恢复' "$stateText：实时进程与系统策略正常"
        Append-Log "状态恢复/正常：$stateText"
    }
    if ($Heartbeat) { Append-Log "Heartbeat：$stateText；$stateDetail" }
} elseif ($fingerprint -ne $previous) {
    $message = $problems.ToArray() -join "`n"
    Show-Notice "USB哨兵 · $stateText" $message
    Append-Log ("状态异常：" + $fingerprint)
}
try { [IO.File]::WriteAllText($fingerprintFile, $fingerprint, [Text.Encoding]::UTF8) } catch { }
if ($problems.Count -eq 0) { exit 0 } else { exit 1 }