param(
    [Parameter(Mandatory = $true)][string]$SourceBundle,
    [Parameter(Mandatory = $true)][string]$EngineDirectory,
    [Parameter(Mandatory = $true)][string]$MsysDirectory,
    [Parameter(Mandatory = $true)][string]$Archive
)
$ErrorActionPreference = 'Stop'
$bundle = (Resolve-Path -LiteralPath $SourceBundle).Path
$engine = (Resolve-Path -LiteralPath $EngineDirectory).Path
$msys = (Resolve-Path -LiteralPath $MsysDirectory).Path
$buildInfo = Join-Path $bundle 'build-info'
New-Item -ItemType Directory -Force -Path $buildInfo | Out-Null
foreach ($name in @('crt', 'libgcc', 'libatomic')) {
    Copy-Item -LiteralPath (Join-Path $msys "ucrt64/share/licenses/$name") -Destination (Join-Path $engine "licenses/$name") -Recurse -Force
}
# Enumerate explicitly because LiteralPath does not expand wildcards.
Get-ChildItem -LiteralPath (Join-Path $engine 'licenses') | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $buildInfo -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $engine 'LICENSE') -Destination (Join-Path $bundle 'LICENSE-GPLv3.txt')
Copy-Item -LiteralPath $PSCommandPath -Destination $bundle
$binaryHashes = foreach ($name in @('ffmpeg.exe', 'ffprobe.exe')) {
    '{0}  bin/{1}' -f (Get-FileHash -LiteralPath (Join-Path $engine "bin/$name") -Algorithm SHA256).Hash.ToLowerInvariant(), $name
}
[IO.File]::WriteAllText((Join-Path $bundle 'binary-SHA256SUMS.txt'), ($binaryHashes -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($bundle, [IO.Path]::GetFullPath($Archive), [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Output "Archived sources matching engine binaries: $Archive"
