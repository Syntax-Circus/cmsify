#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ApiKey = $env:NUGET_API_KEY,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "artifacts"),
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $DryRun -and [string]::IsNullOrWhiteSpace($ApiKey)) {
    throw "No NuGet API key provided. Pass -ApiKey, set `$env:NUGET_API_KEY, or use -DryRun."
}

if (Test-Path $OutputDirectory) { Remove-Item $OutputDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null

$solution = Join-Path $PSScriptRoot "Cmsify.slnx"
dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }
dotnet build $solution --no-restore --configuration Release
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
dotnet pack $solution --no-build --configuration Release --output $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed" }

$packages = @(Get-ChildItem -Path $OutputDirectory -Filter "*.nupkg" | Where-Object Name -notlike "*.symbols.nupkg")
if ($packages.Count -eq 0) { throw "No NuGet packages were produced." }
if ($DryRun) { Write-Host "Dry run complete: $($packages.Name -join ', ')"; exit 0 }

foreach ($package in $packages) {
    dotnet nuget push $package.FullName --api-key $ApiKey --source https://api.nuget.org/v3/index.json --skip-duplicate
    if ($LASTEXITCODE -ne 0) { throw "NuGet push failed for $($package.Name)" }
}
