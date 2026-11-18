# USB哨兵 · 冗余状态指示器（独立于托盘图标）
# 三种"状态标"：
#   1) 蓝色 U 托盘图标（UsbGuard.exe 本体，被关即丢，故需下面两种补足）
#   2) Windows 通知(toast) —— 本脚本每 5 分钟 + 登录时检查，异常才弹
#   3) 常驻文字日志 防护日志.txt —— 即使你错过 toast，随时打开就能看历史
# 本脚本还能自愈：发现实时进程没跑会先自己拉起，4 秒后再判定，避免误报。
# 专门区分两类故障：
#   - "蓝色 U 图标被关闭"（上一次在线、这次发现没了）→ 明确提示，并告警"可能有人/有程序在关防护"
#   - "进程崩溃/没启动"（上一次也不在）→ 提示手动启动或查事件日志
# 用法:
#   .\状态提示.ps1           正常时静默（仅写日志）
#   .\状态提示.ps1 -Heartbeat  正常时也弹一条"防护正常"通知
param([switch]$Heartbeat)

$ErrorActionPreference = 'SilentlyContinue'

# 旧版在此读 usb-guard-disabled.flag 做"彻底停止"静默退出，但主程序从不创建该标记，
# 属死代码，已移除。要彻底停止防护请运行 3-撤销加固.bat（会注销全部任务）。

$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition }
$guardExe  = Join-Path $scriptDir 'UsbGuard.exe'
$logFile   = Join-Path $env:LOCALAPPDATA 'USBGuard\防护日志.txt'
$stateFile = Join-Path $env:LOCALAPPDATA 'USBGuard\last_guard_state.txt'

function Write-EventSafe($msg, $type) {
    try { Write-EventLog -LogName Application -Source 'UsbGuard' -EntryType $type -EventId 1001 -Message $msg } catch {}
}
function Append-Log($line) {
    try {
        $dir = Split-Path -Parent $logFile
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $ts = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
        Add-Content -Path $logFile -Value ("[$ts] $line") -Encoding UTF8
    } catch {}
}
function Show-Notice($title, $body) {
    $ok = $false
    try {
        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
        [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime] | Out-Null
        $tpl = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
        $txt = $tpl.GetElementsByTagName('text')
        $txt.Item(0).AppendChild($tpl.CreateTextNode($title)) | Out-Null
        $txt.Item(1).AppendChild($tpl.CreateTextNode($body)) | Out-Null
        $doc = New-Object Windows.Data.Xml.Dom.XmlDocument
        $doc.LoadXml($tpl.OuterXml)
        $ntf = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('USB哨兵')
        $ntf.Show($doc)
        $ok = $true
    } catch { $ok = $false }
    if (-not $ok) {
        try { (New-Object -ComObject WScript.Shell).Popup($body, 30, $title, 48) } catch {}
    }
}
function Test-Reg($path, $name, $expect) {
    try { return ((Get-ItemProperty -Path $path -Name $name -ErrorAction Stop).$name -eq $expect) }
    catch { return $false }
}
function Test-HomeEdition {
    try {
        $cv = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop
        $edition = [string]$cv.EditionID
        $product = [string]$cv.ProductName
        return ($edition -match '^Core' -or $edition -match 'Home' -or $product -match 'Home')
    } catch { return $false }
}
function Test-SysLockSupported {
    # 需要同时满足：非 Home 版 + build 19041+。只查 build 会把 Home 误判为支持。
    if (Test-HomeEdition) { return $false }
    try {
        $cv = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop
        $b = 0
        if ([int]::TryParse([string]$cv.CurrentBuild, [ref]$b) -and $b -gt 0) { return ($b -ge 19041) }
    } catch { }
    return ([int][Environment]::OSVersion.Version.Build -ge 19041)
}

$problems = @()

# --- 0) 读取上次状态（判断"被关"还是"没启动"）---
$wasRunning = $false
if (Test-Path $stateFile) { $wasRunning = ([string](Get-Content $stateFile -Raw -ErrorAction SilentlyContinue)).Trim() -eq '1' }

# --- 0b) 临时停止联动已移除 ---
# 旧版在此读 usb-guard-suspend.json，但主程序从不创建该文件（暂停/维修是内存态、重启即失效）。
# 属死代码，已移除；下面两个变量保留为 False 以兼容后文引用。
$suspendActive = $false
$suspendLeftMin = 0

# --- 1) 实时进程：计数 + 重复实例检测 ---
$guardProcs   = @(Get-Process -Name 'UsbGuard' -ErrorAction SilentlyContinue)
$guardRunning = $guardProcs.Count -gt 0

if ($guardProcs.Count -gt 1) {
    if ($suspendActive) {
        Append-Log ("检测到重复实例: $($guardProcs.Count) 个（防护临时停止中，仅记录）")
    } else {
        $problems += ("发现 $($guardProcs.Count) 个 UsbGuard 进程（疑似重复打开）。建议：任务管理器→结束所有 UsbGuard.exe，再重新运行 UsbGuard.exe 让单实例互斥体接管。")
        Append-Log ("检测到重复实例: $($guardProcs.Count) 个")
    }
}

# 判定"这次一开场就不在"（用于区分被关 vs 崩溃）
$initiallyDown = -not $guardRunning
$closedEvent   = ($wasRunning -and $initiallyDown)   # 上一次在线、这次发现没了 = 图标被关

# --- 2) 自愈：没跑就先尝试拉起，4 秒后再判定 ---
if (-not $guardRunning) {
    if (Test-Path -LiteralPath $guardExe) {
        try {
            Start-Process -FilePath $guardExe -WindowStyle Hidden -ErrorAction Stop
            Start-Sleep -Seconds 4
            $guardProcs   = @(Get-Process -Name 'UsbGuard' -ErrorAction SilentlyContinue)
            $guardRunning = $guardProcs.Count -gt 0
            if ($guardRunning) {
                Write-EventSafe 'USB哨兵状态提示已自动拉起 UsbGuard' ([Diagnostics.EventLogEntryType]::Information)
                Append-Log '自动拉起 UsbGuard 成功'
            } else {
                Write-EventSafe 'USB哨兵状态提示尝试拉起 UsbGuard 后仍不在运行（可能崩溃或被拦截）' ([Diagnostics.EventLogEntryType]::Warning)
                Append-Log '自动拉起失败（可能崩溃或被拦截）'
            }
        } catch {
            Write-EventSafe ("状态提示拉起 UsbGuard 失败: " + $_.Exception.Message) ([Diagnostics.EventLogEntryType]::Warning)
            Append-Log ("自动拉起异常: " + $_.Exception.Message)
        }
    } else {
        $problems += 'UsbGuard.exe 本体缺失（程序可能被卸载或移动）'
        Append-Log 'UsbGuard.exe 缺失'
    }
}

# --- 3) 专门提示"蓝色 U 图标被关了"（第二种状态标核心）---
if ($suspendActive) {
    Append-Log ("防护临时停止中（剩 $suspendLeftMin 分钟），跳过进程检查")
} else {
    if ($closedEvent) {
        if ($guardRunning) {
            Append-Log '⚠️ 蓝色 U 图标被关闭事件 → 已自动恢复'
            Show-Notice 'USB哨兵 · 托盘图标被关闭（已恢复）' '蓝色 U 图标刚才被关闭，已自动重新拉起。若频繁发生，可能有程序在试图关闭防护，请警惕。'
        } else {
            Append-Log '⚠️ 蓝色 U 图标被关闭事件 → 自动恢复失败'
            $problems += '蓝色 U 托盘图标被关闭且自动恢复失败！实时监控当前离线，请手动启动 UsbGuard.exe 或查事件日志 Application\UsbGuard。'
        }
    }
    if (-not $guardRunning -and -not $closedEvent) {
        $problems += 'UsbGuard 进程未运行（实时 U 盘监控未启动；已尝试自动拉起但未成功，请手动启动或查看事件日志 Application\UsbGuard）'
        Append-Log '⚠️ UsbGuard 未运行（非关闭事件，可能崩溃/未启动）'
    }
}

# 写回本次状态（供下次判定"是否被关"）
try { Set-Content -Path $stateFile -Value ($(if ($guardRunning) { '1' } else { '0' })) -Encoding UTF8 } catch {}

# --- 4) 系统策略层（不依赖进程，永远在线）---
# Home 或 build < 19041 时安装脚本不写系统级策略（只保留报警/锁屏/看护），
# 这里必须同步跳过策略检查，否则每 5 分钟误报一次"防护异常"，与使用说明矛盾。
# 主进程监控与自愈（上面 1-3 段）照常执行，不受影响。
if (Test-SysLockSupported) {
    $restrict = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions'
    if (-not (Test-Reg $restrict 'DenyUnspecified' 1)) {
        $problems += '系统设备限制 DenyUnspecified 不等于 1（陌生 USB 可能能装驱动）'
    }
    $aid = Join-Path $restrict 'AllowInstanceIDs'
    $aidEmpty = (-not (Test-Path $aid) -or @(Get-Item $aid).GetValueNames().Count -eq 0)
    if ($aidEmpty) {
        # fix-03 R2：三段复判——(i-a) 守护心跳 90 秒内跳过；(i-b) 策略守护正在运行跳过；(ii) 5 秒后复判仍空才报
        $hbRecent = $false
        try {
            $hb = Join-Path $scriptDir 'last_enforce.txt'
            if (Test-Path -LiteralPath $hb) {
                $ht = $null
                if ([DateTime]::TryParse([string](Get-Content -LiteralPath $hb -Raw -ErrorAction Stop), [ref]$ht) -and ((Get-Date) - $ht).TotalSeconds -lt 90) { $hbRecent = $true }
            }
        } catch { }
        if ($hbRecent) {
            Append-Log '白名单为空但策略守护近期活动，跳过本轮'
        } else {
            $enforceRunning = $false
            try {
                $q = schtasks.exe /Query /TN 'USB哨兵策略守护' /FO LIST 2>&1
                if ($LASTEXITCODE -eq 0 -and (($q -join ' ') -match '正在运行|Running')) { $enforceRunning = $true }
            } catch { }
            if ($enforceRunning) {
                Append-Log '白名单为空但策略守护正在运行，跳过本轮'
            } else {
                Start-Sleep -Seconds 5
                $aidEmpty2 = (-not (Test-Path $aid) -or @(Get-Item $aid).GetValueNames().Count -eq 0)
                if ($aidEmpty2) {
                    $problems += '设备实例白名单 AllowInstanceIDs 为空或缺失'
                } else {
                    Append-Log '白名单复判已恢复（5 秒窗口内被守护重建），跳过本轮'
                }
            }
        }
    }
    $rsd = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}'
    if (-not (Test-Reg $rsd 'Deny_Execute' 1)) {
        $problems += '可移动存储 Deny_Execute 不等于 1（U 盘里的程序可能被执行）'
    }
} else {
    Append-Log 'Home 或 build < 19041：系统级策略检查已跳过（仅报警/锁屏/看护）'
}

# --- 5) 汇总 ---
if ($problems.Count -eq 0) {
    if ($Heartbeat) {
        Show-Notice 'USB哨兵' '防护正常：实时拦截 + 系统设备限制均在线'
        Append-Log '检查通过（Heartbeat）'
    }
    exit 0
} else {
    $msg = ($problems -join "`n")
    Show-Notice 'USB哨兵 · 防护异常' $msg
    Append-Log ('检查异常: ' + $msg.Replace("`n", ' | '))
    exit 1
}

