Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Set-Location $PSScriptRoot\..

dotnet publish "src/Vnc.App/Vnc.App.csproj" -c Release -r win-x64 --self-contained true
