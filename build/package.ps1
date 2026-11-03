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
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]*$')]
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
    # Match the SemVer form supported by Velopack; reject malformed versions before publishing.
    if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-((0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?$') {
        throw "'$Version' is not a supported semantic version."
    }
    if ($SignParams -and $AzureTrustedSignFile) { throw 'Choose one signing method.' }
    Write-Host "Packaging SANOVA Launcher $Version ($Runtime, channel '$Channel')"

    $Output = [IO.Path]::GetFullPath($Output)
    $rootPath = [IO.Path]::GetFullPath($root.Path).TrimEnd('\', '/')
    if ($rootPath.Equals($Output.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase) -or
        $rootPath.StartsWith($Output.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Output must be a dedicated build directory, not the repository or one of its parents.'
    }
    $publish = Join-Path $Output "publish\$Runtime"
    $releases = Join-Path $Output 'releases'
    # Refuse junctions/symlinks before recursive cleanup can follow them outside the output tree.
    foreach ($target in @($publish, $releases, (Join-Path $Output 'tools'))) {
        $probe = $target
        while ($probe) {
            if ((Test-Path -LiteralPath $probe) -and
                ((Get-Item -LiteralPath $probe -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Build output must not pass through a reparse point: $probe"
            }
            $parent = Split-Path -Parent $probe
            if ($parent -eq $probe) { break }
            $probe = $parent
        }
    }
    if (-not ([IO.Path]::GetFullPath($publish)).StartsWith($Output.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Publish directory escaped the output directory.'
    }
    if (Test-Path -LiteralPath $publish) {
        $linkedChild = Get-ChildItem -LiteralPath $publish -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1
        if ($linkedChild) { throw "Refusing to clean linked output: $($linkedChild.FullName)" }
        Remove-Item -LiteralPath $publish -Recurse -Force
    }

    dotnet tool restore
    if ($LASTEXITCODE) { throw 'dotnet tool restore failed' }

    dotnet publish src/Sanova.Launcher/Sanova.Launcher.csproj -c Release -r $Runtime --self-contained true `
        -p:Version=$Version -p:DebugType=none -warnaserror -o $publish
    if ($LASTEXITCODE) { throw 'dotnet publish failed' }

    $pack = @(
        'vpk', 'pack',
        '--packId', 'SANOVA.Launcher',
        '--packVersion', $Version,
        '--packDir', $publish,
        '--mainExe', 'SanovaLauncher.exe',
        '--runtime', $Runtime,
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
            -p:PublishSingleFile=true -p:Version=$Version -p:DebugType=none -warnaserror -o (Join-Path $Output 'tools')
        if ($LASTEXITCODE) { throw 'manifest tool publish failed' }
        Copy-Item -LiteralPath (Join-Path $publish 'licenses') -Destination (Join-Path $Output 'tools') -Recurse -Force
    }

    Write-Host "Done. Release files are in $releases"
    Get-ChildItem $releases | Format-Table Name, Length -AutoSize
}
finally {
    Pop-Location
}
