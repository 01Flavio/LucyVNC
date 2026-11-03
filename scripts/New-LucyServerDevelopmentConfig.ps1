[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$EnableSharing,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'LucyVNC Server development configuration is supported only on Windows.'
}

$directoryPath = Join-Path $env:LOCALAPPDATA 'LucyVNC\Development'
$configurationPath = Join-Path $directoryPath 'service.json'
$sharingEnabled = $EnableSharing.IsPresent
$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
if ($null -eq $currentSid) {
    throw 'Could not resolve the current Windows user SID.'
}

if (Test-Path -LiteralPath $configurationPath) {
    if ((Get-Item -LiteralPath $configurationPath -Force).Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
        throw 'The development configuration cannot be a reparse point.'
    }

    if (-not $Force) {
        throw "The configuration already exists. Review it or rerun with -Force to replace it: $configurationPath"
    }
}

if ($sharingEnabled) {
    Write-Warning 'This enables an unencrypted classic VNC listener on 127.0.0.1:5901. Start the interactive agent only when ready for a controlled local test.'
}

if (-not $PSCmdlet.ShouldProcess($configurationPath, 'Write a current-user-only LucyVNC Server development configuration')) {
    return
}

[void][System.IO.Directory]::CreateDirectory($directoryPath)
$directorySecurity = New-Object System.Security.AccessControl.DirectorySecurity
$directorySecurity.SetAccessRuleProtection($true, $false)
$directoryRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
    $currentSid,
    [System.Security.AccessControl.FileSystemRights]::FullControl,
    [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit,
    [System.Security.AccessControl.PropagationFlags]::None,
    [System.Security.AccessControl.AccessControlType]::Allow
)
$directorySecurity.AddAccessRule($directoryRule)
[System.IO.Directory]::SetAccessControl($directoryPath, $directorySecurity)

$configuration = [ordered]@{
    RemoteSharingEnabled = $sharingEnabled
    BindAddress = '127.0.0.1'
    Port = 5901
    AuthenticationRequired = $true
    MaxSessions = 1
}
$json = $configuration | ConvertTo-Json
$temporaryPath = Join-Path $directoryPath "service.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    [System.IO.File]::WriteAllText($temporaryPath, $json, [System.Text.UTF8Encoding]::new($false))
    $fileSecurity = New-Object System.Security.AccessControl.FileSecurity
    $fileSecurity.SetAccessRuleProtection($true, $false)
    $fileRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        $currentSid,
        [System.Security.AccessControl.FileSystemRights]::FullControl,
        [System.Security.AccessControl.AccessControlType]::Allow
    )
    $fileSecurity.AddAccessRule($fileRule)
    [System.IO.FileInfo]::new($temporaryPath).SetAccessControl($fileSecurity)
    Move-Item -LiteralPath $temporaryPath -Destination $configurationPath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}

Write-Output "Configuration written: $configurationPath"
Write-Output "RemoteSharingEnabled: $sharingEnabled"
