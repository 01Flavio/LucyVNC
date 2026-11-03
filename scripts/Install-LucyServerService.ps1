[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ExecutablePath,
    [ValidateSet('Automatic', 'Manual')]
    [string]$StartupType = 'Automatic',
    [switch]$UpdateExisting
)

. (Join-Path $PSScriptRoot 'LucyServer.Service.Common.ps1')

Assert-LvcWindowsAdministrator
Write-LvcServiceNotice

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = $script:LucyServerDefaultExecutablePath
}

$resolvedExecutablePath = (Resolve-Path -LiteralPath $ExecutablePath -ErrorAction Stop).Path
if ([IO.Path]::GetFileName($resolvedExecutablePath) -ne 'LucyVNC.Server.Worker.exe') {
    throw 'ExecutablePath must point to the published LucyVNC.Server.Worker.exe.'
}
if ($resolvedExecutablePath.Contains('"')) {
    throw 'ExecutablePath cannot contain a double quote.'
}
if (-not $PSCmdlet.ShouldProcess($resolvedExecutablePath, "Install or update $script:LucyServerDisplayName with startup type $StartupType")) {
    return
}

$quotedExecutablePath = '"{0}"' -f $resolvedExecutablePath
$service = Get-LvcService
if ($null -ne $service) {
    if (-not $UpdateExisting) {
        throw "The $script:LucyServerDisplayName service already exists. Use -UpdateExisting to update its executable path."
    }

    $wasRunning = $service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running
    if ($wasRunning) {
        Stop-Service -InputObject $service -ErrorAction Stop
        $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
    }

    $scStartupType = if ($StartupType -eq 'Automatic') { 'auto' } else { 'demand' }
    Invoke-LvcScCommand -Arguments @(
        'config',
        $script:LucyServerServiceName,
        'binPath=',
        $quotedExecutablePath,
        'start=',
        $scStartupType
    )
    if ($wasRunning) {
        Start-Service -Name $script:LucyServerServiceName -ErrorAction Stop
        $service = Get-LvcService
        $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
    }
}
else {
    New-Service `
        -Name $script:LucyServerServiceName `
        -DisplayName $script:LucyServerDisplayName `
        -BinaryPathName $quotedExecutablePath `
        -StartupType $StartupType `
        -Description 'LucyVNC Server Worker. Desktop sharing requires an authenticated interactive-session Agent.' |
        Out-Null
}

# SCM performs at most three delayed restarts for failures, then takes no action.
# A normal stop is not a service failure and does not trigger these recovery actions.
Invoke-LvcScCommand -Arguments @(
    'failure',
    $script:LucyServerServiceName,
    'reset=',
    '86400',
    'actions=',
    'restart/60000/restart/60000/restart/60000/none/0'
)
Invoke-LvcScCommand -Arguments @(
    'failureflag',
    $script:LucyServerServiceName,
    '1'
)

Write-Host "$script:LucyServerDisplayName was installed or updated with startup type $StartupType. Installation does not configure firewall rules."
