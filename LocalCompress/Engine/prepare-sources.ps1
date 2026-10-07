param([Parameter(Mandatory = $true)][string]$Destination)
$ErrorActionPreference = 'Stop'
$bundle = [IO.Path]::GetFullPath($Destination)
$sources = Join-Path $bundle 'sources'
New-Item -ItemType Directory -Force -Path $sources | Out-Null
$archives = @(
    @{ Name = 'ffmpeg-8.1.tar.xz'; Url = 'https://ffmpeg.org/releases/ffmpeg-8.1.tar.xz'; Hash = 'b072aed6871998cce9b36e7774033105ca29e33632be5b6347f3206898e0756a' },
    @{ Name = 'x264-source.tar.gz'; Url = 'https://codeload.github.com/mirror/x264/tar.gz/b35605ace3ddf7c1a5d67a2eb553f034aef41d55'; Hash = 'cd71a7515b0e9a012e1ac9b1f8415bebcaf6fc97d4db32286642ac4c0fbe24f9' },
    @{ Name = 'dav1d-source.tar.gz'; Url = 'https://codeload.github.com/videolan/dav1d/tar.gz/b546257f770768b2c88258c533da38b91a06f737'; Hash = 'c5b7428c3f5b28db0aee0c62f6ffb2b42f4b08e7be5ce49d7ad046aaf0dc151d' }
)
foreach ($archive in $archives) {
    $path = Join-Path $sources $archive.Name
    if (-not (Test-Path -LiteralPath $path)) {
        Invoke-WebRequest -Uri $archive.Url -OutFile $path -UseBasicParsing
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $archive.Hash) {
        throw "Source archive hash mismatch: $($archive.Name)"
    }
}
$hashes = $archives | ForEach-Object { '{0}  {1}' -f $_.Hash, $_.Name }
[IO.File]::WriteAllText((Join-Path $sources 'SHA256SUMS.txt'), ($hashes -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
foreach ($name in @('build.sh', 'README.md', 'prepare-sources.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $bundle
}
Write-Output "Verified corresponding sources: $bundle"
