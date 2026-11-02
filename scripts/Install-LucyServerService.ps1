[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ExecutablePath
)

. (Join-Path $PSScriptRoot 'LucyServer.Service.Common.ps1')

Assert-LvcWindowsAdministrator
Write-LvcServiceNotice

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = $script:LucyServerDefaultExecutablePath
}

$resolvedExecutablePath = (Resolve-Path -LiteralPath $ExecutablePath -ErrorAction Stop).Path
if ([IO.Path]::GetFileName($resolvedExecutablePath) -ne 'Vnc.Service.exe') {
    throw 'ExecutablePath must point to the published Vnc.Service.exe.'
}
if ($resolvedExecutablePath.Contains('"')) {
    throw 'ExecutablePath cannot contain a double quote.'
}
if ($null -ne (Get-LvcService)) {
    throw "The $script:LucyServerDisplayName service is already installed. Uninstall it before installing a different executable."
}

if (-not $PSCmdlet.ShouldProcess($resolvedExecutablePath, "Install $script:LucyServerDisplayName as Automatic and leave it stopped")) {
    return
}

$quotedExecutablePath = '"{0}"' -f $resolvedExecutablePath
New-Service `
    -Name $script:LucyServerServiceName `
    -DisplayName $script:LucyServerDisplayName `
    -BinaryPathName $quotedExecutablePath `
    -StartupType Automatic `
    -Description 'LucyServer opens an authenticated loopback RFB listener when remote sharing is enabled; an interactive-session agent and IPC are not implemented.' |
    Out-Null

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

Write-Host "$script:LucyServerDisplayName was installed as Automatic and left stopped. Remote sharing opens only when LUCYVNC_VNC_PASSWORD holds a valid VNC password; it remains unavailable until then."
