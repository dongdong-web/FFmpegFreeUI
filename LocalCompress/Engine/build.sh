#!/usr/bin/env bash
# Run in an MSYS2 UCRT64 shell. Sources are supplied beside this script.
set -euo pipefail
if [[ "${MSYSTEM:-}" != UCRT64 ]]; then
    echo 'Use an MSYS2 UCRT64 shell.' >&2; exit 1
fi
root=$(cd "$(dirname "$0")" && pwd)
output=${1:?Usage: build.sh /absolute/output/directory}
mkdir -p "$output"
output=$(cd "$output" && pwd)
for command in gcc nasm make meson ninja pkg-config tar sha256sum; do
    command -v "$command" >/dev/null
done
cd "$root/sources"
sha256sum --check SHA256SUMS.txt
mkdir -p "$output/work" "$output/prefix" "$output/bin" "$output/licenses"
tar -xf ffmpeg-8.1.tar.xz -C "$output/work"
tar -xf x264-source.tar.gz -C "$output/work"
tar -xf dav1d-source.tar.gz -C "$output/work"
prefix="$output/prefix"
export PKG_CONFIG_PATH="$prefix/lib/pkgconfig"
export PKG_CONFIG_LIBDIR="$prefix/lib/pkgconfig"
jobs=${JOBS:-6}
cd "$output/work/x264-b35605ace3ddf7c1a5d67a2eb553f034aef41d55"
./configure --prefix="$prefix" --enable-static --disable-cli --disable-opencl
make -j"$jobs"
make install-lib-static
cp COPYING "$output/licenses/x264-COPYING.txt"
cd "$output/work/dav1d-b546257f770768b2c88258c533da38b91a06f737"
meson setup "$output/work/dav1d-build" --prefix="$prefix" --libdir=lib \
    --buildtype=release --default-library=static --wrap-mode=nodownload \
    -Denable_tools=false -Denable_tests=false -Denable_examples=false
ninja -C "$output/work/dav1d-build" -j"$jobs"
ninja -C "$output/work/dav1d-build" install
cp COPYING "$output/licenses/dav1d-COPYING.txt"
cd "$output/work/ffmpeg-8.1"
./configure --prefix="$output" --target-os=mingw32 --arch=x86_64 \
    --disable-autodetect --disable-network --disable-doc --disable-debug --disable-ffplay \
    --disable-shared --enable-static --enable-gpl --enable-version3 --enable-libx264 --enable-libdav1d \
    --pkg-config-flags=--static --extra-cflags="-I$prefix/include" \
    --extra-ldflags="-L$prefix/lib -static" --extra-version=LocalCompress-source-build
make -j"$jobs"
cp ffmpeg.exe ffprobe.exe "$output/bin/"
cp COPYING.GPLv3 "$output/LICENSE"
cp COPYING* "$output/licenses/"
cp config.h config_components.h ffbuild/config.mak "$output/licenses/"
{ gcc --version; nasm --version; make --version; meson --version; ninja --version; pkg-config --version; pacman -Q; } > "$output/licenses/toolchain.txt"
"$output/bin/ffmpeg.exe" -version > "$output/licenses/FFmpeg-version-and-build.txt"
cp "$root/README.md" "$output/README.txt"
cp "$root/sources/SHA256SUMS.txt" "$output/licenses/source-SHA256SUMS.txt"
echo "Built local engine: $output/bin"
