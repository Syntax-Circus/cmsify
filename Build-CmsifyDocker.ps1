#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string[]]$Targets = @('api', 'admin'),

    [string]$ImageTag = '0.0.0-local',

    [string]$Registry = '',

    [switch]$Push,

    [switch]$NoCache,

    [ValidateNotNullOrEmpty()]
    [string[]]$Platforms = @('linux/amd64', 'linux/arm64')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Header {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "`n=== $Message ===" -ForegroundColor Cyan
}

function Write-Success {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "OK  $Message" -ForegroundColor Green
}

function Fail-Build {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "ERR $Message" -ForegroundColor Red
    throw $Message
}

function Resolve-RepoPath {
    param([Parameter(Mandatory)][string]$Path)
    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return Join-Path $PSScriptRoot $Path
}

function Get-NormalizedValues {
    param(
        [Parameter(Mandatory)][string[]]$Values,
        [string[]]$AllowedValues,
        [Parameter(Mandatory)][string]$Name
    )

    $normalized = @($Values | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim().ToLowerInvariant() } | Select-Object -Unique)
    if ($normalized.Count -eq 0) { Fail-Build "At least one $Name must be specified." }

    if ($null -ne $AllowedValues) {
        $invalid = @($normalized | Where-Object { $_ -notin $AllowedValues })
        if ($invalid.Count -gt 0) { Fail-Build "Unsupported ${Name}: $($invalid -join ', '). Supported values: $($AllowedValues -join ', ')." }
    }

    return $normalized
}

function Get-PlatformSuffix {
    param([Parameter(Mandatory)][string]$Platform)
    switch ($Platform) {
        'linux/amd64' { return 'amd64' }
        'linux/arm64' { return 'arm64' }
        default { return (($Platform -replace '[^a-zA-Z0-9._-]', '-') -replace '-{2,}', '-').Trim('-') }
    }
}

function Add-PlatformSuffix {
    param([Parameter(Mandatory)][string]$ImageReference, [Parameter(Mandatory)][string]$Suffix)
    $tagIndex = $ImageReference.LastIndexOf(':')
    if ($tagIndex -lt 0) { return "$ImageReference-$Suffix" }
    return "$($ImageReference.Substring(0, $tagIndex))`:$($ImageReference.Substring($tagIndex + 1))-$Suffix"
}

function Get-ImageReference {
    param([Parameter(Mandatory)][string]$ImageName, [Parameter(Mandatory)][string]$Tag, [string]$Registry)
    $prefix = if ([string]::IsNullOrWhiteSpace($Registry)) { 'syntaxcircus' } else { $Registry.Trim().TrimEnd('/') }
    return "$prefix/$ImageName`:$Tag"
}

$targetDefinitions = [ordered]@{
    api = @{ DisplayName = 'API'; ImageName = 'cmsify-api'; DockerfilePath = Resolve-RepoPath '.\src\Cmsify.Api\Dockerfile'; ProjectPath = Resolve-RepoPath '.\src\Cmsify.Api\Cmsify.Api.csproj' }
    admin = @{ DisplayName = 'Admin'; ImageName = 'cmsify-admin'; DockerfilePath = Resolve-RepoPath '.\src\Cmsify.Admin\Dockerfile'; ProjectPath = Resolve-RepoPath '.\src\Cmsify.Admin\Cmsify.Admin.csproj' }
}

$Targets = Get-NormalizedValues -Values $Targets -AllowedValues $targetDefinitions.Keys -Name 'target'
$Platforms = Get-NormalizedValues -Values $Platforms -Name 'platform'
if ($Push -and [string]::IsNullOrWhiteSpace($Registry)) { Fail-Build '-Push requires an explicit -Registry. Authenticate first with docker login.' }

Write-Header 'Checking Docker'
& docker info *> $null
if ($LASTEXITCODE -ne 0) { Fail-Build 'Docker is not running or is not accessible. Start Docker and try again.' }
Write-Success 'Docker is running'

Write-Header 'Checking Docker Buildx'
& docker buildx inspect --bootstrap *> $null
if ($LASTEXITCODE -ne 0) { Fail-Build 'Docker Buildx is required. Ensure Docker includes Buildx and try again.' }
Write-Success 'Docker Buildx is ready'

foreach ($target in $Targets) {
    $definition = $targetDefinitions[$target]
    if (-not (Test-Path $definition.DockerfilePath)) { Fail-Build "Dockerfile not found for '$target': $($definition.DockerfilePath)" }
    if (-not (Test-Path $definition.ProjectPath)) { Fail-Build "Project file not found for '$target': $($definition.ProjectPath)" }
}

$buildVersion = $ImageTag
$informationalVersion = $buildVersion
$publishedTags = @($ImageTag)

Write-Header 'Build configuration'
Write-Host "Targets: $($Targets -join ', ')"
Write-Host "Tags: $($publishedTags -join ', ')"
Write-Host "Platforms: $($Platforms -join ', ')"
Write-Host "Registry: $(if ([string]::IsNullOrWhiteSpace($Registry)) { '<local: syntaxcircus>' } else { $Registry })"
Write-Host "Push: $Push"

Push-Location $PSScriptRoot
try {
    foreach ($target in $Targets) {
        $definition = $targetDefinitions[$target]
        $imageReferences = @($publishedTags | ForEach-Object { Get-ImageReference -ImageName $definition.ImageName -Tag $_ -Registry $Registry })

        Write-Header "Building $($definition.DisplayName)"
        if ($Push) {
            $arguments = @('buildx', 'build', '--platform', ($Platforms -join ','))
            foreach ($image in $imageReferences) { $arguments += @('-t', $image) }
            $arguments += @('--build-arg', "BUILD_VERSION=$buildVersion", '--build-arg', "BUILD_INFORMATIONAL_VERSION=$informationalVersion", '-f', $definition.DockerfilePath)
            if ($NoCache) { $arguments += '--no-cache' }
            $arguments += @('--push', '.')
            & docker @arguments
            if ($LASTEXITCODE -ne 0) { Fail-Build "Docker build/push failed for '$target'." }
            Write-Success "$($definition.DisplayName) pushed: $($imageReferences -join ', ')"
            continue
        }

        foreach ($platform in $Platforms) {
            $suffix = Get-PlatformSuffix $platform
            $platformImages = @($imageReferences | ForEach-Object { Add-PlatformSuffix -ImageReference $_ -Suffix $suffix })
            $arguments = @('buildx', 'build', '--platform', $platform)
            foreach ($image in $platformImages) { $arguments += @('-t', $image) }
            if ($platform -eq 'linux/amd64') { foreach ($image in $imageReferences) { $arguments += @('-t', $image) } }
            $arguments += @('--build-arg', "BUILD_VERSION=$buildVersion", '--build-arg', "BUILD_INFORMATIONAL_VERSION=$informationalVersion", '-f', $definition.DockerfilePath)
            if ($NoCache) { $arguments += '--no-cache' }
            $arguments += @('--load', '.')
            & docker @arguments
            if ($LASTEXITCODE -ne 0) { Fail-Build "Docker build failed for '$target' on '$platform'." }
            Write-Success "$($definition.DisplayName) built for ${platform}: $($platformImages -join ', ')"
        }
    }
}
finally {
    Pop-Location
}

Write-Header 'Build complete'
