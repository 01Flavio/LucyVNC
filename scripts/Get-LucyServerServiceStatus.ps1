[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'LucyServer.Service.Common.ps1')

Assert-LvcWindowsAdministrator
Write-LvcServiceNotice
$service = Get-LvcService

if ($null -eq $service) {
    Write-Output ([pscustomobject]@{
        Name                   = $script:LucyServerServiceName
        Status                 = 'NotInstalled'
        RemoteSharingAvailable = $false
    })
    return
}

[pscustomobject]@{
    Name                   = $service.Name
    DisplayName            = $service.DisplayName
    Status                 = [string]$service.Status
    RemoteSharingAvailable = $false
}
