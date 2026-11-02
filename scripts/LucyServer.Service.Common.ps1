Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:LucyServerServiceName = 'LucyVNCService'
$script:LucyServerDisplayName = 'LucyServer'
$script:LucyServerDefaultExecutablePath = Join-Path `
    (Split-Path -Parent $PSScriptRoot) `
    'src\Vnc.Service\bin\Release\net8.0-windows\win-x64\publish\Vnc.Service.exe'

function Assert-LvcWindowsAdministrator {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'LucyServer service management is supported only on Windows.'
    }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Administrator elevation is required. Open PowerShell with "Run as administrator" and invoke this script again.'
    }
}

function Write-LvcServiceNotice {
    Write-Warning 'LucyServer opens an authenticated loopback RFB listener when remote sharing is enabled; its interactive-session agent and authenticated IPC are not implemented, so the worker provides no usable VNC sharing without a valid VNC password. These scripts do not create firewall rules.'
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
