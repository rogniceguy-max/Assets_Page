<#
.SYNOPSIS
    Builds the first-install setup executable (MuYingDrive.Setup-<version>.exe).

.DESCRIPTION
    Publishes the same self-contained client bundle that publish-update.ps1 packages,
    embeds it into desktop/MuYingDrive.Bootstrap, renders the NAS client configuration
    the bootstrap seeds on first install, and produces a single per-user, no-UAC
    setup executable.

    The payload (desktop/MuYingDrive.Bootstrap/Payload) is gitignored: it is roughly
    150 MB and must never be committed.

.PARAMETER NasClientConfigTemplate
    A JSON file with ApiBaseUri, CertificateSha256, ShareRoot and (optionally)
    CredentialProfile. Keep it on the release machine and out of the repository.

.PARAMETER TlsCertificatePath
    Optional. When supplied, CertificateSha256 is recomputed from this certificate so
    the shipped pin cannot drift from what the NAS actually serves.

.EXAMPLE
    ./publish-installer.ps1 -Version 1.0.0 -NasClientConfigTemplate D:\release\nas-client.template.json `
        -TlsCertificatePath D:\release\server.crt -OutputDirectory .\dist
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')][string]$Version,
    [Parameter(Mandatory = $true)][string]$NasClientConfigTemplate,
    [string]$TlsCertificatePath = "",
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    # Optional: signing the setup executable is independent of the update trust anchor.
    [string]$CodeSigningCertificateSha1 = "",
    [string]$TimestampServer = "",
    [string]$SignToolPath = "signtool.exe"
)

$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "PowerShell 7 or later is required."
}

. (Join-Path $PSScriptRoot "publish-common.ps1")

$bootstrapProject = Join-Path $PSScriptRoot "MuYingDrive.Bootstrap\MuYingDrive.Bootstrap.csproj"
$payloadDirectory = Join-Path $PSScriptRoot "MuYingDrive.Bootstrap\Payload"
$payloadZip = Join-Path $payloadDirectory "client-payload.zip"
$payloadSettings = Join-Path $payloadDirectory "nas-client.json"
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)

if (-not (Test-Path -LiteralPath $NasClientConfigTemplate -PathType Leaf)) {
    throw "The NAS client configuration template was not found: $NasClientConfigTemplate"
}

$trust = Get-ReleaseTrustState -DesktopDirectory $PSScriptRoot
$signingCertificate = Resolve-ReleaseSigningCertificate `
    -CodeSigningCertificateSha1 $CodeSigningCertificateSha1 `
    -TrustedThumbprint $trust.PublisherThumbprint

$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("MuYingSetup-" + [guid]::NewGuid().ToString("N"))
$clientPublish = Join-Path $stagingRoot "client"
$setupPublish = Join-Path $stagingRoot "setup"
try {
    Publish-ClientBundle -DesktopDirectory $PSScriptRoot -Version $Version -Destination $clientPublish `
        -SigningCertificate $signingCertificate -TimestampServer $TimestampServer -SignToolPath $SignToolPath

    # --- Render the configuration the bootstrap will seed -------------------------
    $configuration = [System.IO.File]::ReadAllText($NasClientConfigTemplate) | ConvertFrom-Json
    if (-not [string]::IsNullOrWhiteSpace($TlsCertificatePath)) {
        $configuration.CertificateSha256 = Get-TlsCertificatePin -CertificatePath $TlsCertificatePath
    }
    foreach ($required in @("ApiBaseUri", "CertificateSha256", "ShareRoot")) {
        if ([string]::IsNullOrWhiteSpace($configuration.$required)) {
            throw "The NAS client configuration template is missing '$required'."
        }
    }
    if ($configuration.CertificateSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        throw "CertificateSha256 must be a 64-character SHA-256 fingerprint; the client rejects anything else at startup."
    }
    if ($configuration.ApiBaseUri -notmatch '^https://') {
        throw "ApiBaseUri must be HTTPS; the client refuses any other scheme."
    }

    # --- Build the embedded payload ----------------------------------------------
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Path $payloadDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $payloadZip -PathType Leaf) { Remove-Item -LiteralPath $payloadZip -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $clientPublish, $payloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    [System.IO.File]::WriteAllText(
        $payloadSettings,
        ($configuration | ConvertTo-Json -Depth 4),
        (New-Object System.Text.UTF8Encoding($false)))

    # --- Build the setup executable ----------------------------------------------
    # The payload resources are picked up by the Exists() conditions in the csproj, so
    # this publish must run after the files above are written.
    New-Item -ItemType Directory -Path $setupPublish -Force | Out-Null
    & dotnet publish $bootstrapProject -c Release -r win-x64 --self-contained true -p:Version=$Version -o $setupPublish
    if ($LASTEXITCODE -ne 0) { throw "The setup executable publish failed." }

    $setupExecutable = Join-Path $setupPublish "MuYingDrive.Setup.exe"
    if (-not (Test-Path -LiteralPath $setupExecutable -PathType Leaf)) {
        throw "The setup executable was not produced."
    }
    $setupVersion = (Get-Item -LiteralPath $setupExecutable).VersionInfo.ProductVersion
    if ($null -eq $setupVersion -or $setupVersion.Split('+')[0] -ne $Version) {
        throw "Setup version '$setupVersion' does not match the requested version '$Version'."
    }
    if ($null -ne $signingCertificate) {
        & $SignToolPath sign /sha1 $signingCertificate.Thumbprint /fd SHA256 /tr $TimestampServer /td SHA256 $setupExecutable
        if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed for the setup executable." }
    }

    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    $destination = Join-Path $outputRoot ("MuYingDrive.Setup-" + $Version + ".exe")
    Copy-Item -LiteralPath $setupExecutable -Destination $destination -Force
    $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()

    Write-Output "Created first-install setup executable for version $Version."
    Write-Output "  File:    $destination"
    Write-Output "  SHA-256: $hash"
    Write-Output ""
    Write-Output "The setup executable is not Authenticode-signed unless a certificate was supplied."
    Write-Output "Publish that SHA-256 alongside the download so employees can verify it."
}
finally {
    $resolvedStaging = [System.IO.Path]::GetFullPath($stagingRoot)
    $resolvedTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedStaging.StartsWith($resolvedTempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedStaging)) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
