#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "artifacts"),
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (Test-Path $OutputDirectory) { Remove-Item $OutputDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null

$solution = Join-Path $PSScriptRoot "Cmsify.slnx"
dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }
dotnet build $solution --no-restore --configuration Release
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
$packageProjects = @(
    (Join-Path $PSScriptRoot "src\Cmsify.Contracts\Cmsify.Contracts.csproj"),
    (Join-Path $PSScriptRoot "sdk\dotnet\src\SyntaxCircus.Cmsify.Client\SyntaxCircus.Cmsify.Client.csproj"),
    (Join-Path $PSScriptRoot "sdk\dotnet\src\SyntaxCircus.Cmsify.Client.DistributedCaching\SyntaxCircus.Cmsify.Client.DistributedCaching.csproj")
)
foreach ($project in $packageProjects) {
    dotnet pack $project --no-build --configuration Release --output $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $project" }
}

$packages = @(Get-ChildItem -Path $OutputDirectory -Filter "*.nupkg" | Where-Object Name -notlike "*.symbols.nupkg")
if ($packages.Count -eq 0) { throw "No NuGet packages were produced." }
Write-Host "Local non-publishable packages: $($packages.Name -join ', ')"
if ($DryRun) { Write-Host "Dry run complete: $($packages.Name -join ', ')"; exit 0 }
Write-Host "Packages are published through GitHub Actions NuGet Trusted Publishing using secrets.NUGET_USER."
