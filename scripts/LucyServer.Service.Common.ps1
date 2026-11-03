Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:LucyServerServiceName = 'LucyVNCService'
$script:LucyServerDisplayName = 'LucyVNC Server'
$script:LucyServerDefaultExecutablePath = Join-Path `
    (Split-Path -Parent $PSScriptRoot) `
    'artifacts\publish\server-worker\LucyVNC.Server.Worker.exe'

function Assert-LvcWindowsAdministrator {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'LucyVNC Server service management is supported only on Windows.'
    }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Administrator elevation is required. Open PowerShell with "Run as administrator" and invoke this script again.'
    }
}

function Write-LvcServiceNotice {
    Write-Warning 'The Worker and Agent use versioned named-pipe IPC with Windows SID checks, but SCM-to-interactive-session Agent authorization and lifecycle are not validated. Service state does not prove that an RFB listener is available. These scripts do not create firewall rules.'
}

function Get-LvcService {
    Get-Service -Name $script:LucyServerServiceName -ErrorAction SilentlyContinue
}

function Assert-LvcServiceInstalled {
    $service = Get-LvcService
    if ($null -eq $service) {
        throw "The $script:LucyServerDisplayName service is not installed."
    }

    return $service
}

function Invoke-LvcScCommand {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & sc.exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe failed with exit code $LASTEXITCODE."
    }
}
