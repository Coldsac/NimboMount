param(
    [Parameter(Mandatory = $true)][string]$Destination,
    [Parameter(Mandatory = $true)][string]$Url,
    [Parameter(Mandatory = $true)][string]$ExpectedSha256
)

$ErrorActionPreference = "Stop"
$expected = $ExpectedSha256.ToUpperInvariant()

function Get-Sha([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToUpperInvariant()
}

$directory = Split-Path -Parent $Destination
if (-not (Test-Path -LiteralPath $directory)) {
    New-Item -ItemType Directory -Path $directory | Out-Null
}

if (Test-Path -LiteralPath $Destination) {
    $have = Get-Sha $Destination
    if ($have -eq $expected) {
        Write-Host "WinFsp installer already matches $expected"
        exit 0
    }

    Write-Host "WinFsp installer hash is $have. Downloading the official file again."
    Remove-Item -LiteralPath $Destination -Force
}

Write-Host "Downloading $Url"
Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing
$have = Get-Sha $Destination
if ($have -ne $expected) {
    Remove-Item -LiteralPath $Destination -Force
    throw "WinFsp installer SHA-256 is $have, expected $expected. The file was not packed."
}

Write-Host "WinFsp installer SHA-256 matches $expected"
