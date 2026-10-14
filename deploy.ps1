<#
.SYNOPSIS
    MuYing Drive Fast Deployment Shortcut

.EXAMPLE
    .\deploy.ps1 -Client
    .\deploy.ps1 -Backend
    .\deploy.ps1 -All
    .\deploy.ps1 -Client -Installer
#>
param(
    [switch]$Backend,
    [switch]$Client,
    [switch]$All,
    [switch]$Installer,
    [string]$Version = ""
)

$argsList = @()
if ($Backend) { $argsList += "--backend" }
if ($Client) { $argsList += "--client" }
if ($All) { $argsList += "--all" }
if ($Installer) { $argsList += "--installer" }
if (-not [string]::IsNullOrWhiteSpace($Version)) { $argsList += @("--version", $Version) }

if ($argsList.Count -eq 0) {
    Write-Host "Usage: .\deploy.ps1 [-Client] [-Backend] [-All] [-Installer] [-Version <x.y.z>]" -ForegroundColor Yellow
    exit 0
}

python (Join-Path $PSScriptRoot "scripts\deploy.py") @argsList
