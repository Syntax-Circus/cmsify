[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')][string]$PackageVersion,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory)][string]$PackageDirectory,
    [string]$DotnetPath = 'dotnet',
    [ValidateSet('Both', 'Windows', 'Linux')][string]$Platform = 'Both',
    [switch]$PublicPackages
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$feed = [IO.Path]::GetFullPath($PackageDirectory)
$evidence = Join-Path $repo ('artifacts/photo-qualification/' + $PackageVersion + '-' + [Guid]::NewGuid().ToString('N'))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$stage = [IO.Path]::GetFullPath((Join-Path $tempRoot ('cmsify-photo-qualification-' + [Guid]::NewGuid().ToString('N'))))
$pg = 'cmsify-photo-pg-' + [Guid]::NewGuid().ToString('N')
$network = 'cmsify-photo-network-' + [Guid]::NewGuid().ToString('N')
$sdkImage = 'mcr.microsoft.com/dotnet/sdk:10.0.400'
$password = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$connection = "Host=$pg;Database=cmsify_consumer;Username=cmsify;Password=$password"
$operations = [Collections.Generic.List[object]]::new()
$sequence = 0
$oldPath = $env:PATH
$oldPhoto = $env:CMSIFY_PHOTO_QUALIFICATION
$startedPg = $false
$startedNetwork = $false
function Invoke-Recorded([string]$Command, [string[]]$Arguments) {
    $script:sequence++
    $output = (& rtk proxy $Command @Arguments 2>&1 | Out-String)
    $code = $LASTEXITCODE
    $safe = $output.Replace($password, '[fixture-password]').Replace($connection, '[fixture-connection]')
    $name = '{0:D3}-{1}.log' -f $script:sequence, ([IO.Path]::GetFileNameWithoutExtension($Command))
    [IO.File]::WriteAllText((Join-Path $evidence $name), $safe)
    $script:operations.Add(@{ command = [IO.Path]::GetFileName($Command); arguments = $Arguments; exitCode = $code; log = $name })
    if ($code -ne 0) { Write-Output ($safe.Split([Environment]::NewLine) | Select-Object -Last 20); throw "$Command failed with exit code $code." }
    return $safe.Trim()
}
try {
    New-Item -ItemType Directory -Path $evidence,$stage,$feed -Force | Out-Null
    Set-Location $repo
    $actualCommit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualCommit -ne $SourceCommit) { throw 'SourceCommit must equal this checkout HEAD.' }
    & git diff --quiet HEAD -- src/Cmsify.Core src/Cmsify.Infrastructure
    if ($LASTEXITCODE -ne 0) { throw 'Candidate engine source must be committed.' }
    $sdk = Invoke-Recorded $DotnetPath @('--version')
    if ($sdk -notmatch '(?m)^10\.0\.400\s*$') { throw 'Qualification requires SDK10.0.400.' }
    foreach ($project in @('Core','Infrastructure')) {
        if ($PublicPackages) {
            $id = "syntaxcircus.cmsify.$($project.ToLowerInvariant())"
            $target = Join-Path $feed "SyntaxCircus.Cmsify.$project.$PackageVersion.nupkg"
            if (Test-Path -LiteralPath $target) { throw 'Public package download requires a fresh destination.' }
            Invoke-Recorded 'curl.exe' @('--fail','--silent','--show-error','--location',"https://api.nuget.org/v3-flatcontainer/$id/$PackageVersion/$id.$PackageVersion.nupkg",'--output',$target) | Out-Null
        }
        else {
            Invoke-Recorded $DotnetPath @('pack', "src/Cmsify.$project/Cmsify.$project.csproj", '--configuration','Release',
                '-p:CmsifyReleaseBuild=true', "-p:Version=$PackageVersion", "-p:PackageVersion=$PackageVersion",
                "-p:RepositoryCommit=$SourceCommit", '--output',$feed, '--verbosity','quiet') | Out-Null
        }
    }
    if ([IO.Path]::IsPathRooted($DotnetPath)) { $env:PATH = [IO.Path]::GetDirectoryName($DotnetPath) + [IO.Path]::PathSeparator + $oldPath }
    $env:CMSIFY_PHOTO_QUALIFICATION = '1'
    if ($Platform -in @('Both','Windows')) {
        if (-not $IsWindows) { throw 'Windows qualification must run on Windows.' }
        Invoke-Recorded 'node' @('scripts/release/verify-engine-packages.mjs', '--version',$PackageVersion,
            '--source-sha',$SourceCommit,'--packages',$feed,'--postgres-only','--evidence',(Join-Path $evidence 'windows')) | Out-Null
    }
    if ($Platform -in @('Both','Linux')) {
        $consumer = Join-Path $stage 'consumer'; New-Item -ItemType Directory -Path $consumer | Out-Null
        Copy-Item -Path (Join-Path $repo 'tests/package-consumer/*.cs') -Destination $consumer
        $projectText = [IO.File]::ReadAllText((Join-Path $repo 'tests/package-consumer/EngineConsumer.csproj')).Replace('__PACKAGE_VERSION__',$PackageVersion)
        [IO.File]::WriteAllText((Join-Path $consumer 'EngineConsumer.csproj'), $projectText)
        [IO.File]::WriteAllText((Join-Path $consumer 'NuGet.Config'), @'
<configuration>
  <packageSources><clear/><add key="candidate" value="/feed"/><add key="public" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <packageSourceMapping><packageSource key="candidate"><package pattern="SyntaxCircus.Cmsify.*"/></packageSource><packageSource key="public"><package pattern="*"/></packageSource></packageSourceMapping>
  <fallbackPackageFolders><clear/></fallbackPackageFolders>
</configuration>
'@)
        $envFile = Join-Path $stage 'fixture.env'
        [IO.File]::WriteAllText($envFile, "POSTGRES_DB=cmsify_consumer`nPOSTGRES_USER=cmsify`nPOSTGRES_PASSWORD=$password`nCMSIFY_CONSUMER_POSTGRES=$connection`nCMSIFY_PHOTO_QUALIFICATION=1`n")
        if ($IsWindows) { Invoke-Recorded 'icacls' @($envFile,'/inheritance:r','/grant:r',"$($env:USERNAME):(F)") | Out-Null }
        Invoke-Recorded 'docker' @('network','create',$network) | Out-Null; $startedNetwork = $true
        Invoke-Recorded 'docker' @('run','--detach','--rm','--name',$pg,'--network',$network,'--env-file',$envFile,'postgres:17-alpine') | Out-Null; $startedPg = $true
        $ready = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            & docker exec $pg pg_isready -U cmsify -d cmsify_consumer *> $null
            if ($LASTEXITCODE -eq 0) { $ready = $true; break }; Start-Sleep -Seconds 1
        }
        if (-not $ready) { throw 'Disposable PostgreSQL did not become ready.' }
        $common = @('run','--rm','--platform','linux/amd64','--network',$network,'--env-file',$envFile,
            '--mount',"type=bind,source=$consumer,target=/work",'--mount',"type=bind,source=$feed,target=/feed,readonly",
            '--workdir','/work',$sdkImage)
        Invoke-Recorded 'docker' ($common + @('dotnet','--info')) | Out-Null
        Invoke-Recorded 'docker' ($common + @('dotnet','restore','EngineConsumer.csproj','--configfile','NuGet.Config','--no-cache','--use-lock-file','--packages','packages')) | Out-Null
        Invoke-Recorded 'docker' ($common + @('dotnet','restore','EngineConsumer.csproj','--configfile','NuGet.Config','--no-cache','--locked-mode','--packages','packages')) | Out-Null
        $assets = Get-Content (Join-Path $consumer 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
        if ($assets.libraries.Values.type -contains 'project') { throw 'Linux consumer resolved a project reference.' }
        foreach ($id in @('SyntaxCircus.Cmsify.Core','SyntaxCircus.Cmsify.Infrastructure')) {
            if ($assets.libraries["$id/$PackageVersion"].type -ne 'package') { throw 'Linux candidate package identity missing.' }
        }
        Invoke-Recorded 'docker' ($common + @('dotnet','build','EngineConsumer.csproj','--configuration','Release','--no-restore')) | Out-Null
        $licenseHash = (Get-FileHash (Join-Path $repo 'LICENSE') -Algorithm SHA256).Hash
        Invoke-Recorded 'docker' ($common + @('dotnet','run','--project','EngineConsumer.csproj','--configuration','Release','--no-build','--no-restore',
            '--','/feed',$PackageVersion,$SourceCommit,$licenseHash)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $consumer 'packages.lock.json'),(Join-Path $consumer 'obj/project.assets.json') -Destination $evidence
    }
    $packages = foreach ($id in @('SyntaxCircus.Cmsify.Core','SyntaxCircus.Cmsify.Infrastructure')) {
        $file = Join-Path $feed "$id.$PackageVersion.nupkg"
        @{ id = $id; version = $PackageVersion; sha256 = (Get-FileHash $file -Algorithm SHA256).Hash }
    }
    $consumerHashes = Get-ChildItem (Join-Path $repo 'tests/package-consumer') -File | ForEach-Object {
        @{ name = $_.Name; sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
    }
    @{ scope='private-upload-intake-and-synthetic-processing-foundation-only'; publicPackages=[bool]$PublicPackages; version=$PackageVersion; sourceCommit=$SourceCommit; packageDirectory=$feed;
       requestedPlatform=$Platform; sdk=$sdk; packages=$packages; consumerFiles=$consumerHashes; operations=$operations; passed=$true } |
        ConvertTo-Json -Depth 10 | Set-Content (Join-Path $evidence 'evidence.json')
    Write-Output "PASS generic embedded photo package qualification: $evidence"
}
finally {
    if ($startedPg) { & docker rm --force $pg *> $null }
    if ($startedNetwork) { & docker network rm $network *> $null }
    $env:PATH = $oldPath; $env:CMSIFY_PHOTO_QUALIFICATION = $oldPhoto
    # Resolve and check the one generated temp root before recursive removal. Never remove either checkout or the feed.
    if ([IO.Path]::GetDirectoryName($stage) -ne $tempRoot -or [IO.Path]::GetFileName($stage) -notlike 'cmsify-photo-qualification-*') {
        throw 'Unsafe cleanup target.'
    }
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
