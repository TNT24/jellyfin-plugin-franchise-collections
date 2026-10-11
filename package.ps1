# Builds the plugin, produces one installable zip per supported Jellyfin version in .\artifacts,
# and updates manifest.json (the plugin repository manifest).
$ErrorActionPreference = 'Stop'

$version = '1.0.0.0'
$changelog = 'Initial release'
$repo = 'TNT24/jellyfin-plugin-franchise-collections'
$project = Join-Path $PSScriptRoot 'Jellyfin.Plugin.FranchiseCollections'
$artifacts = Join-Path $PSScriptRoot 'artifacts'
# Newest server line first: Jellyfin sorts compatible versions with a stable sort, so with equal
# version numbers the first entry in manifest.json wins.
$targets = @(
    @{ Framework = 'net10.0'; TargetAbi = '12.0.0.0';  Label = 'jellyfin-12' },
    @{ Framework = 'net9.0';  TargetAbi = '10.11.0.0'; Label = 'jellyfin-10.11' }
)
$description = 'Automatically creates and updates collections for movie franchises using TMDb collection data.'
$overview = 'Automatic movie franchise collections'
$guid = '7c1f3c2e-5b8a-4d3e-9f61-2a4b8e0d9c17'
$timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
# Must be UTF-8 *without* a BOM: Jellyfin parses meta.json from raw bytes and rejects a BOM.
# (Out-File -Encoding utf8 in Windows PowerShell always writes one.)
$utf8NoBom = New-Object Text.UTF8Encoding $false
$manifestVersions = @()

if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
New-Item -ItemType Directory $artifacts | Out-Null

foreach ($t in $targets) {
    $staging = Join-Path $artifacts "staging-$($t.Label)"
    dotnet publish $project -c Release -f $t.Framework -o $staging
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $($t.Framework)" }

    # Drop-in folder: copy "Franchise Collections_<version>" straight into the server's plugins directory.
    $out = Join-Path $artifacts "$($t.Label)\Franchise Collections_$version"
    New-Item -ItemType Directory $out | Out-Null
    Copy-Item (Join-Path $staging 'Jellyfin.Plugin.FranchiseCollections.dll') $out
    Remove-Item $staging -Recurse -Force

    $meta = [ordered]@{
        category    = 'Metadata'
        changelog   = $changelog
        description = $description
        guid        = $guid
        name        = 'Franchise Collections'
        overview    = $overview
        owner       = 'TNT24'
        targetAbi   = $t.TargetAbi
        timestamp   = $timestamp
        version     = $version
        status      = 'Active'
        autoUpdate  = $false
        imagePath   = ''
        assemblies  = @('Jellyfin.Plugin.FranchiseCollections.dll')
    }
    [IO.File]::WriteAllText((Join-Path $out 'meta.json'), ($meta | ConvertTo-Json), $utf8NoBom)

    $zipName = "franchise-collections_$($version)_$($t.Label).zip"
    $zip = Join-Path $artifacts $zipName
    Compress-Archive -Path (Join-Path $out 'Jellyfin.Plugin.FranchiseCollections.dll'), (Join-Path $out 'meta.json') -DestinationPath $zip
    Write-Host "Created $out"
    Write-Host "Created $zip"

    $manifestVersions += [ordered]@{
        version   = $version
        changelog = $changelog
        targetAbi = $t.TargetAbi
        sourceUrl = "https://github.com/$repo/releases/download/v$version/$zipName"
        checksum  = (Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant()
        timestamp = $timestamp
    }
}

# Plugin repository manifest, served from the repo's main branch. Versions from earlier releases are
# kept so they stay installable; entries for the version being built are replaced.
$manifestPath = Join-Path $PSScriptRoot 'manifest.json'
$oldVersions = @()
if (Test-Path $manifestPath) {
    $old = @(Get-Content $manifestPath -Raw | ConvertFrom-Json)
    $oldVersions = @($old[0].versions | Where-Object { $_.version -ne $version })
}
$manifest = @(
    [ordered]@{
        guid        = $guid
        name        = 'Franchise Collections'
        description = $description
        overview    = $overview
        owner       = 'TNT24'
        category    = 'Metadata'
        imageUrl    = ''
        versions    = @($manifestVersions) + $oldVersions
    }
)
[IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject $manifest -Depth 5), $utf8NoBom)
Write-Host "Updated $manifestPath"
