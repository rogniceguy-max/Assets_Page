<#
.SYNOPSIS
    Generates an ECDSA P-256 release signing key pair for the MuYingDrive updater.

.DESCRIPTION
    Writes the PUBLIC key into the embedded trust anchor that ships inside the
    client, and the PRIVATE key to an offline location outside the repository.
    The private key signs the update manifest; the client verifies that signature
    against the embedded public key. It is the root of trust for every update and
    must never enter the repository or the NAS.

    Rotation: run again with -Append to add a second public key. The client
    accepts a manifest signed by ANY embedded key, so the sequence is
    (1) append the next key and ship a release signed by the current key,
    (2) wait for clients to take it, (3) start signing with the new key,
    (4) drop the old key in a later release.

.EXAMPLE
    ./New-ReleaseSigningKey.ps1 -PrivateKeyPath D:\offline\muying-release.key
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PrivateKeyPath,
    [string]$PublicKeyPath = "",
    [switch]$Append,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# Resolved here rather than in the param default: $PSScriptRoot is not reliably
# populated while parameter defaults are evaluated.
$scriptDirectory = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PublicKeyPath)) {
    $PublicKeyPath = Join-Path $scriptDirectory "..\MuYingDrive.UpdateCore\Trust\trusted-update-publisher.pem"
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDirectory "..\.."))
$privateKeyFull = [System.IO.Path]::GetFullPath($PrivateKeyPath)
$publicKeyFull = [System.IO.Path]::GetFullPath($PublicKeyPath)

# The private key must never be committed: a leaked key lets anyone sign an update
# that every employee machine would accept.
if ($privateKeyFull.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to write the release private key inside the repository ($repoRoot). Choose an offline location."
}
if ((Test-Path -LiteralPath $privateKeyFull) -and -not $Force) {
    throw "The private key file already exists. Pass -Force to overwrite it (this makes the current key unrecoverable)."
}
if ((Test-Path -LiteralPath $publicKeyFull) -and -not $Append -and -not $Force) {
    throw "The embedded public key file already exists. Pass -Append to add a rotation key, or -Force to replace every embedded key."
}

# The curve must be named explicitly: ECDsa.Create() with no argument uses the
# platform default, which on Windows CNG produced P-521 here. The client pins
# KeySize == 256, so a different curve would be rejected at verification time.
$key = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    if ($key.KeySize -ne 256) { throw "Expected an ECDSA P-256 key but the provider produced $($key.KeySize) bits." }

    $publicPem = $key.ExportSubjectPublicKeyInfoPem()
    $privatePem = $key.ExportPkcs8PrivateKeyPem()
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)

    $privateDirectory = Split-Path -Parent $privateKeyFull
    if ($privateDirectory -and -not (Test-Path -LiteralPath $privateDirectory)) {
        New-Item -ItemType Directory -Path $privateDirectory -Force | Out-Null
    }
    [System.IO.File]::WriteAllText($privateKeyFull, $privatePem + [Environment]::NewLine, $utf8NoBom)

    $publicDirectory = Split-Path -Parent $publicKeyFull
    if ($publicDirectory -and -not (Test-Path -LiteralPath $publicDirectory)) {
        New-Item -ItemType Directory -Path $publicDirectory -Force | Out-Null
    }
    if ($Append -and (Test-Path -LiteralPath $publicKeyFull)) {
        # Preserve existing key blocks plus any explanatory comments, then add the
        # new block. LoadManifestPublicKeys reads every BEGIN/END block in order.
        $existing = ([System.IO.File]::ReadAllText($publicKeyFull)).TrimEnd()
        [System.IO.File]::WriteAllText($publicKeyFull, $existing + [Environment]::NewLine + $publicPem + [Environment]::NewLine, $utf8NoBom)
    }
    else {
        [System.IO.File]::WriteAllText($publicKeyFull, $publicPem + [Environment]::NewLine, $utf8NoBom)
    }

    $privateKeyHash = (Get-FileHash -LiteralPath $privateKeyFull -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Output "Generated an ECDSA P-256 release key pair."
    Write-Output "  Public key (committed, embedded): $publicKeyFull"
    Write-Output "  Private key (store offline):      $privateKeyFull"
    Write-Output "  Private key SHA-256:              $privateKeyHash"
    Write-Output "  SPKI (base64):                    $([Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo()))"
    Write-Output ""
    Write-Output "Next steps:"
    Write-Output "  1. Move the private key to offline/removable storage and back it up."
    Write-Output "  2. Rebuild and publish the client so the embedded public key ships."
    Write-Output "  3. Publish releases with publish-update.ps1 -ManifestSigningPrivateKeyPem <path>."
}
finally {
    $key.Dispose()
}
