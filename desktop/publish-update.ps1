[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')][string]$Version,
    [Parameter(Mandatory = $true)][string]$CodeSigningCertificateSha1,
    [Parameter(Mandatory = $true)][string]$ManifestSigningPrivateKeyPem,
    [Parameter(Mandatory = $true)][string]$TimestampServer,
    [Parameter(Mandatory = $true)][string]$ReleaseDirectory,
    [string]$SignToolPath = "signtool.exe"
)

$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "PowerShell 7 or later is required to import the release signing key."
}

$clientProject = Join-Path $PSScriptRoot "MuYingDrive.NativeWpf\MuYingDrive.NativeWpf.csproj"
$updaterProject = Join-Path $PSScriptRoot "MuYingDrive.Updater\MuYingDrive.Updater.csproj"
$publisherKeyPath = Join-Path $PSScriptRoot "MuYingDrive.UpdateCore\Trust\trusted-update-publisher.pem"
$publisherThumbprintPath = Join-Path $PSScriptRoot "MuYingDrive.UpdateCore\Trust\trusted-code-signing-thumbprint.txt"
$manifestSigningPrivateKeyPem = [System.IO.Path]::GetFullPath($ManifestSigningPrivateKeyPem)
$releaseRoot = [System.IO.Path]::GetFullPath($ReleaseDirectory)

if (-not (Test-Path -LiteralPath $ManifestSigningPrivateKeyPem -PathType Leaf)) {
    throw "Manifest signing private key was not found."
}
if (-not (Test-Path -LiteralPath $publisherKeyPath -PathType Leaf) -or -not (Test-Path -LiteralPath $publisherThumbprintPath -PathType Leaf)) {
    throw "The embedded update trust anchor files are missing."
}

$trustedThumbprintText = (Get-Content -LiteralPath $publisherThumbprintPath | Where-Object { $_ -notmatch '^\s*#' } | Select-Object -First 1)
$trustedThumbprint = if ($null -ne $trustedThumbprintText -and $trustedThumbprintText.Trim() -match '^[0-9A-Fa-f]{64}$') { $trustedThumbprintText.Trim().ToUpperInvariant() } else { "" }
$signingCertSha1 = ($CodeSigningCertificateSha1 -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
if ($trustedThumbprint.Length -ne 64 -or $signingCertSha1.Length -ne 40) {
    throw "Configure the 64-character SHA-256 publisher trust anchor and provide the signing certificate SHA-1 store thumbprint."
}

$signingCertificate = Get-ChildItem -Path Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $signingCertSha1 } | Select-Object -First 1
if ($null -eq $signingCertificate) {
    throw "The Authenticode certificate was not found in the current user's certificate store."
}
$certificateSha256 = $signingCertificate.GetCertHashString([System.Security.Cryptography.HashAlgorithmName]::SHA256).ToUpperInvariant()
if ($certificateSha256 -ne $trustedThumbprint) {
    throw "The Authenticode certificate does not match the embedded publisher thumbprint."
}

$privateKey = [System.Security.Cryptography.ECDsa]::Create()
$publicKey = [System.Security.Cryptography.ECDsa]::Create()
try {
    $privateKey.ImportFromPem([System.IO.File]::ReadAllText($manifestSigningPrivateKeyPem))
    $publicKey.ImportFromPem([System.IO.File]::ReadAllText($publisherKeyPath))
    $privatePublic = [Convert]::ToBase64String($privateKey.ExportSubjectPublicKeyInfo())
    $embeddedPublic = [Convert]::ToBase64String($publicKey.ExportSubjectPublicKeyInfo())
    if ($privatePublic -ne $embeddedPublic) {
        throw "The manifest private key does not match the public key embedded in the applications."
    }
}
catch {
    $privateKey.Dispose()
    $publicKey.Dispose()
    throw
}

$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("MuYingUpdate-" + [guid]::NewGuid().ToString("N"))
$clientPublish = Join-Path $stagingRoot "client"
$updaterPublish = Join-Path $stagingRoot "updater"
New-Item -ItemType Directory -Path $clientPublish -Force | Out-Null
New-Item -ItemType Directory -Path $updaterPublish -Force | Out-Null

try {
    & dotnet publish $clientProject -c Release -r win-x64 --self-contained true -p:Version=$Version -o $clientPublish
    if ($LASTEXITCODE -ne 0) { throw "The native WPF client publish failed." }
    & dotnet publish $updaterProject -c Release -r win-x64 --self-contained true -p:Version=$Version -o $updaterPublish
    if ($LASTEXITCODE -ne 0) { throw "The native WPF updater publish failed." }

    $updaterOutput = Join-Path $clientPublish "Updater"
    New-Item -ItemType Directory -Path $updaterOutput -Force | Out-Null
    Copy-Item -Path (Join-Path $updaterPublish "*") -Destination $updaterOutput -Recurse -Force
    $clientConfig = Join-Path $clientPublish "nas-client.json"
    if (Test-Path -LiteralPath $clientConfig -PathType Leaf) {
        Remove-Item -LiteralPath $clientConfig -Force
    }

    foreach ($executable in @(
        (Join-Path $clientPublish "MuYingDrive.NativeWpf.exe"),
        (Join-Path $updaterOutput "MuYingDrive.Updater.exe")
    )) {
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "A required WPF executable is missing from the publish output." }
        & $SignToolPath sign /sha1 $signingCertificate.Thumbprint /fd SHA256 /tr $TimestampServer /td SHA256 $executable
        if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed for a WPF executable." }
    }

    $packageId = [guid]::NewGuid().ToString("N")
    $packagePath = Join-Path $stagingRoot ($packageId + ".zip")
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $clientPublish,
        $packagePath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false
    )

    $packageInfo = Get-Item -LiteralPath $packagePath
    $sha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $publishedAt = [DateTimeOffset]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
    $manifestPayload = [ordered]@{
        schema_version = 1
        channel = "stable"
        version = $Version
        platform = "windows"
        architecture = "x64"
        package_id = $packageId
        package_size = [long]$packageInfo.Length
        sha256 = $sha256
        published_at_utc = $publishedAt
    }
    $payloadBytes = [System.Text.Encoding]::UTF8.GetBytes(($manifestPayload | ConvertTo-Json -Compress))
    $signature = $privateKey.SignData(
        $payloadBytes,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence
    )
    $envelope = [ordered]@{
        payload = [Convert]::ToBase64String($payloadBytes)
        signature = [Convert]::ToBase64String($signature)
    }
    $envelopeBytes = [System.Text.Encoding]::UTF8.GetBytes(($envelope | ConvertTo-Json -Compress))

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
}
finally {
    $privateKey.Dispose()
    $publicKey.Dispose()
    $resolvedStaging = [System.IO.Path]::GetFullPath($stagingRoot)
    $resolvedTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedStaging.StartsWith($resolvedTempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedStaging)) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
