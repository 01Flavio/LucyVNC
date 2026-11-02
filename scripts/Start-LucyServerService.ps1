[CmdletBinding(SupportsShouldProcess = $true)]
param()

. (Join-Path $PSScriptRoot 'LucyServer.Service.Common.ps1')

Assert-LvcWindowsAdministrator
Write-LvcServiceNotice
$service = Assert-LvcServiceInstalled

if ($service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running) {
    Write-Host "$script:LucyServerDisplayName is already running. Remote sharing opens only when LUCYVNC_VNC_PASSWORD holds a valid VNC password; no listener was opened."
    return
}

if ($PSCmdlet.ShouldProcess($script:LucyServerDisplayName, 'Start Windows service')) {
    Start-Service -InputObject $service -ErrorAction Stop
    Write-Host "$script:LucyServerDisplayName started. Remote sharing opens only when LUCYVNC_VNC_PASSWORD holds a valid VNC password; no listener was opened."
}
