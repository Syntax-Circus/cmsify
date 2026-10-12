[CmdletBinding()]
param([ValidateSet('Windows','Linux')][string]$Platform='Windows')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$destination=Join-Path $repo ('artifacts/file-source/'+$Platform.ToLowerInvariant()+'-'+[Guid]::NewGuid().ToString('N'))
$snapshot=Join-Path $destination 'source'
$image='mcr.microsoft.com/dotnet/sdk@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Set-Location $repo
$files=@(& rtk proxy git ls-files --cached --others --exclude-standard)
if($LASTEXITCODE -ne 0){throw 'Source inventory failed.'}
$files=$files | Where-Object { $_ -notmatch '^\[rtk\]' -and (Test-Path -LiteralPath $_ -PathType Leaf) }
$manifest=foreach($file in $files){@{ path=$file;sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash }}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $destination 'source-identities.json')
$operations=[Collections.Generic.List[object]]::new()
function Invoke-Check([string]$Name,[string]$Command,[string[]]$Arguments)
{
    $output=(& rtk proxy $Command @Arguments 2>&1 | Out-String);$code=$LASTEXITCODE
    [IO.File]::WriteAllText((Join-Path $destination ($Name+'.log')),$output)
    $operations.Add(@{ name=$Name;exitCode=$code;arguments=$Arguments })
    Write-Output ($output.Split([Environment]::NewLine) | Select-Object -Last 15)
    if($code -ne 0){throw "$Name failed: $code"}
}
try
{
    if($Platform -eq 'Linux')
    {
        New-Item -ItemType Directory -Path $snapshot -Force | Out-Null
        foreach($file in $files)
        {
            $target=Join-Path $snapshot $file
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
            Copy-Item -LiteralPath $file -Destination $target
        }
        $copied=foreach($entry in $manifest){@{ path=$entry.path;sha256=(Get-FileHash -LiteralPath (Join-Path $snapshot $entry.path) -Algorithm SHA256).Hash }}
        $copied | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $destination 'linux-source-identities.json')
        foreach($entry in $manifest){if(($copied | Where-Object path -eq $entry.path).sha256 -ne $entry.sha256){throw 'Source identity mismatch.'}}
        $prefix=@('run','--rm','--platform','linux/amd64','--mount',"type=bind,source=$snapshot,target=/work",'--mount','type=bind,source=/var/run/docker.sock,target=/var/run/docker.sock',
            '--env','TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal','--env','NUGET_PACKAGES=/work/.qualification-packages','--env','DOTNET_CLI_HOME=/work/.qualification-dotnet','--workdir','/work',$image)
        Invoke-Check 'sdk' 'docker' ($prefix+@('dotnet','--info'))
        $lockedAssets='-p:CustomAfterMicrosoftCommonTargets=/work/scripts/qualification/locked-assets.targets'
        Invoke-Check 'restore' 'docker' ($prefix+@('dotnet','restore','Cmsify.slnx','--locked-mode',$lockedAssets))
        Invoke-Check 'build' 'docker' ($prefix+@('dotnet','build','Cmsify.slnx','-c','Release','--no-restore','--no-incremental','--verbosity','minimal','-m:1',$lockedAssets))
        Invoke-Check 'tests' 'docker' ($prefix+@('dotnet','test','--solution','Cmsify.slnx','-c','Release','--no-build','--max-parallel-test-modules','1','--minimum-expected-tests','1',$lockedAssets))
    }
    else
    {
        Invoke-Check 'sdk' 'dotnet' @('--info')
        Invoke-Check 'restore' 'dotnet' @('restore','Cmsify.slnx','--locked-mode')
        Invoke-Check 'build' 'dotnet' @('build','Cmsify.slnx','-c','Release','--no-restore','--no-incremental','--verbosity','minimal','-m:1')
        Invoke-Check 'tests' 'dotnet' @('test','--solution','Cmsify.slnx','-c','Release','--no-build','--max-parallel-test-modules','1','--minimum-expected-tests','1')
    }
    @{ platform=$Platform;passed=$true;image=$image;sourceCount=$manifest.Count;operations=$operations } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'evidence.json')
    Write-Output "PASS source qualification: $destination"
}
catch
{
    @{ platform=$Platform;passed=$false;image=$image;sourceCount=$manifest.Count;operations=$operations;failure=$_.Exception.Message } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'evidence.json')
    throw
}
