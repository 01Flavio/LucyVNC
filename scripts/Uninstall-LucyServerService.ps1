[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Force
)

. (Join-Path $PSScriptRoot 'LucyServer.Service.Common.ps1')

Assert-LvcWindowsAdministrator
Write-LvcServiceNotice
$service = Assert-LvcServiceInstalled

if (-not $PSCmdlet.ShouldProcess($script:LucyServerDisplayName, 'Stop if running and remove Windows service')) {
    return
}
if (-not $Force -and -not $PSCmdlet.ShouldContinue(
    "Stop the $script:LucyServerDisplayName service if running and permanently uninstall it?",
    'Confirm service uninstall')) {
    Write-Host 'Uninstall cancelled.'
    return
}

if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
    Stop-Service -InputObject $service -ErrorAction Stop
    $service.WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Stopped,
        [TimeSpan]::FromSeconds(30)
    )
}

Invoke-LvcScCommand -Arguments @('delete', $script:LucyServerServiceName)
Write-Host "$script:LucyServerDisplayName was uninstalled. No firewall rules were changed."
