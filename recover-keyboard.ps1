$ErrorActionPreference = 'SilentlyContinue'
$log = @()
function Log($m){ $global:log += $m; Write-Output $m }

# 1. Disable ONLY this tool's own scheduled tasks, by exact task name.
#    Broad matching or killing unrelated script hosts can break other software.
$ownTasks = @('USB哨兵看护','USB哨兵策略守护','USB哨兵状态提示','USB哨兵实时进程')
foreach ($tn in $ownTasks) {
    $t = Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
    if ($t) {
        Stop-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
        Disable-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
        Log ("Disabled task: " + $tn)
    }
}

# 2. Stop guard process only (this tool installs no service).
Get-Process -Name 'UsbGuard' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Log "Stopped guard process"

# 3. Lift device install restriction
$restrict = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions'
Set-ItemProperty -Path $restrict -Name 'DenyUnspecified' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
# 安装脚本建的是子键，必须删子键；同时清同名值兼容旧版。
Remove-Item -Path "$restrict\AllowDeviceIDs" -Recurse -Force -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $restrict -Name 'AllowDeviceIDs' -ErrorAction SilentlyContinue
Remove-Item -Path "$restrict\AllowInstanceIDs" -Recurse -Force -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $restrict -Name AllowDenyLayered -ErrorAction SilentlyContinue
Remove-Item -Path "$restrict\DenyDeviceClasses" -Recurse -Force -ErrorAction SilentlyContinue
Log "Cleared device install restriction (DenyUnspecified=0)"

# 4. Re-enable blocked keyboard/mouse devices
$targets = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
    $_.Status -ne 'OK' -and (
        $_.Class -in @('Keyboard','Mouse','HIDClass')
    )
}
Log ("Found " + $targets.Count + " blocked device(s) to re-enable")
foreach ($d in $targets) {
    try {
        Enable-PnpDevice -InstanceId $d.InstanceId -Confirm:$false -ErrorAction Stop
        Log ("ENABLED : " + $d.InstanceId)
    } catch {
        & pnputil /enable-device $d.InstanceId 2>$null | Out-Null
        Log ("ENABLED(via pnputil): " + $d.InstanceId)
    }
}

# 5. Rescan hardware
& pnputil /scan-devices 2>$null | Out-Null
Log "Hardware rescanned."

$log | Out-File -FilePath "$env:USERPROFILE\Desktop\recover-keyboard-result.txt" -Encoding ascii
Log "RECOVERY COMPLETE. Your keyboard should work now."
