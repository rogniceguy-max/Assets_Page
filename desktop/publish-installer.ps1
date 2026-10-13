<#
.SYNOPSIS
    Builds the first-install setup executable (MuYingDrive.Setup-<version>.exe).

.DESCRIPTION
    Publishes desktop/MuYingDrive.Bootstrap with the NAS client configuration baked in.
    The client bundle is NOT packaged here: the setup executable fetches the current
    release from the NAS and verifies it against the signed manifest before installing.
    That keeps the setup small and means the installed payload is authenticated, which
    an embedded archive could not be.

.PARAMETER NasClientConfigTemplate
    A JSON file with ApiBaseUri, CertificateSha256, ShareRoot and (optionally)
    CredentialProfile. Keep it on the release machine and out of the repository.

.PARAMETER TlsCertificatePath
    Optional. When supplied, CertificateSha256 is recomputed from this certificate so
    the shipped pin cannot drift from what the NAS actually serves.

.PARAMETER OutputDirectory
    Where the setup executable is copied.

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
$setupPublish = Join-Path $stagingRoot "setup"
try {
    New-Item -ItemType Directory -Path $setupPublish -Force | Out-Null

    # --- Render the configuration the setup executable carries and seeds -------------
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

    New-Item -ItemType Directory -Path $payloadDirectory -Force | Out-Null
    [System.IO.File]::WriteAllText(
        $payloadSettings,
        ($configuration | ConvertTo-Json -Depth 4),
        (New-Object System.Text.UTF8Encoding($false)))

    # The payload resource is picked up by the Exists() condition in the csproj, so this
    # publish must run after the file above is written.
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
    $sizeMb = [math]::Round((Get-Item -LiteralPath $destination).Length / 1MB, 1)
    $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()

    Write-Output "Created first-install setup executable for version $Version."
    Write-Output "  File:    $destination  ($sizeMb MB)"
    Write-Output "  SHA-256: $hash"
    Write-Output ""
    Write-Output "It fetches the current release from $($configuration.ApiBaseUri) and verifies it"
    Write-Output "against the signed manifest before installing. The setup executable is not"
    Write-Output "Authenticode-signed unless a certificate was supplied; publish the SHA-256 above."
}
finally {
    $resolvedStaging = [System.IO.Path]::GetFullPath($stagingRoot)
    $resolvedTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedStaging.StartsWith($resolvedTempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedStaging)) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
