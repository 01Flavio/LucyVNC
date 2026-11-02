[CmdletBinding(SupportsShouldProcess = $true)]
param()

. (Join-Path $PSScriptRoot 'LucyServer.Service.Common.ps1')

Assert-LvcWindowsAdministrator
Write-LvcServiceNotice
$service = Assert-LvcServiceInstalled

if ($service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
    Write-Host "$script:LucyServerDisplayName is already stopped."
    return
}

if (-not $PSCmdlet.ShouldProcess($script:LucyServerDisplayName, 'Stop Windows service')) {
    return
}
if (-not $PSCmdlet.ShouldContinue(
    "Stop the $script:LucyServerDisplayName Windows service?",
    'Confirm service stop')) {
    Write-Host 'Stop cancelled.'
    return
}

Stop-Service -InputObject $service -ErrorAction Stop
$service.WaitForStatus(
    [System.ServiceProcess.ServiceControllerStatus]::Stopped,
    [TimeSpan]::FromSeconds(30)
)
Write-Host "$script:LucyServerDisplayName stopped. The VNC listener was released."
