Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Set-Location $PSScriptRoot\..

dotnet restore "LucyVNC.slnx"
dotnet build "LucyVNC.slnx" -c Release

dotnet test "LucyVNC.slnx" -c Release --no-build
