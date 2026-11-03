[CmdletBinding(SupportsShouldProcess = $true)]
param()

. (Join-Path $PSScriptRoot 'LucyServer.Service.Common.ps1')

Assert-LvcWindowsAdministrator
Write-LvcServiceNotice
$service = Assert-LvcServiceInstalled

if (-not $PSCmdlet.ShouldProcess($script:LucyServerDisplayName, 'Restart Windows service')) {
    return
}

if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
    Stop-Service -InputObject $service -ErrorAction Stop
    $service.WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Stopped,
        [TimeSpan]::FromSeconds(30)
    )
}

Start-Service -InputObject $service -ErrorAction Stop
Write-Host "$script:LucyServerDisplayName restarted. Verify the service diagnostics and authenticated Agent connection in LucyVNC."
