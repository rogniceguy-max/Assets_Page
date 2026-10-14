<#
.SYNOPSIS
    Builds, packages and signs a Windows x64 update release.

.DESCRIPTION
    Publishes the self-contained client with the updater merged in, zips it under a
    fresh opaque package id, signs the manifest with the offline ECDSA release key,
    and writes the package plus an atomically replaced stable.manifest.json into the
    release directory.

    Authenticode signing of the executables is OPTIONAL. When it is skipped the
    update trust anchor is the ECDSA manifest signature plus the whole-package
    SHA-256. See desktop/publish-common.ps1.

    This script does NOT deploy to the NAS and does NOT create a first-install
    package; use desktop/publish-installer.ps1 for the setup executable.

.EXAMPLE
    ./publish-update.ps1 -Version 1.1.0 -ManifestSigningPrivateKeyPem D:\offline\muying-release-signing.key -ReleaseDirectory \\nas\releases
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')][string]$Version,
    # Optional: leave empty to skip Authenticode signing entirely.
    [string]$CodeSigningCertificateSha1 = "",
    [Parameter(Mandatory = $true)][string]$ManifestSigningPrivateKeyPem,
    [string]$TimestampServer = "",
    [Parameter(Mandatory = $true)][string]$ReleaseDirectory,
    [string]$SignToolPath = "signtool.exe"
)

$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "PowerShell 7 or later is required to import the release signing key."
}

. (Join-Path $PSScriptRoot "publish-common.ps1")

$manifestSigningPrivateKeyPem = [System.IO.Path]::GetFullPath($ManifestSigningPrivateKeyPem)
$releaseRoot = [System.IO.Path]::GetFullPath($ReleaseDirectory)
if (-not (Test-Path -LiteralPath $manifestSigningPrivateKeyPem -PathType Leaf)) {
    throw "Manifest signing private key was not found."
}

$trust = Get-ReleaseTrustState -DesktopDirectory $PSScriptRoot
$signingCertificate = Resolve-ReleaseSigningCertificate `
    -CodeSigningCertificateSha1 $CodeSigningCertificateSha1 `
    -TrustedThumbprint $trust.PublisherThumbprint

# The release private key must match the public key embedded in the clients. If it
# does not, every shipped client would reject the manifest this script produces.
$privateKey = [System.Security.Cryptography.ECDsa]::Create()
try {
    $privateKey.ImportFromPem([System.IO.File]::ReadAllText($manifestSigningPrivateKeyPem))
    $embeddedKey = [System.Security.Cryptography.ECDsa]::Create()
    try {
        $embeddedKey.ImportFromPem([System.IO.File]::ReadAllText($trust.PublisherKeyPath))
        $privatePublic = [Convert]::ToBase64String($privateKey.ExportSubjectPublicKeyInfo())
        $embeddedPublic = [Convert]::ToBase64String($embeddedKey.ExportSubjectPublicKeyInfo())
        if ($privatePublic -ne $embeddedPublic) {
            throw "The manifest private key does not match the public key embedded in the applications."
        }
    }
    finally { $embeddedKey.Dispose() }

    $stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("MuYingUpdate-" + [guid]::NewGuid().ToString("N"))
    $clientPublish = Join-Path $stagingRoot "client"
    try {
        Publish-ClientBundle -DesktopDirectory $PSScriptRoot -Version $Version -Destination $clientPublish `
            -SigningCertificate $signingCertificate -TimestampServer $TimestampServer -SignToolPath $SignToolPath
        if ($null -eq $signingCertificate) {
            Write-Output "Authenticode signing skipped: no publisher pin is configured. The ECDSA manifest signature and the package SHA-256 remain the trust anchor."
        }

        $packageId = [guid]::NewGuid().ToString("N")
        $packagePath = Join-Path $stagingRoot ($packageId + ".zip")
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $clientPublish,
            $packagePath,
            [System.IO.Compression.CompressionLevel]::Fastest,
            $false
        )

        $packageInfo = Get-Item -LiteralPath $packagePath
        $sha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $publishedAt = [DateTimeOffset]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
        $manifestPayload = [ordered]@{
            schema_version   = 1
            channel          = "stable"
            version          = $Version
            platform         = "windows"
            architecture     = "x64"
            package_id       = $packageId
            package_size     = [long]$packageInfo.Length
            sha256           = $sha256
            published_at_utc = $publishedAt
        }
        $payloadBytes = [System.Text.Encoding]::UTF8.GetBytes(($manifestPayload | ConvertTo-Json -Compress))
        $signature = $privateKey.SignData(
            $payloadBytes,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence
        )
        $envelope = [ordered]@{
            payload   = [Convert]::ToBase64String($payloadBytes)
            signature = [Convert]::ToBase64String($signature)
        }
        $envelopeBytes = [System.Text.Encoding]::UTF8.GetBytes(($envelope | ConvertTo-Json -Compress))

        # A published package must never be overwritten in place: its SHA-256 is the
        # strong ETag, and clients resume downloads against it. Each release gets a
        # new opaque id, and the manifest is replaced atomically last.
        $packagesDirectory = Join-Path $releaseRoot "packages"
        New-Item -ItemType Directory -Path $packagesDirectory -Force | Out-Null
        $packageDestination = Join-Path $packagesDirectory ($packageId + ".zip")
        [System.IO.File]::Copy($packagePath, $packageDestination, $false)
        $manifestDestination = Join-Path $releaseRoot "stable.manifest.json"
        $manifestTemporary = $manifestDestination + ".tmp"
        [System.IO.File]::WriteAllBytes($manifestTemporary, $envelopeBytes)
        [System.IO.File]::Move($manifestTemporary, $manifestDestination, $true)

        Write-Output "Created signed Windows x64 package $packageId for version $Version."
        Write-Output "Package and stable manifest were written to the specified release directory."
        Write-Output "Copy them to the NAS release directory yourself; this script does not deploy."
    }
    finally {
        $resolvedStaging = [System.IO.Path]::GetFullPath($stagingRoot)
        $resolvedTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if ($resolvedStaging.StartsWith($resolvedTempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedStaging)) {
            Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
        }
    }
}
finally {
    $privateKey.Dispose()
}
