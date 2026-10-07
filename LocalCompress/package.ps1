param(
    [Parameter(Mandatory = $true)][string]$FFmpegDirectory,
    [Parameter(Mandatory = $true)][string]$EngineSourceArchive,
    [string]$DotnetPath = 'dotnet',
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repoDirectory = Split-Path $PSScriptRoot -Parent
$buildDirectory = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repoDirectory 'artifacts/LocalCompress-win-x64' }
$engineDirectory = (Resolve-Path -LiteralPath $FFmpegDirectory).Path
foreach ($file in @('bin/ffmpeg.exe', 'bin/ffprobe.exe', 'LICENSE', 'README.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $engineDirectory $file))) { throw "Missing engine distribution file: $file" }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$sourceArchivePath = (Resolve-Path -LiteralPath $EngineSourceArchive).Path
$sourceZip = [IO.Compression.ZipFile]::OpenRead($sourceArchivePath)
try {
    $manifest = $sourceZip.GetEntry('binary-SHA256SUMS.txt')
    if ($null -eq $manifest) { throw 'Engine source archive lacks matching binary hashes.' }
    $reader = [IO.StreamReader]::new($manifest.Open())
    try { $binaryHashes = $reader.ReadToEnd() } finally { $reader.Dispose() }
    foreach ($name in @('ffmpeg.exe', 'ffprobe.exe')) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $engineDirectory "bin/$name") -Algorithm SHA256).Hash
        if ($binaryHashes -notmatch "(?im)^$hash  bin/$([regex]::Escape($name))\r?$") {
            throw "Engine source archive does not match $name."
        }
    }
    foreach ($entry in @('build.sh', 'README.md', 'sources/ffmpeg-8.1.tar.xz', 'sources/x264-source.tar.gz', 'sources/dav1d-source.tar.gz', 'sources/SHA256SUMS.txt', 'build-info/toolchain.txt', 'build-info/config.mak')) {
        if ($null -eq $sourceZip.GetEntry($entry)) { throw "Missing corresponding source material: $entry" }
    }
} finally { $sourceZip.Dispose() }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $DotnetPath publish (Join-Path $PSScriptRoot 'App/LocalCompress.App.csproj') -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.12 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $buildDirectory
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$toolsDirectory = Join-Path $buildDirectory 'tools'
$licenseDirectory = Join-Path $buildDirectory 'licenses'
New-Item -ItemType Directory -Force $toolsDirectory, $licenseDirectory | Out-Null
$sourceDirectory = Join-Path $buildDirectory 'sources'
New-Item -ItemType Directory -Force $sourceDirectory | Out-Null
Copy-Item -LiteralPath $sourceArchivePath -Destination (Join-Path $sourceDirectory 'FFmpeg-x264-corresponding-source.zip')
Copy-Item -LiteralPath (Join-Path $engineDirectory 'bin/ffmpeg.exe'), (Join-Path $engineDirectory 'bin/ffprobe.exe') -Destination $toolsDirectory
Copy-Item -LiteralPath (Join-Path $engineDirectory 'LICENSE') -Destination (Join-Path $licenseDirectory 'FFmpeg-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $engineDirectory 'README.txt') -Destination (Join-Path $licenseDirectory 'FFmpeg-build-README.txt')
$engineLicenses = Join-Path $engineDirectory 'licenses'
if (Test-Path -LiteralPath $engineLicenses) {
    Copy-Item -LiteralPath $engineLicenses -Destination (Join-Path $licenseDirectory 'engine') -Recurse
}
Copy-Item -LiteralPath (Join-Path $repoDirectory 'LICENSE.txt') -Destination (Join-Path $licenseDirectory 'FFmpegFreeUI-MIT.txt')
$usage = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Raw -Encoding UTF8
$usage.Replace('(RELATIONSHIP.md)', '(项目关系与第三方声明.md)').Replace('(SHARING.md)', '(分享与分发说明.md)').Replace('(Engine/README.md)', '(内核构建说明.md)') | Set-Content -LiteralPath (Join-Path $buildDirectory '使用说明.md') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Engine/README.md') -Destination (Join-Path $buildDirectory '内核构建说明.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CODEC-STUDY.md') -Destination (Join-Path $buildDirectory 'CODEC-STUDY.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RELATIONSHIP.md') -Destination (Join-Path $buildDirectory '项目关系与第三方声明.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'QUICKSTART.txt') -Destination (Join-Path $buildDirectory '先读我.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SHARING.md') -Destination (Join-Path $buildDirectory '分享与分发说明.md')
$packageLocation = (& $DotnetPath nuget locals global-packages --list) -replace '^global-packages:\s*', ''
if ($LASTEXITCODE -ne 0) { throw 'Cannot locate runtime license files.' }
foreach ($package in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
    $directory = Join-Path $packageLocation "$package/10.0.12"
    $licenseFiles = Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Name -match '^LICENSE(\.TXT)?$|^THIRD-PARTY-NOTICES\.TXT$' }
    if (-not ($licenseFiles | Where-Object { $_.Name -match '^LICENSE' })) { throw "Missing runtime license: $package" }
    foreach ($file in $licenseFiles) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $licenseDirectory "$package-$($file.Name)")
    }
}
$engineVersion = & (Join-Path $toolsDirectory 'ffmpeg.exe') -version
$engineVersion | Set-Content -LiteralPath (Join-Path $licenseDirectory 'FFmpeg-version-and-build.txt') -Encoding UTF8
Get-ChildItem -LiteralPath $buildDirectory -Filter '*.pdb' | Remove-Item
$hashes = Get-ChildItem -LiteralPath $buildDirectory -Recurse -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | ForEach-Object {
    $relative = $_.FullName.Substring($buildDirectory.Length + 1)
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $relative
}
$hashes | Set-Content -LiteralPath (Join-Path $buildDirectory 'SHA256SUMS.txt') -Encoding UTF8
Write-Output "Ready: $buildDirectory"
