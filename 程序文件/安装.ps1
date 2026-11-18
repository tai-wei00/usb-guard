<#
 USB 哨兵 · 加固版安装与维护脚本
 由文件夹里的 bat 调用；也可手动运行。
   无参数          完整安装 / 升级
   -DryRun         只显示计划，不改任何东西
   -AddDevice      把新插入的 USB 设备加入信任名单
   -EnforcePolicy  核对并修正系统允许列表（定时自动运行）
   -Uninstall      卸载并撤销系统级限制
#>
param(
    [switch]$DryRun,
    [switch]$AddDevice,
    [switch]$EnforcePolicy,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$srcDir   = $PSScriptRoot
$pf       = $env:ProgramW6432
if (-not $pf) { $pf = $env:ProgramFiles }
$target   = $srcDir   # 就地安装到本文件夹（程序文件\），不再指向已删除的 C:\Program Files\USB哨兵
$policy   = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions'
$allowKey = "$policy\AllowDeviceIDs"
$instKey  = "$policy\AllowInstanceIDs"
# 微软文档：AllowDeviceIDs 只认 hardware/compatible ID，实例 ID 必须写 AllowInstanceIDs
# （Win10 2004+）。本项目白名单存的是设备实例 ID，因此改用 $instKey。
$removable = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}'
$tWatch   = 'USB哨兵看护'
$tPolicy  = 'USB哨兵策略守护'

function Step($m) { Write-Host "[*] $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "[OK] $m" -ForegroundColor Green }
function Warn($m) { Write-Host "[!] $m" -ForegroundColor Yellow }

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# 读取真实 Windows build（Win10+ 的 [Environment]::OSVersion 可能受 manifest 影响返回 6.2）
function Get-OsBuild {
    try {
        $cv = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop
        $b = 0
        if ([int]::TryParse([string]$cv.CurrentBuild, [ref]$b) -and $b -gt 0) { return $b }
    } catch { }
    return [int][Environment]::OSVersion.Version.Build
}

function Test-HomeEdition {
    # Home 版（Windows 10/11 Home、Core/CoreCountrySpecific/CoreSingleLanguage 等）
    # 不支持 DeviceInstallation/RemovableStorage 组策略；build 再新也不能走系统级锁定。
    try {
        $cv = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop
        $edition = [string]$cv.EditionID
        $product = [string]$cv.ProductName
        return ($edition -match '^Core' -or $edition -match 'Home' -or $product -match 'Home')
    } catch { return $false }
}

function Test-SysLockSupported {
    # 需要同时满足：非 Home 版 + build >= 19041（Win10 2004）。
    # 只查 build 会把 Windows 10/11 Home 误判为支持，进而写一套系统实际不生效的策略。
    if (Test-HomeEdition) { return $false }
    return ((Get-OsBuild) -ge 19041)
}

# 回滚：注销本次注册的任务（参数来自调用方，不依赖全局任务名）
function Undo-InstallTasks([string[]]$names) {
    foreach ($tn in $names) {
        try { Unregister-ScheduledTask -TaskName $tn -Confirm:$false -ErrorAction Stop }
        catch { }
    }
}

function Get-WhitePath {
    $p = Join-Path $target 'usb-whitelist.json'
    if (Test-Path -LiteralPath $p) { return $p }
    return (Join-Path $srcDir 'usb-whitelist.json')
}

# 白名单双格式读取：旧格式=字符串数组；新格式=对象数组（deviceIds/description/category/trustedAt）。
# 统一输出扁平的实例 ID 大写数组；混合格式明确报错返回空（不按空名单继续加固）。
function Read-White([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    try {
        $json = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8) | ConvertFrom-Json
        $r = @(); $hasStr = $false; $hasObj = $false
        foreach ($x in $json) {
            if ($x -is [string]) { $hasStr = $true; $r += $x }
            elseif ($null -ne $x.PSObject.Properties['deviceIds']) { $hasObj = $true; foreach ($i in $x.deviceIds) { $r += [string]$i } }
        }
        if ($hasStr -and $hasObj) { Warn '信任名单混合格式（字符串与对象并存），已中止读取，请先运行新版主程序完成迁移'; return @() }
        return @($r | ForEach-Object { ([string]$_).ToUpperInvariant() } | Sort-Object -Unique)
    } catch { Warn "信任名单读取失败：$($_.Exception.Message)"; return @() }
}

function Group-KeyOf([string]$id) {
    $m = [regex]::Match($id, '(VID_[0-9A-F]{4}&PID_[0-9A-F]{4})', 'IgnoreCase')
    if ($m.Success) { return $m.Groups[1].Value.ToUpperInvariant() }
    return "UNGROUPED:$($id.ToUpperInvariant())"
}

# 结构化保存：新 ID 按 VID+PID 归并进既有条目（或新建条目），旧格式自动升级为对象数组；原子写回。
function Save-White([string]$path, [string[]]$ids) {
    $entries = @()
    if (Test-Path -LiteralPath $path) {
        try {
            $json = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8) | ConvertFrom-Json
            foreach ($x in $json) {
                if ($x -is [string]) {
                    $entries += ,@{ deviceIds = @(([string]$x).Trim().ToUpperInvariant()); description = '未知设备'; category = 'other'; trustedAt = $null }
                } elseif ($null -ne $x.PSObject.Properties['deviceIds']) {
                    $eids = @($x.deviceIds | ForEach-Object { ([string]$_).Trim().ToUpperInvariant() })
                    if ($eids.Count -gt 0) {
                        $entries += ,@{ deviceIds = $eids
                                        description = $(if ($x.description) { [string]$x.description } else { '未知设备' })
                                        category    = $(if ($x.category)    { [string]$x.category }    else { 'other' })
                                        trustedAt   = $(if ($x.trustedAt)   { [string]$x.trustedAt }   else { $null }) }
                    }
                }
            }
        } catch { Warn "读取现有信任名单失败：$($_.Exception.Message)" }
    }
    $now = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
    foreach ($raw in $ids) {
        $id = ([string]$raw).Trim().ToUpperInvariant()
        if (-not $id) { continue }
        $exists = $false
        foreach ($e in $entries) { if ($e.deviceIds -contains $id) { $exists = $true; break } }
        if ($exists) { continue }
        $gk = Group-KeyOf $id
        $targetE = $null
        if (-not $gk.StartsWith('UNGROUPED:')) {
            foreach ($e in $entries) {
                if ($e.deviceIds.Count -gt 0 -and (Group-KeyOf $e.deviceIds[0]) -eq $gk) { $targetE = $e; break }
            }
        }
        if ($null -eq $targetE) {
            $targetE = @{ deviceIds = @(); description = '未知设备'; category = 'other'; trustedAt = $now }
            $entries += ,$targetE
        }
        $targetE.deviceIds = @($targetE.deviceIds + $id)
        if (-not $targetE.trustedAt) { $targetE.trustedAt = $now }
    }
    $out = @($entries | ForEach-Object {
        [ordered]@{ deviceIds = @($_.deviceIds | Sort-Object); description = $_.description; category = $_.category; trustedAt = $_.trustedAt }
    }) | ConvertTo-Json -Depth 4
    $tmp = "$path.tmp"
    try {
        [IO.File]::WriteAllText($tmp, $out, (New-Object Text.UTF8Encoding($false)))
        if ([IO.File]::ReadAllText($tmp, [Text.Encoding]::UTF8) -ne $out) { throw '写回校验不一致' }
        if (Test-Path -LiteralPath $path) { [IO.File]::Replace($tmp, $path, $null) }
        else { Move-Item -LiteralPath $tmp -Destination $path -Force }
    } catch {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        throw "信任名单保存失败：$($_.Exception.Message)"
    }
}

function Get-CurrentUsbIds {
    $ids = @()
    Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
        Where-Object { $_.DeviceID -like 'USB\VID_*' } | ForEach-Object {
            $p = $_.DeviceID.Split('\')
            if ($p.Length -ge 3) {
                $hw = $p[1]
                $mi = $hw.IndexOf('&MI_')
                if ($mi -gt 0) { $hw = $hw.Substring(0, $mi) }
                $ids += (($p[0] + '\' + $hw + '\' + $p[2]).ToUpperInvariant())
            }
        }
    return @($ids | Sort-Object -Unique)
}

# 本机当前已安装的全部 PnP 设备实例 ID（含网卡/声卡/PCI/Tailscale 等非 USB 设备）。
# 用作 AllowDeviceIDs 的兜底：DenyUnspecified=1 下，已装设备不会被误挡（曾因缺失导致 Tailscale 断连）。
function Get-AllDeviceIds {
    $ids = @()
    Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.DeviceID) { $ids += $_.DeviceID.ToUpperInvariant() }
    }
    return @($ids | Sort-Object -Unique)
}

# 会被陌生设备冒用的条目：USB 型号级（不含序列号）、USB 存储通用 ID、纯通配根集线器
function Test-Dangerous([string]$id) {
    if (-not $id) { return $true }
    if ($id -match '^USB\\VID_'  -and $id -notmatch '^USB\\[^\\]+\\[^\\]+')  { return $true }
    if ($id -match '^USBSTOR\\' -and $id -notmatch '^USBSTOR\\[^\\]+\\[^\\]+') { return $true }
    if ($id -match '^USB\\ROOT_HUB[0-9]+$') { return $true }
    return $false
}

function Get-AllowValues {
    # 从实例 ID 白名单键读取（旧版误写在 AllowDeviceIDs，这里也兼容读）
    $out = @()
    foreach ($key in @($instKey, $allowKey)) {
        if (-not (Test-Path $key)) { continue }
        foreach ($n in (Get-Item $key).GetValueNames()) {
            $v = [string](Get-ItemPropertyValue -Path $key -Name $n -ErrorAction SilentlyContinue)
            if ($v) { $out += $v }
        }
    }
    return @($out | Sort-Object -Unique)
}

function Sync-AllowList([string[]]$WhiteIds, [switch]$WhatIf) {
    # 最后一道防线：老版本 Windows 上 AllowInstanceIDs 被忽略，写 DenyUnspecified
    # 会造成信任设备也装不上。所有入口（安装/加设备/守护）都经此函数，统一拦截。
    if (-not (Test-SysLockSupported)) {
        Warn '当前 Windows 版本不满足系统级策略要求（需 Win10 2004+ 且 Pro/Enterprise/Education/LTSC）：已跳过系统级设备策略写入（仅保留报警/锁屏）。'
        return
    }
    if (-not (Test-Path $policy)) {
        if (-not $WhatIf) { New-Item -Path $policy -Force | Out-Null }
    }
    if (-not (Test-Path $instKey)) {
        if (-not $WhatIf) { New-Item -Path $instKey -Force | Out-Null }
    }
    $existing = Get-AllowValues
    $keep = @(); $drop = @()
    foreach ($id in $existing) { if (Test-Dangerous $id) { $drop += $id } else { $keep += $id } }
    # 安全兜底：把本机当前所有已安装设备实例 ID 一并纳入放行表，
    # 避免 DenyUnspecified=1 时把网卡/Tailscale/声卡等非 USB 设备挡掉（2026-11-12 曾致两台电脑断连）。
    foreach ($c in (Get-AllDeviceIds)) { if ($keep -notcontains $c) { $keep += $c } }
    foreach ($w in $WhiteIds) {
        if ($w -and $keep -notcontains $w) { $keep += $w }
    }
    $keep = @($keep | Sort-Object -Unique)
    Step ("实例放行列表：保留 $($keep.Count) 条（含本机当前全部设备兜底），剔除 $($drop.Count) 条可被冒用条目，信任名单 $($WhiteIds.Count) 条")
    if ($WhatIf) { return }
    # 先增后删：先把缺失条目增量写入（任何时刻集合均为合法超集），再剔除危险条目，
    # 消除"白名单短暂为空"被状态提示误报的竞态窗口。
    $occupied = @{}
    $maxN = 0
    foreach ($n in (Get-Item $instKey).GetValueNames()) {
        $occupied[$n] = $true
        $num = 0
        if ([int]::TryParse($n, [ref]$num) -and $num -gt $maxN) { $maxN = $num }
    }
    foreach ($v in $keep) {
        if (@(Get-Item $instKey).GetValueNames() | ForEach-Object { [string](Get-ItemPropertyValue -Path $instKey -Name $_ -ErrorAction SilentlyContinue) } | Where-Object { $_ -eq $v }) { continue }
        $next = $maxN + 1
        while ($occupied.ContainsKey([string]$next)) { $next++ }
        $name = [string]$next
        try {
            New-ItemProperty -Path $instKey -Name $name -Value $v -PropertyType String -ErrorAction Stop | Out-Null
            $occupied[$name] = $true
            if ($next -gt $maxN) { $maxN = $next }
        } catch {
            Warn ("写入实例放行条目失败（跳过）：$($_.Exception.Message)")
        }
    }
    foreach ($n in (Get-Item $instKey).GetValueNames()) {
        $cur = [string](Get-ItemPropertyValue -Path $instKey -Name $n -ErrorAction SilentlyContinue)
        if (Test-Dangerous $cur) {
            Remove-ItemProperty -Path $instKey -Name $n -Force -ErrorAction SilentlyContinue
        }
    }
    # 清理旧版误写进 AllowDeviceIDs 的实例 ID（语义错误：那个键只认硬件ID/兼容ID）。
    if (Test-Path $allowKey) {
        foreach ($n in (Get-Item $allowKey).GetValueNames()) {
            Remove-ItemProperty -Path $allowKey -Name $n -Force -ErrorAction SilentlyContinue
        }
    }
    New-ItemProperty -Path $policy -Name DenyUnspecified -Value 1 -PropertyType DWord -Force | Out-Null
    # 启用分层评估：实例级 Allow 才能压过"可移动设备/设备类"的 Prevent。
    # 微软文档：默认所有 Prevent 优先，不设此项白名单可能被类规则压制。
    New-ItemProperty -Path $policy -Name AllowDenyLayered -Value 1 -PropertyType DWord -Force | Out-Null
}

function Write-EnforceHeartbeat {
    try {
        Set-Content -LiteralPath (Join-Path $target 'last_enforce.txt') -Value ((Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')) -Encoding UTF8 -ErrorAction SilentlyContinue
    } catch { }
}

function Set-ProtectedAcl([string]$dir) {
    $acl = Get-Acl -LiteralPath $dir
    $acl.SetAccessRuleProtection($true, $false)
    $inh = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    $pr  = [Security.AccessControl.PropagationFlags]::None
    $ty  = [Security.AccessControl.AccessControlType]::Allow
    foreach ($r in @(@('BUILTIN\Administrators','FullControl'),
                     @('NT AUTHORITY\SYSTEM','FullControl'),
                     @('BUILTIN\Users','ReadAndExecute'))) {
        $acl.SetAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($r[0], $r[1], $inh, $pr, $ty)))
    }
    Set-Acl -LiteralPath $dir -AclObject $acl
    # 只给日志文件单独开放写权限，配置与信任名单仍为只读
    $log = Join-Path $dir 'usb-guard.log'
    if (-not (Test-Path -LiteralPath $log)) { New-Item -ItemType File -Path $log -Force | Out-Null }
    $la = Get-Acl -LiteralPath $log
    $la.SetAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        'BUILTIN\Users', 'Read, Write', $pr, $pr, $ty)))
    Set-Acl -LiteralPath $log -AclObject $la
}

function Write-Event([string]$msg, [Diagnostics.EventLogEntryType]$kind) {
    try {
        if (-not [Diagnostics.EventLog]::SourceExists('UsbGuard')) {
            [Diagnostics.EventLog]::CreateEventSource('UsbGuard', 'Application')
        }
        [Diagnostics.EventLog]::WriteEntry('UsbGuard', $msg, $kind)
    } catch { }
}

function Invoke-Install {
    Write-Host ''
    Write-Host '要做的四件事：' -ForegroundColor Cyan
    Step "1) 程序装到 $target —— 那里只有管理员能改，普通软件动不了防护自己"
    Step '2) 看护脚本去掉停止开关，谁关掉防护都会自动拉回来'
    Step '3) 系统允许列表剔除可被冒用的型号条目，只认你信任的具体设备'
    Step '4) 每 10 分钟自动核对一次，误点托盘同步也能自动恢复'
    if ($DryRun) { Warn '预览结束，没有改动任何东西'; return }
    if (-not (Test-Admin)) { throw '需要管理员权限：请双击「1-装好就能用.bat」并在 UAC 点「是」。' }

    # 本仓库不附带预编译 exe。必须先自行编译，否则会装出"策略生效但没有托盘程序、
    # 无法信任设备"的半装状态。在写任何策略/ACL 之前先检查，避免留下需要撤销的残局。
    $nextExe0 = Join-Path $srcDir 'UsbGuard.next.exe'
    $mainExe0 = Join-Path $target 'UsbGuard.exe'
    if (-not (Test-Path -LiteralPath $nextExe0) -and -not (Test-Path -LiteralPath $mainExe0)) {
        throw ('找不到主程序。请先双击「程序文件\源码\编译.bat」生成 UsbGuard.next.exe，再运行安装。' +
               '本仓库不提供预编译 exe（见 README）。当前未改动任何系统策略。')
    }
    Ok '已找到主程序，继续安装。'

    # 升级段：若存在 UsbGuard.next.exe（新版编译产物），先停防护再替换，旧版备份为 UsbGuard.pre-v2.bak
    $nextExe = Join-Path $srcDir 'UsbGuard.next.exe'
    if (Test-Path -LiteralPath $nextExe) {
        Step '检测到新版程序（UsbGuard.next.exe），执行升级替换...'
        foreach ($tn in @('USB哨兵看护','USB哨兵策略守护','USB哨兵状态提示','USB哨兵实时进程')) {
            Stop-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
        }
        Get-Process -Name UsbGuard -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
        $exeTarget = Join-Path $target 'UsbGuard.exe'
        try {
            if (Test-Path -LiteralPath $exeTarget) {
                [IO.File]::Replace($nextExe, $exeTarget, (Join-Path $target 'UsbGuard.pre-v2.bak'))
            } else {
                Move-Item -LiteralPath $nextExe -Destination $exeTarget -Force
            }
            Ok '已替换 UsbGuard.exe（旧版备份为 UsbGuard.pre-v2.bak）'
        } catch {
            Warn "替换 UsbGuard.exe 失败：$($_.Exception.Message)，已保留现场"
            throw
        }
    }

    if (-not (Test-Path -LiteralPath $target)) { New-Item -ItemType Directory -Path $target -Force | Out-Null }

    $wlTarget = Join-Path $target 'usb-whitelist.json'
    # 首次安装判定：文件不存在【或内容为空】都要按本机当前设备生成。
    # 分发包预置的 [] 会让旧的 "仅判断不存在" 逻辑跳过生成，导致名单永远空、
    # 主程序把空数组当"启动时插着的设备全部信任"（LoadWhiteV2），与陌生设备报警冲突。
    $existing = @(Read-White $wlTarget)
    if ((-not (Test-Path -LiteralPath $wlTarget)) -or $existing.Count -eq 0) {
        # 按「这台电脑当前插着的设备」生成名单。
        # 分发包里带来的旧名单，只保留仍在本机存在的条目 —— 别人装不会继承作者的设备。
        $current = Get-CurrentUsbIds
        $shipped = @(Read-White (Join-Path $srcDir 'usb-whitelist.json'))
        $valid   = @($shipped | Where-Object { $current -contains $_ })
        $initial = @($current + $valid | Sort-Object -Unique)
        if ($initial.Count -eq 0) {
            # 连键盘鼠标都没探到：宁可保留空名单并明确警告，也不要静默全放行。
            Warn '没检测到任何 USB 设备。名单保持为空，首次插入设备时会按陌生设备报警；请确认键鼠可用。'
            $initial = @()
        }
        Save-White $wlTarget $initial
        Ok "已按这台电脑生成信任名单（$($initial.Count) 台）"
    } else { Step '保留已有的信任名单（不覆盖，不丢数据）' }

    if ($srcDir -ne $target) {
        foreach ($f in @('UsbGuard.exe', 'watchdog.vbs', '安装.ps1', 'usb-guard-config.json', '状态提示.ps1')) {
            $s = Join-Path $srcDir $f
            if (Test-Path -LiteralPath $s) { Copy-Item -LiteralPath $s -Destination (Join-Path $target $f) -Force }
        }
    }

    # 注意：程序目录 ACL 与系统策略都移到「四个任务注册成功之后」再写，
    # 避免安全软件拦下任务时留下"策略生效但没有主程序/看护"的半装状态。

    # 清理源目录下的旧副本程序文件，防止"影子进程"抢互斥体导致硬化失效（F-01/F-07）
    if ($srcDir -ne $target) {
        foreach ($f in @('UsbGuard.exe', 'watchdog.vbs', '状态提示.ps1', 'usb-guard-config.json')) {
            $p = Join-Path $srcDir $f
            if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
        }
        Step "已清理源目录旧副本程序文件，避免其与受保护副本争抢互斥体"
    }

    $bl = @{}
    foreach ($f in @('UsbGuard.exe', 'watchdog.vbs', '安装.ps1', '状态提示.ps1')) {
        $p = Join-Path $target $f
        if (Test-Path -LiteralPath $p) { $bl[$f] = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }
    }
    [IO.File]::WriteAllText((Join-Path $target 'integrity.json'),
        (ConvertTo-Json -InputObject $bl), (New-Object Text.UTF8Encoding($false)))
    Step '已记录程序指纹，被改动会在事件日志里留痕'

    # 系统级策略的写入已移到四个任务全部注册成功之后（见本函数末尾）。

    $oldLnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\USB哨兵.lnk'
    if (Test-Path -LiteralPath $oldLnk) {
        Remove-Item -LiteralPath $oldLnk -Force
        Step '已移除旧的开机自启快捷方式（改由任务负责）'
    }

    # 任务注册结果收集：看护/实时进程是必需项，缺一即判定安装失败并回滚。
    $registeredTasks = @()
    $failedCritical  = @()
    $failedOptional  = @()

    $me = "$env:USERDOMAIN\$env:USERNAME"
    $wshExe = Join-Path $env:windir 'System32\wscript.exe'
    $wshArgs = '//nologo "' + (Join-Path $target 'watchdog.vbs') + '"'
    $a1 = New-ScheduledTaskAction -Execute $wshExe -Argument $wshArgs
    $p1 = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
    $t1 = New-ScheduledTaskTrigger -AtLogOn -User $me
    $t2 = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5)
    try {
        Register-ScheduledTask -TaskName $tWatch -Action $a1 -Principal $p1 -Trigger @($t1, $t2) -Force -ErrorAction Stop | Out-Null
        if (Get-ScheduledTask -TaskName $tWatch -ErrorAction SilentlyContinue) { $registeredTasks += $tWatch; Ok '看护任务已重建（登录即启动，每 5 分钟检查）' }
        else { $failedCritical += "$tWatch（注册后校验不到）"; Warn '看护任务注册后校验失败'; Write-Event '看护任务注册失败（校验未通过）' ([Diagnostics.EventLogEntryType]::Warning) }
    } catch {
        $failedCritical += "$tWatch（$($_.Exception.Message)）"
        Warn "看护任务注册失败：$($_.Exception.Message)"; Write-Event "看护任务注册异常：$($_.Exception.Message)" ([Diagnostics.EventLogEntryType]::Warning)
    }

    $psExe = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $psArgs = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -NonInteractive -File "' + (Join-Path $target '安装.ps1') + '" -EnforcePolicy'
    $a2 = New-ScheduledTaskAction -Execute $psExe -Argument $psArgs
    $p2 = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $t3 = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(2) -RepetitionInterval (New-TimeSpan -Minutes 10)
    try {
        Register-ScheduledTask -TaskName $tPolicy -Action $a2 -Principal $p2 -Trigger $t3 -Force -ErrorAction Stop | Out-Null
        if (Get-ScheduledTask -TaskName $tPolicy -ErrorAction SilentlyContinue) { $registeredTasks += $tPolicy; Ok '策略守护已开启（每 10 分钟自动校正）' }
        else { $failedOptional += "$tPolicy（注册后校验不到）"; Warn '策略守护任务注册后校验失败，请管理员检查任务计划程序'; Write-Event '策略守护任务注册失败（校验未通过）' ([Diagnostics.EventLogEntryType]::Warning) }
    } catch {
        # 多为安全软件(如卡巴斯基自保/应用权限控制)或系统策略禁止创建"以 SYSTEM 身份 / 最高权限运行"的计划任务。
        # 这类失败属「可接受降级」（失去自动校正，但实时拦截、看护保活、主程序都还在），不计入致命失败。
        $failedOptional += "$tPolicy（被系统/安全软件拦截）"
        $denied = ($_.Exception.Message -match '拒绝访问|Access is denied|0x80070005')
        if ($denied) {
            Warn "策略守护任务被系统/安全软件策略阻止创建（常见原因：卡巴斯基自保禁止创建高权限计划任务，错误：$($_.Exception.Message)）。"
            Warn "这不影响实时 USB 防护（陌生设备禁驱动、U盘禁执行、看护保活均正常）。"
            Warn "若需要『被篡改后自动恢复』，请手动运行以下命令（管理员 PowerShell）："
            Warn "  powershell -ExecutionPolicy Bypass -File `"$target\安装.ps1`" -EnforcePolicy"
        } else {
            Warn "策略守护任务注册失败：$($_.Exception.Message)"
        }
        Write-Event ("策略守护任务注册异常：$($_.Exception.Message)") ([Diagnostics.EventLogEntryType]::Warning)
    }

    # --- 冗余状态提示任务（当前用户、登录即启、每5分钟；独立于托盘图标）---
    $tStatus = 'USB哨兵状态提示'
    # 与看护任务一致：经 wscript VBS 包装隐藏启动（窗口参数 0），
    # 不再直接调 powershell -WindowStyle Hidden（计划任务下控制台窗口会闪现/驻留）。
    $stExe  = $wshExe
    $stArgs = '//nologo "' + (Join-Path $target '状态提示.vbs') + '"'
    $a3 = New-ScheduledTaskAction -Execute $stExe -Argument $stArgs
    $p3 = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
    # 用两个触发器实现「登录即启 + 每5分钟」：-AtLogOn 参数集不接受 RepetitionInterval。
    # 与看护任务 t1/t2 的写法一致，避免 "Parameter set cannot be resolved" 终止安装。
    $t5 = New-ScheduledTaskTrigger -AtLogOn -User $me
    $t6 = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5)
    try {
        Register-ScheduledTask -TaskName $tStatus -Action $a3 -Principal $p3 -Trigger @($t5, $t6) -Force -ErrorAction Stop | Out-Null
        if (Get-ScheduledTask -TaskName $tStatus -ErrorAction SilentlyContinue) { $registeredTasks += $tStatus; Ok '冗余状态提示已开启（每 5 分钟检查，防护异常会弹通知，不依赖托盘图标）' }
        else { $failedOptional += "$tStatus（注册后校验不到）"; Warn '状态提示任务注册后校验失败，请检查任务计划程序' }
    } catch {
        $failedOptional += "$tStatus（$($_.Exception.Message)）"
        Warn "状态提示任务注册失败：$($_.Exception.Message)"
    }

    # --- 实时进程启动任务（登录即启动 UsbGuard，跑在交互会话里，蓝色 U 图标才会显示）---
    $tGuard = 'USB哨兵实时进程'
    $gExe  = Join-Path $target 'UsbGuard.exe'
    $a4 = New-ScheduledTaskAction -Execute $gExe
    $p4 = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
    $t4 = New-ScheduledTaskTrigger -AtLogOn -User $me
    try {
        Register-ScheduledTask -TaskName $tGuard -Action $a4 -Principal $p4 -Trigger $t4 -Force -ErrorAction Stop | Out-Null
        if (Get-ScheduledTask -TaskName $tGuard -ErrorAction SilentlyContinue) { $registeredTasks += $tGuard; Ok '实时进程启动任务已开启（登录即启动 UsbGuard，托盘图标会显示）' }
        else { $failedCritical += "$tGuard（注册后校验不到）"; Warn '实时进程启动任务注册后校验失败'; Write-Event '实时进程任务注册失败（校验未通过）' ([Diagnostics.EventLogEntryType]::Warning) }
    } catch {
        $failedCritical += "$tGuard（$($_.Exception.Message)）"
        Warn "实时进程启动任务注册失败：$($_.Exception.Message)"; Write-Event "实时进程任务注册异常：$($_.Exception.Message)" ([Diagnostics.EventLogEntryType]::Warning)
    }

    # ===== 关键任务判定：看护/实时进程任一失败 → 回滚，绝不写系统策略/ACL =====
    if ($failedCritical.Count -gt 0) {
        Warn ("关键任务注册失败：" + ($failedCritical -join '；'))
        Warn '正在回滚已注册的任务；程序目录权限与系统策略均未被写入，不会留下半装状态。'
        Undo-InstallTasks $registeredTasks
        foreach ($tn in @($tWatch, $tPolicy, $tStatus, $tGuard)) {
            try { Stop-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue } catch { }
        }
        Write-Event ("USB哨兵安装失败（关键任务未注册）：" + ($failedCritical -join '；')) ([Diagnostics.EventLogEntryType]::Error)
        throw "安装失败：关键任务未注册成功（$($failedCritical -join '；')）。已回滚；请检查任务计划程序或安全软件（如卡巴自保禁止创建计划任务）后重试。"
    }

    # ===== 关键任务全部就绪 → 先锁目录 ACL（与系统版本无关，始终执行）=====
    # ACL 与 AllowInstanceIDs 无关：老版本 Windows 同样要防止普通用户改看护/白名单。
    Set-ProtectedAcl $target
    Ok '程序目录已锁定（普通用户只读）'

    # ===== 再写系统级策略：仅非 Home 且 19041+ 支持 AllowInstanceIDs 时执行 =====
    if (-not (Test-SysLockSupported)) {
        Warn '当前 Windows 版本不满足系统级策略要求（Home，或 build < 19041）。'
        Warn '本次只保留「报警 + 锁屏 + 看护」；系统级拦驱动已跳过，以免误判或把信任设备挡住。'
        Write-Event 'USB哨兵安装：老版本 Windows，系统级设备策略已跳过' ([Diagnostics.EventLogEntryType]::Warning)
    } else {
        Sync-AllowList -WhiteIds (Read-White (Get-WhitePath))
        # RemovableStorageDevices\{GUID} 子键在干净 Windows 上通常不存在；
        # New-ItemProperty 不会自动建父路径，必须先 New-Item，否则会在加固中途抛错。
        if (-not (Test-Path -LiteralPath $removable)) {
            New-Item -Path $removable -Force | Out-Null
        }
        New-ItemProperty -Path $removable -Name Deny_Execute -Value 1 -PropertyType DWord -Force | Out-Null
        Ok '系统级设备限制已开启（陌生 USB 装不上驱动）'
    }

    Get-Process -Name UsbGuard -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-ScheduledTask -TaskName $tWatch -ErrorAction SilentlyContinue
    Write-Event 'USB 哨兵加固版安装完成' ([Diagnostics.EventLogEntryType]::Information)
    Ok '安装完成：右下角出现蓝色 U 图标就是已经保护上了。'
    if ($failedOptional.Count -gt 0) {
        Warn ("以下增强功能未启用（不影响实时拦截与看护保活）：" + ($failedOptional -join '；'))
    }
}

function Invoke-AddDevice {
    if (-not (Test-Path -LiteralPath (Join-Path $target 'UsbGuard.exe'))) {
        throw '还没装加固版：请先双击「1-装好就能用.bat」。'
    }
    if (-not (Test-Admin)) { throw '需要管理员权限：请双击「2-添加新设备.bat」并在 UAC 点「是」。' }
    $wlPath = Get-WhitePath
    $white  = Read-White $wlPath
    $now    = Get-CurrentUsbIds
    $new    = @($now | Where-Object { $white -notcontains $_ })
    if ($new.Count -eq 0) { Ok '没有发现新设备，信任名单不用改'; return }
    Write-Host ''
    Write-Host "发现 $($new.Count) 台新设备：" -ForegroundColor Yellow
    $new | ForEach-Object { Write-Host "    $_" }
    Write-Host ''
    if ($DryRun) { Warn '预览结束，没有改动任何东西'; return }
    $ans = Read-Host '确认加入信任名单吗？输入 Y 回车继续，其他键取消'
    if ($ans -ne 'Y' -and $ans -ne 'y') { Warn '已取消，什么都没改'; return }
    Save-White $wlPath @($white + $new)
    Write-EnforceHeartbeat
    Sync-AllowList -WhiteIds (Read-White $wlPath)
    Ok "已信任 $($new.Count) 台设备。系统防护全程没关过。"
}

function Invoke-Enforce {
    # 与安装入口同一道防线：老版本 Windows 不支持 AllowInstanceIDs，
    # 绝不能写 DenyUnspecified（否则所有新设备含信任设备都装不上）。
    if (-not (Test-SysLockSupported)) {
        if ($DryRun) { Warn '当前 Windows 版本不支持系统级策略（Home 或 build < 19041）：策略守护跳过系统级策略校正' }
        return
    }
    $wl = Read-White (Get-WhitePath)
    $blPath = Join-Path $target 'integrity.json'
    if (Test-Path -LiteralPath $blPath) {
        try {
            $ref = [IO.File]::ReadAllText($blPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
            foreach ($f in @('UsbGuard.exe', 'watchdog.vbs', '安装.ps1', '状态提示.ps1')) {
                $p = Join-Path $target $f
                if ((Test-Path -LiteralPath $p) -and $ref.$f) {
                    $h = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash
                    if ($h -ne $ref.$f) {
                        Write-Event "程序文件被改动：$f" ([Diagnostics.EventLogEntryType]::Warning)
                    }
                }
            }
        } catch { }
    }
    $cur = Get-AllowValues
    $danger = @($cur | Where-Object { Test-Dangerous $_ }).Count
    $missing = 0
    foreach ($w in $wl) { if ($w -and $cur -notcontains $w) { $missing++ } }

    # 完整性守护：不止补白名单，还要把被删掉的策略项恢复回加固状态。
    # 否则"每10分钟校正"只是补名单，DenyUnspecified/Deny_Execute 被删后不会回来。
    $policyFixed = @()
    if (-not (Test-Path -LiteralPath $policy)) { New-Item -Path $policy -Force | Out-Null }
    $deny = (Get-ItemProperty -Path $policy -Name DenyUnspecified -ErrorAction SilentlyContinue).DenyUnspecified
    if ($deny -ne 1) {
        New-ItemProperty -Path $policy -Name DenyUnspecified -Value 1 -PropertyType DWord -Force | Out-Null
        $policyFixed += 'DenyUnspecified'
    }
    if (-not (Test-Path -LiteralPath $removable)) { New-Item -Path $removable -Force | Out-Null }
    $dx = (Get-ItemProperty -Path $removable -Name Deny_Execute -ErrorAction SilentlyContinue).Deny_Execute
    if ($dx -ne 1) {
        New-ItemProperty -Path $removable -Name Deny_Execute -Value 1 -PropertyType DWord -Force | Out-Null
        $policyFixed += 'Deny_Execute'
    }
    $lay = (Get-ItemProperty -Path $policy -Name AllowDenyLayered -ErrorAction SilentlyContinue).AllowDenyLayered
    if ($lay -ne 1) {
        New-ItemProperty -Path $policy -Name AllowDenyLayered -Value 1 -PropertyType DWord -Force | Out-Null
        $policyFixed += 'AllowDenyLayered'
    }
    if ($DryRun) {
        Step "需要剔除 $danger 条、补回 $missing 条；策略待恢复：$($policyFixed -join ', ')"
        return
    }
    Write-EnforceHeartbeat
    if ($danger -gt 0 -or $missing -gt 0 -or -not (Test-Path $instKey)) {
        Sync-AllowList -WhiteIds $wl
        Write-Event "已校正允许列表（剔除 $danger 条，补回 $missing 条）" ([Diagnostics.EventLogEntryType]::Information)
    }
    if ($policyFixed.Count -gt 0) {
        Write-Event "已恢复策略项：$($policyFixed -join ', ')" ([Diagnostics.EventLogEntryType]::Information)
    }
}

function Invoke-Uninstall {
    if ($DryRun) { Step '预览：将删除四个计划任务与开机自启快捷方式、撤销系统限制；程序文件与信任名单全部保留'; return }
    if (-not (Test-Admin)) { throw '需要管理员权限。' }
    # 先注销全部四个计划任务（看护/策略守护/状态提示/实时进程），再停进程，避免看护把进程又拉起来。
    # 单个任务不存在或注销失败不影响其余清理。
    foreach ($t in @($tWatch, $tPolicy, 'USB哨兵状态提示', 'USB哨兵实时进程')) {
        try { Unregister-ScheduledTask -TaskName $t -Confirm:$false -ErrorAction Stop }
        catch { }
    }
    Get-Process -Name UsbGuard -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    # 就地安装时 $target 与 $srcDir 是同一目录，Copy-Item 到自己会抛
    #「无法使用项 its own 覆盖该项」并在删除策略前中断。同源时直接跳过。
    if ($srcDir -ne $target) {
        foreach ($f in @('usb-whitelist.json', 'usb-guard-config.json')) {
            $s = Join-Path $target $f
            if (Test-Path -LiteralPath $s) {
                Copy-Item -LiteralPath $s -Destination (Join-Path $srcDir $f) -Force
            }
        }
    }
    Remove-ItemProperty -Path $policy -Name DenyUnspecified -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $policy -Name AllowDenyLayered -ErrorAction SilentlyContinue
    Remove-Item -Path $allowKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $instKey -Recurse -Force -ErrorAction SilentlyContinue
    # 类限制也必须撤销，否则卸载后 USB/HID 新设备仍被挡。
    Remove-Item -Path "$policy\DenyDeviceClasses" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $removable -Name Deny_Execute -ErrorAction SilentlyContinue
    # 恢复程序目录 ACL：安装时 Set-ProtectedAcl 把 Users 降为只读，卸载要还回去。
    try {
        if (Test-Path -LiteralPath $target) {
            $acl = Get-Acl -LiteralPath $target
            $rule = New-Object Security.AccessControl.FileSystemAccessRule(
                'BUILTIN\Users', 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
            $acl.AddAccessRule($rule)
            Set-Acl -LiteralPath $target -AclObject $acl
        }
    } catch { Warn "恢复目录权限失败（不影响卸载）：$($_.Exception.Message)" }
    $oldLnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'USB哨兵.lnk'
    if (Test-Path -LiteralPath $oldLnk) { Remove-Item -LiteralPath $oldLnk -Force }
    Ok '已卸载。信任名单和程序文件都保留，没有删你的数据。'
}

Write-Host ''
Write-Host '=== USB 哨兵 · 加固版 ===' -ForegroundColor Cyan
if ($AddDevice)          { Invoke-AddDevice }
elseif ($Uninstall)      { Invoke-Uninstall }
elseif ($EnforcePolicy)  { Invoke-Enforce }
else                     { Invoke-Install }

