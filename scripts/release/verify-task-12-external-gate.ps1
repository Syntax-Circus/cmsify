[CmdletBinding(DefaultParameterSetName = "Gate")]
param(
    [Parameter(Mandatory = $true, ParameterSetName = "Gate")]
    [ValidateSet(
        "public-package-restore",
        "hosted-accessibility",
        "protected-approvals",
        "artifact-attestation",
        "registry-signing",
        "immutable-oci-promotion",
        "hosted-smoke-soak",
        "final-release"
    )]
    [string] $Gate,

    [Parameter(Mandatory = $true, ParameterSetName = "Inputs")]
    [switch] $ListInputs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Repository = "Syntax-Circus/cmsify"
$PublishWorkflowPath = ".github/workflows/publish-cmsify.yml"
$AttestationSignerWorkflow = "Syntax-Circus/cmsify/.github/workflows/publish-cmsify.yml"
$SoakWorkflowPath = ".github/workflows/record-release-soak.yml"
$SoakAttestationSignerWorkflow = "Syntax-Circus/cmsify/.github/workflows/record-release-soak.yml"

$GateInputs = [ordered]@{
    "public-package-restore"   = @()
    "hosted-accessibility"    = @("CMSIFY_RELEASE_RUN_ID", "CMSIFY_RELEASE_SOURCE_SHA", "CMSIFY_RELEASE_TAG", "CMSIFY_ACCESSIBILITY_JOB_ID")
    "protected-approvals"     = @("CMSIFY_RELEASE_RUN_ID", "CMSIFY_RELEASE_SOURCE_SHA", "CMSIFY_RELEASE_TAG", "CMSIFY_PROMOTE_JOB_ID")
    "artifact-attestation"   = @("CMSIFY_CHECKSUMS_PATH", "CMSIFY_RELEASE_VERSION", "CMSIFY_RELEASE_SOURCE_SHA", "CMSIFY_ATTESTATION_SIGNER_WORKFLOW")
    "registry-signing"        = @("CMSIFY_API_DIGEST", "CMSIFY_ADMIN_DIGEST", "CMSIFY_RELEASE_TAG", "CMSIFY_COSIGN_CERTIFICATE_IDENTITY")
    "immutable-oci-promotion" = @("CMSIFY_RELEASE_VERSION", "CMSIFY_API_DIGEST", "CMSIFY_ADMIN_DIGEST")
    "hosted-smoke-soak"       = @("CMSIFY_RELEASE_RUN_ID", "CMSIFY_RELEASE_SOURCE_SHA", "CMSIFY_RELEASE_TAG", "CMSIFY_SMOKE_JOB_ID", "CMSIFY_UPGRADE_ROLLBACK_JOB_ID", "CMSIFY_SOAK_EVIDENCE_PATH", "CMSIFY_SOAK_EVIDENCE_SHA256", "CMSIFY_SOAK_RECORDER_RUN_ID", "CMSIFY_SOAK_RECORDER_SOURCE_SHA", "CMSIFY_SOAK_ATTESTATION_SIGNER_WORKFLOW")
    "final-release"           = @("CMSIFY_RELEASE_VERSION", "CMSIFY_RELEASE_TAG", "CMSIFY_RELEASE_SOURCE_SHA")
}

if ($ListInputs) {
    @($GateInputs.Values | ForEach-Object { $_ } | Sort-Object -Unique) | ConvertTo-Json -Compress
    exit 0
}

function Get-RequiredEnvironmentValue {
    param([Parameter(Mandatory = $true)][string] $Name)

    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Required immutable input $Name is unset."
    }

    return $value
}

$Inputs = @{}
foreach ($name in $GateInputs[$Gate]) {
    $Inputs[$name] = Get-RequiredEnvironmentValue -Name $name
}

function Assert-FullSourceSha {
    param([Parameter(Mandatory = $true)][string] $Value)
    if ($Value -cnotmatch "^[0-9a-f]{40}$") { throw "Source SHA must be 40 lowercase hexadecimal characters." }
}

function Assert-OciDigest {
    param([Parameter(Mandatory = $true)][string] $Value, [Parameter(Mandatory = $true)][string] $Label)
    if ($Value -cnotmatch "^sha256:[0-9a-f]{64}$") { throw "$Label must be an exact lowercase sha256 digest." }
}

function ConvertTo-ExactUtcTimestamp {
    param([Parameter(Mandatory = $true)][object] $Value, [Parameter(Mandatory = $true)][string] $Label)

    if ($Value -is [DateTimeOffset]) {
        $parsed = [DateTimeOffset] $Value
    }
    elseif ($Value -is [DateTime]) {
        $dateTime = [DateTime] $Value
        if ($dateTime.Kind -ne [DateTimeKind]::Utc) { throw "$Label must be UTC." }
        $parsed = [DateTimeOffset]::new($dateTime)
    }
    elseif ($Value -is [string] -and -not [string]::IsNullOrWhiteSpace($Value)) {
        $parsed = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse(
            $Value,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref] $parsed
        )) {
            throw "$Label must be a round-trip UTC timestamp."
        }
    }
    else {
        throw "$Label must be a nonempty UTC timestamp."
    }

    if ($parsed.Offset -ne [TimeSpan]::Zero) {
        throw "$Label must be a round-trip UTC timestamp."
    }

    return $parsed
}

function Invoke-CheckedNative {
    param(
        [Parameter(Mandatory = $true)][string] $FilePath,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [Parameter(Mandatory = $true)][string] $FailureMessage
    )

    try {
        $output = @(& $FilePath @Arguments)
        $exitCode = $LASTEXITCODE
    }
    catch {
        throw "$FailureMessage`: $($_.Exception.Message)"
    }

    if ($exitCode -ne 0) {
        throw "$FailureMessage (exit $exitCode)."
    }

    return $output
}

function Invoke-CheckedJson {
    param(
        [Parameter(Mandatory = $true)][string] $FilePath,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [Parameter(Mandatory = $true)][string] $FailureMessage
    )

    $output = Invoke-CheckedNative -FilePath $FilePath -Arguments $Arguments -FailureMessage $FailureMessage
    try {
        return (($output -join [Environment]::NewLine) | ConvertFrom-Json -Depth 100)
    }
    catch {
        throw "$FailureMessage returned invalid JSON."
    }
}

function Get-ReleaseRun {
    param([Parameter(Mandatory = $true)][string] $RunId)
    return Invoke-CheckedJson -FilePath "gh" -Arguments @("api", "repos/$Repository/actions/runs/$RunId") -FailureMessage "Release run query failed"
}

function Assert-ReleaseRunIdentity {
    param(
        [Parameter(Mandatory = $true)][object] $Run,
        [Parameter(Mandatory = $true)][string] $RunId,
        [Parameter(Mandatory = $true)][string] $SourceSha,
        [Parameter(Mandatory = $true)][string] $Tag
    )

    Assert-FullSourceSha -Value $SourceSha
    if ([string] $Run.id -ne $RunId -or
        $Run.event -ne "push" -or
        $Run.path -ne $PublishWorkflowPath -or
        $Run.head_repository.full_name -ne $Repository -or
        $Run.repository.full_name -ne $Repository -or
        $Run.head_sha -ne $SourceSha -or
        $Run.head_branch -ne $Tag) {
        throw "Release run workflow, event, tag, or source identity is invalid."
    }
}

function Get-ReleaseJobs {
    param([Parameter(Mandatory = $true)][string] $RunId)
    $response = Invoke-CheckedJson -FilePath "gh" -Arguments @("api", "repos/$Repository/actions/runs/$RunId/jobs?filter=latest&per_page=100") -FailureMessage "Release jobs query failed"
    return @($response.jobs)
}

function Get-ExactSuccessfulJob {
    param(
        [Parameter(Mandatory = $true)][object[]] $Jobs,
        [Parameter(Mandatory = $true)][string] $JobId,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $matches = @($Jobs | Where-Object { [string] $_.id -eq $JobId })
    if ($matches.Count -ne 1) { throw "Expected exactly one $Name job with immutable ID $JobId." }
    $job = $matches[0]
    if ($job.name -ne $Name -or $job.status -ne "completed" -or $job.conclusion -ne "success") {
        throw "$Name job identity or conclusion is invalid."
    }
    if (-not $job.started_at -or -not $job.completed_at) { throw "$Name job timestamps are missing." }
    return $job
}

function Assert-NoLinkComponents {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $rootFull = [IO.Path]::GetFullPath($Root)
    $pathFull = [IO.Path]::GetFullPath($Path)
    $relative = [IO.Path]::GetRelativePath($rootFull, $pathFull)
    if ([IO.Path]::IsPathRooted($relative) -or $relative -eq ".." -or $relative.StartsWith("..$([IO.Path]::DirectorySeparatorChar)") -or $relative.StartsWith("..$([IO.Path]::AltDirectorySeparatorChar)")) {
        throw "Candidate subject is outside the candidate root."
    }

    $current = $rootFull
    $separators = [char[]] @([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    foreach ($part in $relative.Split($separators, [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $part
        $item = Get-Item -LiteralPath $current -Force
        if ($item.LinkType -or (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Candidate path contains a symbolic link or reparse point."
        }
    }
}

function Assert-AttestationSignerWorkflow {
    param([Parameter(Mandatory = $true)][string] $Value)
    if ($Value -cne $AttestationSignerWorkflow) {
        throw "Attestation signer workflow locator is invalid."
    }
}

function Assert-SoakAttestationSignerWorkflow {
    param([Parameter(Mandatory = $true)][string] $Value)
    if ($Value -cne $SoakAttestationSignerWorkflow) {
        throw "Soak attestation signer workflow locator is invalid."
    }
}

function Assert-SoakRecorderRunIdentity {
    param(
        [Parameter(Mandatory = $true)][object] $Run,
        [Parameter(Mandatory = $true)][string] $RunId,
        [Parameter(Mandatory = $true)][string] $SourceSha
    )

    Assert-FullSourceSha -Value $SourceSha
    if ([string] $Run.id -ne $RunId -or
        $Run.event -ne "workflow_dispatch" -or
        $Run.path -ne $SoakWorkflowPath -or
        $Run.head_repository.full_name -ne $Repository -or
        $Run.repository.full_name -ne $Repository -or
        $Run.head_sha -ne $SourceSha -or
        $Run.head_branch -ne "main" -or
        $Run.status -ne "completed" -or
        $Run.conclusion -ne "success") {
        throw "Soak recorder run workflow, event, branch, source, or conclusion is invalid."
    }
}

function Invoke-AttestationVerification {
    param(
        [Parameter(Mandatory = $true)][string] $Subject,
        [Parameter(Mandatory = $true)][string] $SignerWorkflow,
        [Parameter(Mandatory = $true)][string] $SourceSha
    )

    [void] (Invoke-CheckedNative -FilePath "gh" -Arguments @(
        "attestation", "verify", $Subject,
        "--repo", $Repository,
        "--signer-workflow", $SignerWorkflow,
        "--source-digest", $SourceSha
    ) -FailureMessage "Attestation verification failed for $Subject")
}

function Test-PublicPackageRestore {
    $repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
    $evidencePath = Join-Path $repositoryRoot "docs/evidence/task-12-local-verification.json"
    try { $identity = (Get-Content -Raw -LiteralPath $evidencePath | ConvertFrom-Json -Depth 20).localFeedPackage } catch { throw "Tracked Task 12 package identity is missing or invalid." }
    $packageId = [string] $identity.id
    $packageVersion = [string] $identity.version
    $localUnsignedSha = [string] $identity.localUnsignedSha256
    $publicSignedSha = [string] $identity.publicSignedSha256
    $contentHash = [string] $identity.contentHash
    $expectedSignatureType = [string] $identity.expectedRepositorySignature.type
    $expectedServiceIndex = [string] $identity.expectedRepositorySignature.serviceIndex
    $expectedOwner = [string] $identity.expectedRepositorySignature.owner
    if ($packageId -cnotmatch "^[A-Za-z0-9][A-Za-z0-9._-]+$" -or
        $packageVersion -cnotmatch "^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$" -or
        $localUnsignedSha -cnotmatch "^[A-F0-9]{64}$" -or
        $publicSignedSha -cnotmatch "^[A-F0-9]{64}$" -or
        $contentHash -cnotmatch "^[A-Za-z0-9+/]{86}==$" -or
        $expectedSignatureType -cne "Repository" -or
        $expectedServiceIndex -cne "https://api.nuget.org/v3/index.json" -or
        $expectedOwner -cne "syntaxcircus") {
        throw "Tracked Task 12 package identity is incomplete or invalid."
    }

    $temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $temporaryRoot = [IO.Path]::GetFullPath((Join-Path $temporaryParent "cmsify-public-restore-$([Guid]::NewGuid().ToString('N'))"))
    $temporaryRootCreated = $false
    try {
        if ([IO.Path]::GetRelativePath($temporaryParent, $temporaryRoot).StartsWith("..") -or (Test-Path -LiteralPath $temporaryRoot)) {
            throw "Public restore temporary root is not an exact new child of the system temporary directory."
        }
        [void] (New-Item -ItemType Directory -Path $temporaryRoot)
        $temporaryRootCreated = $true
        $temporaryItem = Get-Item -LiteralPath $temporaryRoot -Force
        if ($temporaryItem.LinkType -or (($temporaryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Public restore temporary root must not be a symbolic link or reparse point."
        }

        $configPath = Join-Path $temporaryRoot "NuGet.Config"
        $packagesRoot = Join-Path $temporaryRoot "packages"
        $downloadRoot = Join-Path $temporaryRoot "download"
        [void] (New-Item -ItemType Directory -Path $packagesRoot)
        [void] (New-Item -ItemType Directory -Path $downloadRoot)
        $configContents = "<?xml version=`"1.0`" encoding=`"utf-8`"?>`n<configuration>`n  <packageSources>`n    <clear />`n    <add key=`"nuget.org`" value=`"https://api.nuget.org/v3/index.json`" protocolVersion=`"3`" />`n  </packageSources>`n</configuration>`n"
        [IO.File]::WriteAllText($configPath, $configContents, [Text.UTF8Encoding]::new($false))

        $lowerId = $packageId.ToLowerInvariant()
        $lowerVersion = $packageVersion.ToLowerInvariant()
        $nupkgName = "$lowerId.$lowerVersion.nupkg"
        $nupkgPath = Join-Path $downloadRoot $nupkgName
        $downloadUrl = "https://api.nuget.org/v3-flatcontainer/$lowerId/$lowerVersion/$nupkgName"
        [void] (Invoke-CheckedNative -FilePath "curl" -Arguments @("--fail", "--silent", "--show-error", "--location", "--proto", "=https", "--tlsv1.2", "--output", $nupkgPath, $downloadUrl) -FailureMessage "Exact public package download failed")
        if (-not (Test-Path -LiteralPath $nupkgPath -PathType Leaf)) { throw "Exact public package download produced no nupkg." }
        $downloadedSignedSha = (Get-FileHash -LiteralPath $nupkgPath -Algorithm SHA256).Hash
        if ($downloadedSignedSha -cne $publicSignedSha) { throw "Public package SHA-256 does not match the tracked repository-signed package identity." }
        $verificationOutput = Invoke-CheckedNative -FilePath "dotnet" -Arguments @("nuget", "verify", $nupkgPath, "--all", "--configfile", $configPath, "--verbosity", "normal", "--force-english-output") -FailureMessage "Public package signature verification failed"
        $verificationText = $verificationOutput -join [Environment]::NewLine
        $contentHashMatches = [Regex]::Matches($verificationText, "(?m)^[ `t]*Content hash:[ `t]*(?<value>\S+)[ `t`r]*$")
        $signatureTypeMatches = [Regex]::Matches($verificationText, "(?m)^[ `t]*Signature type:[ `t]*(?<value>\S+)[ `t`r]*$")
        $serviceIndexMatches = [Regex]::Matches($verificationText, "(?m)^[ `t]*Service index:[ `t]*(?<value>\S+)[ `t`r]*$")
        $ownerMatches = [Regex]::Matches($verificationText, "(?m)^[ `t]*Owners:[ `t]*(?<value>[^`r`n]+?)[ `t`r]*$")
        if ($contentHashMatches.Count -ne 1 -or $contentHashMatches[0].Groups["value"].Value -cne $contentHash) { throw "Public package content hash is invalid." }
        if ($signatureTypeMatches.Count -ne 1 -or $signatureTypeMatches[0].Groups["value"].Value -cne $expectedSignatureType) { throw "Public package signature type is invalid." }
        if ($serviceIndexMatches.Count -ne 1 -or $serviceIndexMatches[0].Groups["value"].Value -cne $expectedServiceIndex) { throw "Public package repository service index is invalid." }
        if ($ownerMatches.Count -ne 1 -or $ownerMatches[0].Groups["value"].Value -cne $expectedOwner) { throw "Public package repository owner is invalid." }

        $graphDefinitions = @(
            @{ Asset = "sdk/dotnet/src/SyntaxCircus.Cmsify.Client/obj/project.assets.json"; Lock = "sdk/dotnet/src/SyntaxCircus.Cmsify.Client/packages.lock.json"; LockType = "Direct" },
            @{ Asset = "sdk/dotnet/src/SyntaxCircus.Cmsify.Client.DistributedCaching/obj/project.assets.json"; Lock = "sdk/dotnet/src/SyntaxCircus.Cmsify.Client.DistributedCaching/packages.lock.json"; LockType = "CentralTransitive" },
            @{ Asset = "sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/obj/project.assets.json"; Lock = "sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/packages.lock.json"; LockType = "CentralTransitive" },
            @{ Asset = "src/Cmsify.Admin/obj/project.assets.json"; Lock = "src/Cmsify.Admin/packages.lock.json"; LockType = "CentralTransitive" },
            @{ Asset = "tests/Cmsify.Admin.Integration.Tests/obj/project.assets.json"; Lock = "tests/Cmsify.Admin.Integration.Tests/packages.lock.json"; LockType = "CentralTransitive" }
        )
        foreach ($definition in $graphDefinitions) {
            $lockPath = Join-Path $repositoryRoot $definition.Lock
            try { $lock = Get-Content -Raw -LiteralPath $lockPath | ConvertFrom-Json -Depth 100 } catch { throw "Public restore lock graph is missing or invalid JSON: $($definition.Lock)." }
            $lockEntries = @($lock.dependencies.PSObject.Properties | ForEach-Object { $_.Value.PSObject.Properties | Where-Object { $_.Name -ceq $packageId } })
            if ($lockEntries.Count -ne 1 -or
                $lockEntries[0].Value.type -isnot [string] -or $lockEntries[0].Value.type -cne $definition.LockType -or
                $lockEntries[0].Value.resolved -isnot [string] -or $lockEntries[0].Value.resolved -cne $packageVersion -or
                $lockEntries[0].Value.contentHash -isnot [string] -or $lockEntries[0].Value.contentHash -cne $contentHash) {
                throw "Public restore lock graph package identity is invalid: $($definition.Lock)."
            }
        }

        Push-Location $repositoryRoot
        try {
            [void] (Invoke-CheckedNative -FilePath "dotnet" -Arguments @("restore", "Cmsify.slnx", "--configfile", $configPath, "--packages", $packagesRoot, "--no-http-cache", "--locked-mode") -FailureMessage "Public locked restore failed")
        }
        finally { Pop-Location }
        $restoredNupkgPath = Join-Path $packagesRoot "$lowerId/$lowerVersion/$nupkgName"
        if (-not (Test-Path -LiteralPath $restoredNupkgPath -PathType Leaf)) { throw "Fresh package cache does not contain the exact restored nupkg." }
        $restoredPackageSha = (Get-FileHash -LiteralPath $restoredNupkgPath -Algorithm SHA256).Hash
        if ($restoredPackageSha -cne $downloadedSignedSha) { throw "Fresh package cache nupkg bytes do not match the verified public download." }

        $libraryKey = "$packageId/$packageVersion"
        $expectedPackagePath = "$lowerId/$lowerVersion"
        $dotnetCommand = Get-Command dotnet -ErrorAction Stop
        $dotnetPath = [IO.Path]::GetFullPath([string] $dotnetCommand.Source)
        $resolvedDotnet = [IO.File]::ResolveLinkTarget($dotnetPath, $true)
        if ($resolvedDotnet) { $dotnetPath = $resolvedDotnet.FullName }
        $trustedLibraryPacks = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($dotnetPath)) "library-packs")).TrimEnd('\', '/')
        $sourceComparison = if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
        $verified = @()
        foreach ($definition in $graphDefinitions) {
            $relativePath = $definition.Asset
            $assetPath = Join-Path $repositoryRoot $relativePath
            if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) { throw "Public restore did not produce required asset graph $relativePath." }
            try { $assets = Get-Content -Raw -LiteralPath $assetPath | ConvertFrom-Json -Depth 100 } catch { throw "Public restore asset graph is invalid JSON: $relativePath." }
            $matches = @($assets.libraries.PSObject.Properties | Where-Object { $_.Name -ceq $libraryKey })
            if ($matches.Count -ne 1) { throw "Public restore asset graph must contain exactly one exact package identity: $relativePath." }
            $library = $matches[0].Value
            if ($library.type -isnot [string] -or $library.type -cne "package" -or
                $library.path -isnot [string] -or $library.path -cne $expectedPackagePath -or
                $library.sha512 -isnot [string] -or $library.sha512 -cne $contentHash) {
                throw "Public restore asset graph package type, path, or content hash is invalid: $relativePath."
            }
            $packageFolders = @($assets.packageFolders.PSObject.Properties.Name)
            if ($packageFolders.Count -ne 1 -or [IO.Path]::GetFullPath($packageFolders[0]).TrimEnd('\', '/') -cne $packagesRoot.TrimEnd('\', '/')) {
                throw "Public restore asset graph did not use the exact fresh packages directory: $relativePath."
            }
            $configFiles = @($assets.project.restore.configFilePaths)
            if ($configFiles.Count -ne 1 -or [IO.Path]::GetFullPath([string] $configFiles[0]) -cne [IO.Path]::GetFullPath($configPath)) {
                throw "Public restore asset graph did not use the exact public-only NuGet configuration: $relativePath."
            }
            $sources = @($assets.project.restore.sources.PSObject.Properties.Name)
            $publicSources = @($sources | Where-Object { $_ -ceq "https://api.nuget.org/v3/index.json" })
            $unexpectedSources = @($sources | Where-Object {
                if ($_ -ceq "https://api.nuget.org/v3/index.json") { return $false }
                try {
                    $sourcePath = [IO.Path]::GetFullPath([string] $_).TrimEnd('\', '/')
                    return (-not $sourceComparison.Equals($sourcePath, $trustedLibraryPacks))
                }
                catch { return $true }
            })
            if ($publicSources.Count -ne 1 -or $unexpectedSources.Count -ne 0) {
                throw "Public restore asset graph contains a non-public or unexpected package source: $relativePath."
            }
            $verified += $relativePath
        }
        if ($verified.Count -ne 5) { throw "Public restore must verify exactly five affected asset graphs." }
    }
    finally {
        if ($temporaryRootCreated -and (Test-Path -LiteralPath $temporaryRoot)) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
    }
}

function Test-HostedAccessibility {
    $run = Get-ReleaseRun -RunId $Inputs.CMSIFY_RELEASE_RUN_ID
    Assert-ReleaseRunIdentity -Run $run -RunId $Inputs.CMSIFY_RELEASE_RUN_ID -SourceSha $Inputs.CMSIFY_RELEASE_SOURCE_SHA -Tag $Inputs.CMSIFY_RELEASE_TAG
    $jobs = Get-ReleaseJobs -RunId $Inputs.CMSIFY_RELEASE_RUN_ID
    [void] (Get-ExactSuccessfulJob -Jobs $jobs -JobId $Inputs.CMSIFY_ACCESSIBILITY_JOB_ID -Name "candidate-accessibility")
}

function Test-ProtectedApprovals {
    $runId = $Inputs.CMSIFY_RELEASE_RUN_ID
    $sourceSha = $Inputs.CMSIFY_RELEASE_SOURCE_SHA
    $tag = $Inputs.CMSIFY_RELEASE_TAG
    $run = Get-ReleaseRun -RunId $runId
    Assert-ReleaseRunIdentity -Run $run -RunId $runId -SourceSha $sourceSha -Tag $tag
    if ($run.status -ne "completed" -or $run.conclusion -ne "success") { throw "Approved release run is not successfully completed." }

    $jobs = Get-ReleaseJobs -RunId $runId
    $promote = Get-ExactSuccessfulJob -Jobs $jobs -JobId $Inputs.CMSIFY_PROMOTE_JOB_ID -Name "promote"
    $runCreated = ConvertTo-ExactUtcTimestamp -Value $run.created_at -Label "Release run creation"
    $promoteStarted = ConvertTo-ExactUtcTimestamp -Value $promote.started_at -Label "Promote job start"
    $promoteCompleted = ConvertTo-ExactUtcTimestamp -Value $promote.completed_at -Label "Promote job completion"

    $protection = Invoke-CheckedJson -FilePath "gh" -Arguments @("api", "repos/$Repository/environments/release") -FailureMessage "Release environment protection query failed"
    $reviewerRules = @($protection.protection_rules | Where-Object { $_.type -eq "required_reviewers" })
    $configuredReviewers = @($reviewerRules | ForEach-Object { @($_.reviewers) } | Where-Object {
        $_.reviewer -and (-not [string]::IsNullOrWhiteSpace($_.reviewer.login) -or -not [string]::IsNullOrWhiteSpace($_.reviewer.slug))
    })
    if ($reviewerRules.Count -ne 1 -or $configuredReviewers.Count -eq 0) {
        throw "Release environment does not have configured required reviewers."
    }

    $approvals = @(Invoke-CheckedJson -FilePath "gh" -Arguments @("api", "repos/$Repository/actions/runs/$runId/approvals") -FailureMessage "Release approval query failed")
    $approved = @($approvals | Where-Object {
        $_.state -eq "approved" -and
        $_.user -and -not [string]::IsNullOrWhiteSpace($_.user.login) -and
        @($_.environments | Where-Object { $_.name -eq "release" }).Count -gt 0
    })
    if ($approved.Count -eq 0) { throw "No approved release-environment review history exists for the exact run." }

    $deployments = @(Invoke-CheckedJson -FilePath "gh" -Arguments @("api", "repos/$Repository/deployments?environment=release&sha=$sourceSha&per_page=100") -FailureMessage "Release deployment query failed")
    $successfulDeployment = $false
    $expectedJobUrl = "https://github.com/$Repository/actions/runs/$runId/job/$($Inputs.CMSIFY_PROMOTE_JOB_ID)"
    foreach ($deployment in $deployments) {
        if ($deployment.sha -ne $sourceSha -or $deployment.ref -ne $tag -or $deployment.environment -ne "release") { continue }
        $created = ConvertTo-ExactUtcTimestamp -Value $deployment.created_at -Label "Deployment timestamp"
        if ($created -lt $runCreated -or $created -gt $promoteCompleted) { continue }
        $statuses = @(Invoke-CheckedJson -FilePath "gh" -Arguments @("api", "repos/$Repository/deployments/$($deployment.id)/statuses?per_page=100") -FailureMessage "Release deployment-status query failed")
        $latest = $statuses | Sort-Object created_at -Descending | Select-Object -First 1
        if ($latest -and $latest.state -eq "success" -and $latest.log_url -eq $expectedJobUrl) {
            $statusTime = ConvertTo-ExactUtcTimestamp -Value $latest.created_at -Label "Deployment status timestamp"
            if ($statusTime -ge $promoteStarted -and $statusTime -le [DateTimeOffset]::UtcNow.AddMinutes(5)) {
                $successfulDeployment = $true
                break
            }
        }
    }
    if (-not $successfulDeployment) { throw "No SHA-, tag-, run-, and time-bound successful release deployment exists." }
}

function Test-ArtifactAttestation {
    $checksumsPath = $Inputs.CMSIFY_CHECKSUMS_PATH
    $sourceSha = $Inputs.CMSIFY_RELEASE_SOURCE_SHA
    $signerWorkflow = $Inputs.CMSIFY_ATTESTATION_SIGNER_WORKFLOW
    Assert-FullSourceSha -Value $sourceSha
    Assert-AttestationSignerWorkflow -Value $signerWorkflow
    $version = $Inputs.CMSIFY_RELEASE_VERSION
    if ($version -cnotmatch "^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$") { throw "Release version is invalid." }
    if (-not (Test-Path -LiteralPath $checksumsPath -PathType Leaf)) { throw "SHA256SUMS file is missing." }

    $checksumsItem = Get-Item -LiteralPath $checksumsPath -Force
    if ($checksumsItem.LinkType -or (($checksumsItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "SHA256SUMS must not be a symbolic link or reparse point."
    }
    $rootItem = Get-Item -LiteralPath $checksumsItem.Directory.FullName -Force
    if ($rootItem.LinkType -or (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Candidate trust root must not be a symbolic link or reparse point."
    }
    $root = [IO.Path]::GetFullPath($rootItem.FullName)
    $trustedAnchor = [IO.Path]::GetPathRoot($root)
    Assert-NoLinkComponents -Root $trustedAnchor -Path $root
    $manifestPath = Join-Path $root "release-manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Release manifest is missing." }
    Assert-NoLinkComponents -Root $root -Path $manifestPath
    try { $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json -Depth 100 } catch { throw "Release manifest is invalid JSON." }
    if ($manifest.version -isnot [string] -or $manifest.version -cne $version -or
        $manifest.sourceSha -isnot [string] -or $manifest.sourceSha -cne $sourceSha) {
        throw "Release manifest version or source SHA does not match immutable gate inputs."
    }
    $artifactVerifier = Join-Path $PSScriptRoot "verify-release-artifacts.mjs"
    [void] (Invoke-CheckedNative -FilePath "node" -Arguments @($artifactVerifier, "--artifacts", $root, "--version", $version, "--source-sha", $sourceSha) -FailureMessage "Complete release candidate verification failed")

    $comparison = if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
    $seen = [Collections.Generic.HashSet[string]]::new($comparison)
    $subjects = @()

    foreach ($line in Get-Content -LiteralPath $checksumsPath) {
        if ($line -cnotmatch "^(?<hash>[A-Fa-f0-9]{64})\s+\*?(?<subject>[^\\/].*)$") { throw "Invalid SHA256SUMS entry." }
        $expectedHash = $Matches.hash.ToLowerInvariant()
        $subject = $Matches.subject
        if ([IO.Path]::IsPathRooted($subject) -or $subject.Contains(":") -or $subject -match "(^|[\\/])\.\.([\\/]|$)" -or -not $seen.Add($subject)) {
            throw "SHA256SUMS subject is rooted, traverses, aliases a stream, or duplicates."
        }
        $path = [IO.Path]::GetFullPath((Join-Path $root $subject))
        Assert-NoLinkComponents -Root $root -Path $path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "SHA256SUMS subject is missing." }
        $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -cne $expectedHash) { throw "SHA256SUMS digest mismatch for $subject." }
        $subjects += $path
    }
    if ($subjects.Count -eq 0) { throw "SHA256SUMS contains no subjects." }
    foreach ($subject in $subjects) {
        Invoke-AttestationVerification -Subject $subject -SignerWorkflow $signerWorkflow -SourceSha $sourceSha
    }
}

function Test-RegistrySigning {
    $apiDigest = $Inputs.CMSIFY_API_DIGEST
    $adminDigest = $Inputs.CMSIFY_ADMIN_DIGEST
    $tag = $Inputs.CMSIFY_RELEASE_TAG
    $identity = $Inputs.CMSIFY_COSIGN_CERTIFICATE_IDENTITY
    Assert-OciDigest -Value $apiDigest -Label "API digest"
    Assert-OciDigest -Value $adminDigest -Label "Admin digest"
    if ($tag -cnotmatch "^v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$") { throw "Release tag is invalid." }
    $expectedIdentity = "https://github.com/$AttestationSignerWorkflow@refs/tags/$tag"
    if ($identity -cne $expectedIdentity) { throw "Cosign certificate identity is invalid." }
    foreach ($subject in @("docker.io/syntaxcircus/cmsify-api@$apiDigest", "docker.io/syntaxcircus/cmsify-admin@$adminDigest")) {
        [void] (Invoke-CheckedNative -FilePath "cosign" -Arguments @("verify", "--certificate-identity", $identity, "--certificate-oidc-issuer", "https://token.actions.githubusercontent.com", $subject) -FailureMessage "Cosign verification failed for $subject")
    }
}

function Test-ImmutableOciPromotion {
    $version = $Inputs.CMSIFY_RELEASE_VERSION
    $apiExpected = $Inputs.CMSIFY_API_DIGEST
    $adminExpected = $Inputs.CMSIFY_ADMIN_DIGEST
    if ($version -cnotmatch "^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$") { throw "Release version is invalid." }
    Assert-OciDigest -Value $apiExpected -Label "API digest"
    Assert-OciDigest -Value $adminExpected -Label "Admin digest"

    foreach ($candidate in @(
        @{ Name = "API"; Image = "cmsify-api"; Expected = $apiExpected },
        @{ Name = "Admin"; Image = "cmsify-admin"; Expected = $adminExpected }
    )) {
        $descriptor = Invoke-CheckedJson -FilePath "oras" -Arguments @("manifest", "fetch", "--descriptor", "docker.io/syntaxcircus/$($candidate.Image):$version") -FailureMessage "$($candidate.Name) descriptor fetch failed"
        $digest = [string] $descriptor.digest
        Assert-OciDigest -Value $digest -Label "$($candidate.Name) descriptor digest"
        if ($digest -cne $candidate.Expected) { throw "$($candidate.Name) digest mismatch." }
    }
}

function Assert-ExactJsonBoolean {
    param([Parameter(Mandatory = $true)][object] $Value, [Parameter(Mandatory = $true)][string] $Label)
    if ($Value -isnot [bool] -or $Value -ne $true) { throw "$Label must be the JSON boolean true." }
}

function Test-HostedSmokeSoak {
    $runId = $Inputs.CMSIFY_RELEASE_RUN_ID
    $sourceSha = $Inputs.CMSIFY_RELEASE_SOURCE_SHA
    $tag = $Inputs.CMSIFY_RELEASE_TAG
    $soakSignerWorkflow = $Inputs.CMSIFY_SOAK_ATTESTATION_SIGNER_WORKFLOW
    $soakRecorderRunId = $Inputs.CMSIFY_SOAK_RECORDER_RUN_ID
    $soakRecorderSourceSha = $Inputs.CMSIFY_SOAK_RECORDER_SOURCE_SHA
    Assert-SoakAttestationSignerWorkflow -Value $soakSignerWorkflow
    $run = Get-ReleaseRun -RunId $runId
    Assert-ReleaseRunIdentity -Run $run -RunId $runId -SourceSha $sourceSha -Tag $tag
    $jobs = Get-ReleaseJobs -RunId $runId
    $smoke = Get-ExactSuccessfulJob -Jobs $jobs -JobId $Inputs.CMSIFY_SMOKE_JOB_ID -Name "artifact-smoke"
    $upgrade = Get-ExactSuccessfulJob -Jobs $jobs -JobId $Inputs.CMSIFY_UPGRADE_ROLLBACK_JOB_ID -Name "upgrade-rollback"
    $smokeCompleted = ConvertTo-ExactUtcTimestamp -Value $smoke.completed_at -Label "Smoke job completion"
    $upgradeCompleted = ConvertTo-ExactUtcTimestamp -Value $upgrade.completed_at -Label "Upgrade/rollback job completion"
    $jobsCompleted = if ($smokeCompleted -gt $upgradeCompleted) { $smokeCompleted } else { $upgradeCompleted }
    $soakRecorderRun = Get-ReleaseRun -RunId $soakRecorderRunId
    Assert-SoakRecorderRunIdentity -Run $soakRecorderRun -RunId $soakRecorderRunId -SourceSha $soakRecorderSourceSha
    $soakRecorderCreated = ConvertTo-ExactUtcTimestamp -Value $soakRecorderRun.created_at -Label "Soak recorder run creation"
    if ($soakRecorderCreated -lt $jobsCompleted) { throw "Soak recorder run predates the exact release jobs." }

    $soakPath = $Inputs.CMSIFY_SOAK_EVIDENCE_PATH
    if (-not (Test-Path -LiteralPath $soakPath -PathType Leaf)) { throw "Soak evidence is missing." }
    $soakItem = Get-Item -LiteralPath $soakPath -Force
    if ($soakItem.LinkType -or (($soakItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw "Soak evidence must not be a symbolic link or reparse point." }
    $expectedHash = $Inputs.CMSIFY_SOAK_EVIDENCE_SHA256
    if ($expectedHash -cnotmatch "^[0-9a-f]{64}$") { throw "Soak evidence SHA-256 is invalid." }
    $actualHash = (Get-FileHash -LiteralPath $soakPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $expectedHash) { throw "Soak evidence SHA-256 mismatch." }
    Invoke-AttestationVerification -Subject ([IO.Path]::GetFullPath($soakPath)) -SignerWorkflow $soakSignerWorkflow -SourceSha $soakRecorderSourceSha

    try { $soak = Get-Content -Raw -LiteralPath $soakPath | ConvertFrom-Json -Depth 20 } catch { throw "Soak evidence is not valid JSON." }
    if ($soak.schema -cne "cmsify.hosted-soak-evidence.v1" -or
        $soak.releaseRunId -isnot [string] -or $soak.releaseRunId -cne $runId -or
        $soak.releaseTag -isnot [string] -or $soak.releaseTag -cne $tag -or
        $soak.sourceSha -isnot [string] -or $soak.sourceSha -cne $sourceSha -or
        $soak.smokeJobId -isnot [string] -or $soak.smokeJobId -cne $Inputs.CMSIFY_SMOKE_JOB_ID -or
        $soak.upgradeRollbackJobId -isnot [string] -or $soak.upgradeRollbackJobId -cne $Inputs.CMSIFY_UPGRADE_ROLLBACK_JOB_ID -or
        $soak.soakRecorderRunId -isnot [string] -or $soak.soakRecorderRunId -cne $soakRecorderRunId -or
        $soak.soakRecorderSourceSha -isnot [string] -or $soak.soakRecorderSourceSha -cne $soakRecorderSourceSha) {
        throw "Soak evidence schema or immutable identity is invalid."
    }
    Assert-ExactJsonBoolean -Value $soak.smokePassed -Label "smokePassed"
    Assert-ExactJsonBoolean -Value $soak.upgradeRollbackPassed -Label "upgradeRollbackPassed"
    Assert-ExactJsonBoolean -Value $soak.passed -Label "passed"
    $started = ConvertTo-ExactUtcTimestamp -Value $soak.startedAtUtc -Label "Soak start"
    $completed = ConvertTo-ExactUtcTimestamp -Value $soak.completedAtUtc -Label "Soak completion"
    $now = [DateTimeOffset]::UtcNow
    if ($started -lt $jobsCompleted -or $completed -lt $soakRecorderCreated -or $completed -le $started -or ($completed - $started).TotalMinutes -lt 60 -or $completed -gt $now.AddMinutes(5) -or ($now - $completed).TotalHours -gt 24) {
        throw "Soak evidence is stale, future-dated, shorter than 60 minutes, or not bound after the exact jobs."
    }
}

function Resolve-ReleaseTagSource {
    param([Parameter(Mandatory = $true)][string] $Tag)

    $object = (Invoke-CheckedJson -FilePath "gh" -Arguments @("api", "repos/$Repository/git/ref/tags/$Tag") -FailureMessage "Release tag query failed").object
    for ($depth = 0; $depth -lt 5 -and $object.type -eq "tag"; $depth++) {
        $object = (Invoke-CheckedJson -FilePath "gh" -Arguments @("api", "repos/$Repository/git/tags/$($object.sha)") -FailureMessage "Annotated release tag query failed").object
    }
    if ($object.type -ne "commit" -or [string]::IsNullOrWhiteSpace($object.sha)) { throw "Release tag does not resolve to a commit." }
    return [string] $object.sha
}

function Test-FinalRelease {
    $version = $Inputs.CMSIFY_RELEASE_VERSION
    $tag = $Inputs.CMSIFY_RELEASE_TAG
    $sourceSha = $Inputs.CMSIFY_RELEASE_SOURCE_SHA
    Assert-FullSourceSha -Value $sourceSha
    if ($version -cnotmatch "^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$" -or $tag -cne "v$version") {
        throw "Final release requires an exact stable SemVer tag."
    }

    $release = Invoke-CheckedJson -FilePath "gh" -Arguments @("release", "view", $tag, "--repo", $Repository, "--json", "tagName,isDraft,isPrerelease,publishedAt,url") -FailureMessage "Release query failed"
    if ($release.tagName -cne $tag -or
        $release.isDraft -isnot [bool] -or $release.isDraft -ne $false -or
        $release.isPrerelease -isnot [bool] -or $release.isPrerelease -ne $false -or
        -not $release.publishedAt) {
        throw "Release tag, source, publication, draft, or prerelease state is invalid."
    }
    [void] (ConvertTo-ExactUtcTimestamp -Value $release.publishedAt -Label "Release publication")
    $resolvedSource = Resolve-ReleaseTagSource -Tag $tag
    if ($resolvedSource -cne $sourceSha) { throw "Release tag does not resolve to the exact source SHA." }
}

switch ($Gate) {
    "public-package-restore"   { Test-PublicPackageRestore }
    "hosted-accessibility"    { Test-HostedAccessibility }
    "protected-approvals"     { Test-ProtectedApprovals }
    "artifact-attestation"    { Test-ArtifactAttestation }
    "registry-signing"        { Test-RegistrySigning }
    "immutable-oci-promotion" { Test-ImmutableOciPromotion }
    "hosted-smoke-soak"       { Test-HostedSmokeSoak }
    "final-release"           { Test-FinalRelease }
}

Write-Output "TASK12_EXTERNAL_GATE_OK $Gate"
