<#
.SYNOPSIS
  Builds a distributable SANOVA Launcher release with Velopack.

.DESCRIPTION
  1. Publishes the launcher self-contained for the target runtime.
  2. Packs it with Velopack (Setup.exe, full/delta .nupkg packages, releases.<channel>.json).
  3. Optionally publishes the sanova-manifest maintainer tool as a single executable.

  Nothing is uploaded. Publishing to GitHub Releases is a separate, deliberate step
  (see docs/RELEASING.md).

  To produce a delta package, put the previous release's files in <Output>\releases first
  (for example with `dotnet vpk download github`).

.EXAMPLE
  ./build/package.ps1 -Version 0.2.0
  ./build/package.ps1 -Version 0.2.0 -ReleaseNotes ./notes/0.2.0.md -IncludeTools
  ./build/package.ps1 -Version 0.2.0 -SignParams '/td sha256 /fd sha256 /f C:\certs\sanova.pfx /tr http://timestamp.digicert.com'
#>
param(
    [string]$Version,
    [string]$Runtime = 'win-x64',
    [string]$Channel = 'win',
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts'),
    [string]$ReleaseNotes,
    # Authenticode signing through signtool.exe (parameters after "signtool sign").
    [string]$SignParams,
    # Authenticode signing through Azure Trusted Signing (path to metadata.json).
    [string]$AzureTrustedSignFile,
    [switch]$IncludeTools
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Push-Location $root
try {
    if (-not $Version) {
        [xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
        $Version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    }
    Write-Host "Packaging SANOVA Launcher $Version ($Runtime, channel '$Channel')"

    $publish = Join-Path $Output "publish\$Runtime"
    $releases = Join-Path $Output 'releases'
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    dotnet tool restore
    if ($LASTEXITCODE) { throw 'dotnet tool restore failed' }

    dotnet publish src/Sanova.Launcher/Sanova.Launcher.csproj -c Release -r $Runtime --self-contained true `
        -p:Version=$Version -p:DebugType=none -o $publish
    if ($LASTEXITCODE) { throw 'dotnet publish failed' }

    $pack = @(
        'vpk', 'pack',
        '--packId', 'SANOVA.Launcher',
        '--packVersion', $Version,
        '--packDir', $publish,
        '--mainExe', 'SanovaLauncher.exe',
        '--packTitle', 'SANOVA Launcher',
        '--packAuthors', 'SANOVA contributors',
        '--icon', 'src/Sanova.Launcher/Assets/Branding/sanova.ico',
        '--channel', $Channel,
        '--outputDir', $releases
    )
    if ($ReleaseNotes) { $pack += @('--releaseNotes', (Resolve-Path $ReleaseNotes)) }
    if ($SignParams) { $pack += @('--signParams', $SignParams) }
    if ($AzureTrustedSignFile) { $pack += @('--azureTrustedSignFile', (Resolve-Path $AzureTrustedSignFile)) }
    dotnet @pack
    if ($LASTEXITCODE) { throw 'vpk pack failed' }

    if ($IncludeTools) {
        dotnet publish src/Sanova.ManifestTool/Sanova.ManifestTool.csproj -c Release -r $Runtime --self-contained true `
            -p:PublishSingleFile=true -p:Version=$Version -p:DebugType=none -o (Join-Path $Output 'tools')
        if ($LASTEXITCODE) { throw 'manifest tool publish failed' }
    }

    Write-Host "Done. Release files are in $releases"
    Get-ChildItem $releases | Format-Table Name, Length -AutoSize
}
finally {
    Pop-Location
}
