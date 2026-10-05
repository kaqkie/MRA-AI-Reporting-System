<#
  Makes the MRA Reporting Assistant start by itself when you sign in to Windows.
  It adds a Windows scheduled task named "MRA Reporting Assistant" that runs start-all.ps1
  one minute after you sign in (so Windows and the network are ready first).

  Turn it on:   powershell -ExecutionPolicy Bypass -File .\scripts\install-autostart.ps1
  Turn it off:  powershell -ExecutionPolicy Bypass -File .\scripts\install-autostart.ps1 -Remove
  Check it:     Get-ScheduledTask -TaskName "MRA Reporting Assistant"
#>
param([switch]$Remove)

$taskName = "MRA Reporting Assistant"

if ($Remove) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "Automatic start turned off."
    exit 0
}

$startAll = Join-Path $PSScriptRoot "start-all.ps1"
$user = "$env:USERDOMAIN\$env:USERNAME"

$action = New-ScheduledTaskAction -Execute "powershell.exe" `
    -Argument "-ExecutionPolicy Bypass -WindowStyle Minimized -File `"$startAll`" -NoBrowser" `
    -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$trigger.Delay = "PT1M"
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings `
    -Principal $principal -Description "Starts llama-server and the MRA Reporting Assistant web app." -Force | Out-Null

Write-Host "Done. From your next sign-in, the model and the app start by themselves (about a minute after you sign in)."
Write-Host "Then open http://localhost:5080 in your browser."
