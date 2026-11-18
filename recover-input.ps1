#Requires -RunAsAdministrator
$ErrorActionPreference = 'SilentlyContinue'
$ConfirmPreference = 'None'
$log = @()
function Log($m){ $global:log += $m; Write-Output $m }

Log "=== USB Guard input recovery (auto, no prompt) ==="

# 1) Disable ONLY this tool's own tasks by exact name.
#    Broad action regexes can disable unrelated third-party tasks.
$ownTasks = @('USB哨兵看护','USB哨兵策略守护','USB哨兵状态提示','USB哨兵实时进程')
foreach ($tn in $ownTasks) {
    $t = Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
    if ($t) {
        Stop-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
        Disable-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
        Log ("disabled task: " + $tn)
    }
}

# 2) Kill guard processes repeatedly to defeat respawn.
for ($k=0; $k -lt 3; $k++) {
    Get-Process -Name 'UsbGuard' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like '*watchdog.vbs*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
}
Log "stopped guard process"

# 3) 本工具不安装任何服务，不要按 'Guard|Usb' 模糊匹配去停/禁用第三方服务
#    （会误伤其他安全软件）。计划任务已在上面按精确名称处理，进程也已停。

# 4) Lift the device-install restriction.
$policy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions'
Set-ItemProperty -Path $policy -Name 'DenyUnspecified' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
Remove-Item -Path "$policy\AllowDeviceIDs" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "$policy\AllowInstanceIDs" -Recurse -Force -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $policy -Name AllowDenyLayered -ErrorAction SilentlyContinue
Remove-Item -Path "$policy\DenyDeviceClasses" -Recurse -Force -ErrorAction SilentlyContinue
Log "device restriction lifted (DenyUnspecified=0)"

# 5) Re-enable + restart all blocked input devices, looped to defeat re-disable.
for ($round=1; $round -le 3; $round++) {
    $devs = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
        $d = $_
        ($d.Status -ne 'OK') -and ($d.Class -in @('HIDClass','Keyboard','Mouse'))
    }
    Log ("round $round : $($devs.Count) device(s) to fix")
    foreach ($d in $devs) {
        $id = $d.InstanceId
        & pnputil /enable-device "$id" 2>$null | Out-Null
        & pnputil /restart-device "$id" 2>$null | Out-Null
        Log ("enabled+restarted: " + $id)
    }
    Start-Sleep -Seconds 2
}

# 6) Rescan hardware so HID re-enumerates.
& pnputil /scan-devices 2>$null | Out-Null
Log "hardware rescanned"

Log "NOTE: USB Guard is now DISABLED and device restrictions lifted."
$global:log | Out-File -FilePath "$env:USERPROFILE\Desktop\recover-input-result.txt" -Encoding ascii
Log "DONE. Mouse and keyboard should work now."
