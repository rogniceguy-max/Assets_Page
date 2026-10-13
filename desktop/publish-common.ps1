<#
    Shared release build steps.

    Dot-source this from publish-update.ps1 and publish-installer.ps1 so the update
    package and the setup payload are produced by the same code path. Two divergent
    build paths are how a package and an installer end up containing different bytes.
#>

function Get-ReleaseTrustState {
    <#
        Reads the embedded trust anchors and validates the Authenticode pin shape.
        A pin that is present but unparsable would make every shipped client fail
        closed on an anchor it cannot read, so it is rejected here instead.
    #>
    param([Parameter(Mandatory = $true)][string]$DesktopDirectory)

    $publisherKeyPath = Join-Path $DesktopDirectory "MuYingDrive.UpdateCore\Trust\trusted-update-publisher.pem"
    $publisherThumbprintPath = Join-Path $DesktopDirectory "MuYingDrive.UpdateCore\Trust\trusted-code-signing-thumbprint.txt"
    if (-not (Test-Path -LiteralPath $publisherKeyPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $publisherThumbprintPath -PathType Leaf)) {
        throw "The embedded update trust anchor files are missing."
    }

    $configured = (Get-Content -LiteralPath $publisherThumbprintPath |
        Where-Object { $_ -notmatch '^\s*#' } | Select-Object -First 1)
    $thumbprint = if ($null -ne $configured -and $configured.Trim() -match '^[0-9A-Fa-f]{64}$') {
        $configured.Trim().ToUpperInvariant()
    } else { "" }
    if ($thumbprint.Length -ne 0 -and $thumbprint.Length -ne 64) {
        throw "The embedded Authenticode publisher thumbprint is present but is not a 64-character SHA-256 value."
    }

    [pscustomobject]@{ PublisherKeyPath = $publisherKeyPath; PublisherThumbprint = $thumbprint }
}

function Resolve-ReleaseSigningCertificate {
    <#
        Returns the code-signing certificate, or $null when Authenticode signing is
        intentionally skipped. The ECDSA manifest signature and the package SHA-256
        remain the trust anchor in that case.
    #>
    param(
        [string]$CodeSigningCertificateSha1 = "",
        # An empty value means no Authenticode pin is embedded, which is a valid state,
        # so this cannot be Mandatory (PowerShell rejects binding an empty string).
        [AllowEmptyString()][string]$TrustedThumbprint = ""
    )

    $sha1 = ($CodeSigningCertificateSha1 -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($sha1.Length -eq 0) {
        if ($TrustedThumbprint.Length -eq 64) {
            # The client verifies Authenticode whenever a pin is embedded, so shipping
            # an unsigned package would be rejected on every machine.
            throw "A publisher pin is embedded but no -CodeSigningCertificateSha1 was supplied; the client would reject this unsigned package."
        }
        return $null
    }
    if ($sha1.Length -ne 40) {
        throw "The supplied code-signing certificate SHA-1 thumbprint is not 40 hex characters."
    }
    if ($TrustedThumbprint.Length -ne 64) {
        throw "A signing certificate was supplied but no 64-character publisher pin is embedded; write the thumbprint into Trust/trusted-code-signing-thumbprint.txt first."
    }

    $certificate = Get-ChildItem -Path Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $sha1 } | Select-Object -First 1
    if ($null -eq $certificate) {
        throw "The Authenticode certificate was not found in the current user's certificate store."
    }
    $sha256 = $certificate.GetCertHashString([System.Security.Cryptography.HashAlgorithmName]::SHA256).ToUpperInvariant()
    if ($sha256 -ne $TrustedThumbprint) {
        throw "The Authenticode certificate does not match the embedded publisher thumbprint."
    }
    return $certificate
}

function Publish-ClientBundle {
    <#
        Publishes the self-contained client with the updater merged into .\Updater,
        optionally Authenticode-signs both executables, and asserts that the published
        version matches the requested one. The client reports AssemblyVersion, the
        updater parses ProductVersion and the update health marker is compared as a
        string, so a mismatch would break updates silently.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$DesktopDirectory,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$Destination,
        $SigningCertificate,
        [string]$TimestampServer = "",
        [string]$SignToolPath = "signtool.exe"
    )

    $clientProject = Join-Path $DesktopDirectory "MuYingDrive.NativeWpf\MuYingDrive.NativeWpf.csproj"
    $updaterProject = Join-Path $DesktopDirectory "MuYingDrive.Updater\MuYingDrive.Updater.csproj"
    $updaterPublish = Join-Path ([System.IO.Path]::GetTempPath()) ("MuYingUpdater-" + [guid]::NewGuid().ToString("N"))

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    New-Item -ItemType Directory -Path $updaterPublish -Force | Out-Null
    try {
        & dotnet publish $clientProject -c Release -r win-x64 --self-contained true -p:Version=$Version -o $Destination
        if ($LASTEXITCODE -ne 0) { throw "The native WPF client publish failed." }
        & dotnet publish $updaterProject -c Release -r win-x64 --self-contained true -p:Version=$Version -o $updaterPublish
        if ($LASTEXITCODE -ne 0) { throw "The native WPF updater publish failed." }

        $updaterOutput = Join-Path $Destination "Updater"
        New-Item -ItemType Directory -Path $updaterOutput -Force | Out-Null
        Copy-Item -Path (Join-Path $updaterPublish "*") -Destination $updaterOutput -Recurse -Force

        # The client reads nas-client.json from the per-user settings directory, and the
        # bootstrap is its only writer. Keeping it out of the bundle also keeps
        # machine-specific values out of the update package.
        $clientConfig = Join-Path $Destination "nas-client.json"
        if (Test-Path -LiteralPath $clientConfig -PathType Leaf) {
            Remove-Item -LiteralPath $clientConfig -Force
        }

        foreach ($executable in @(
                (Join-Path $Destination "MuYingDrive.NativeWpf.exe"),
                (Join-Path $updaterOutput "MuYingDrive.Updater.exe")
            )) {
            if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
                throw "A required WPF executable is missing from the publish output."
            }
            if ($null -ne $SigningCertificate) {
                & $SignToolPath sign /sha1 $SigningCertificate.Thumbprint /fd SHA256 /tr $TimestampServer /td SHA256 $executable
                if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed for a WPF executable." }
            }
            $publishedVersion = (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion
            if ($null -eq $publishedVersion -or $publishedVersion.Split('+')[0] -ne $Version) {
                throw "Publish output version '$publishedVersion' does not match the requested version '$Version'."
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $updaterPublish) { Remove-Item -LiteralPath $updaterPublish -Recurse -Force }
    }
}

function Get-TlsCertificatePin {
    <#
        Returns the SHA-256 pin the Windows client stores for a TLS certificate.
        Deriving it from the deployed certificate means the pin cannot drift from
        what the NAS actually serves.
    #>
    param([Parameter(Mandatory = $true)][string]$CertificatePath)

    if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
        throw "The TLS certificate was not found: $CertificatePath"
    }
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath)
    return $certificate.GetCertHashString([System.Security.Cryptography.HashAlgorithmName]::SHA256)
}
