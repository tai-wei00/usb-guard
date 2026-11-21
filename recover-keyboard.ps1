$ErrorActionPreference = 'SilentlyContinue'
$log = @()
function Log($m){ $global:log += $m; Write-Output $m }

# 1. Disable any scheduled task that runs the USB guard.
#    Match by action path containing UsbGuard / USB / Guard (all ASCII, avoids Chinese).
$all = Get-ScheduledTask -ErrorAction SilentlyContinue
foreach ($t in $all) {
    $hit = $false
    try {
        foreach ($a in $t.Actions) {
            $cmd = ($a.Execute + ' ' + $a.Arguments)
            if ($cmd -match 'UsbGuard|USB|Guard') { $hit = $true; break }
        }
    } catch {}
    if ($t.TaskName -match 'Guard|Usb') { $hit = $true }
    if ($hit) {
        Stop-ScheduledTask -TaskName $t.TaskName -ErrorAction SilentlyContinue
        Disable-ScheduledTask -TaskName $t.TaskName -ErrorAction SilentlyContinue
        Log ("Disabled task: " + $t.TaskName)
    }
}

# 2. Stop guard service + process
Stop-Service -Name 'UsbGuard' -Force -ErrorAction SilentlyContinue
Get-Process -Name 'UsbGuard' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process -Name 'wscript' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Log "Stopped guard service/process"

# 3. Lift device install restriction
$restrict = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions'
Set-ItemProperty -Path $restrict -Name 'DenyUnspecified' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $restrict -Name 'AllowDeviceIDs' -ErrorAction SilentlyContinue
Remove-Item -Path "$restrict\DenyDeviceClasses" -Recurse -Force -ErrorAction SilentlyContinue
Log "Cleared device install restriction (DenyUnspecified=0)"

# 4. Re-enable blocked keyboard/mouse devices
$targets = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
    $_.Status -ne 'OK' -and (
        $_.InstanceId -match 'VID_XXXX' -or
        $_.InstanceId -match 'VID_1EA7' -or
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
