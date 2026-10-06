"""Local, opt-in codec study. Inputs stay unchanged; no network calls.

Requires Python 3.8+ and a trusted FFmpeg/ffprobe build with x264, x265,
SVT-AV1 and SSIM. This is an experiment, not the application's default policy.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import time


def run(executable, arguments):
    result = subprocess.run([str(executable)] + arguments, capture_output=True,
                            encoding="utf-8", errors="replace")
    if result.returncode:
        raise RuntimeError(result.stderr[-16000:])
    return result


def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            value.update(chunk)
    return value.hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("tools", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("inputs", nargs="+", type=Path)
    parser.add_argument("--conservative", action="store_true", help="Try higher-quality HEVC/AV1 candidates")
    args = parser.parse_args()
    for path in [args.output] + args.inputs:
        if str(path).startswith(("\\\\", "//")):
            parser.error("Use local disk paths, not network shares")
    if (args.output / "results.json").exists() or (args.output / "report.md").exists():
        parser.error("Choose a fresh output directory to preserve previous reports")
    args.output.mkdir(parents=True, exist_ok=True)
    ffmpeg, probe = args.tools / "ffmpeg.exe", args.tools / "ffprobe.exe"
    profiles = [("h264-20", "libx264", "slow", "20"),
                ("hevc-22", "libx265", "medium", "22"),
                ("av1-26", "libsvtav1", "6", "26")]
    if args.conservative:
        profiles = [("hevc-20", "libx265", "medium", "20"),
                    ("av1-22", "libsvtav1", "6", "22")]
    rows = []
    for source in args.inputs:
        source = source.resolve(strict=True)
        original_hash = digest(source)
        original = json.loads(run(probe, ["-v", "error", "-protocol_whitelist", "file,pipe",
            "-show_streams", "-show_format", "-of", "json", str(source)]).stdout)
        video = next(s for s in original["streams"] if s["codec_type"] == "video")
        for name, codec, preset, crf in profiles:
            output = args.output / (source.stem + "-" + name + ".mp4")
            # Exclusive FFmpeg creation protects existing study outputs.
            command = ["-v", "error", "-nostdin", "-n", "-protocol_whitelist", "file,pipe",
                "-i", str(source), "-map", "0:v:0", "-map", "0:a:0?", "-c:v", codec,
                "-preset", preset, "-crf", crf, "-pix_fmt", "yuv420p", "-fps_mode", "passthrough",
                "-c:a", "copy", "-map_metadata", "-1", "-map_metadata:s", "-1",
                "-map_chapters", "-1", "-movflags", "+faststart"]
            if codec == "libx265":
                command += ["-tag:v", "hvc1", "-x265-params", "pools=4:frame-threads=2"]
            if codec == "libsvtav1":
                command += ["-svtav1-params", "lp=4"]
            started = time.perf_counter()
            run(ffmpeg, command + [str(output)])
            elapsed = time.perf_counter() - started
            encoded = json.loads(run(probe, ["-v", "error", "-show_streams", "-show_format",
                "-of", "json", str(output)]).stdout)
            out_video = next(s for s in encoded["streams"] if s["codec_type"] == "video")
            if (video["width"], video["height"]) != (out_video["width"], out_video["height"]):
                raise RuntimeError("Dimensions changed")
            if abs(float(original["format"]["duration"]) - float(encoded["format"]["duration"])) > .1:
                raise RuntimeError("Duration changed")
            metric = run(ffmpeg, ["-hide_banner", "-nostdin", "-protocol_whitelist", "file,pipe",
                "-i", str(source), "-protocol_whitelist", "file,pipe", "-i", str(output),
                "-filter_complex", "[0:v]settb=AVTB,setpts=PTS-STARTPTS[a];[1:v]settb=AVTB,setpts=PTS-STARTPTS[b];[a][b]ssim",
                "-an", "-f", "null", "NUL"]).stderr
            match = re.search(r"SSIM.*All:([0-9.]+)", metric)
            if not match:
                raise RuntimeError("Missing SSIM")
            if digest(source) != original_hash:
                raise RuntimeError("Input changed")
            row = dict(sample=source.name, profile=name, original_bytes=source.stat().st_size,
                       output_bytes=output.stat().st_size, ssim=float(match.group(1)),
                       seconds=elapsed, output=str(output.resolve()), input_sha256=original_hash)
            rows.append(row)
            (args.output / "results.json").write_text(json.dumps(rows, ensure_ascii=False, indent=2), encoding="utf-8")
            print(json.dumps(row, ensure_ascii=False), flush=True)
    report = ["# 本地编码对照试验", "", "只代表所列短片段。各编码质量数值不能横向等同；SSIM 不代表视觉无损。保持尺寸和帧时间，复制首个音轨，输入 SHA256 未变化。编码时间包含当前机器负载。尚未验证接收设备兼容性或完整动态观感。", "",
              "| 片段 | 方案 | 缩小 | SSIM | 编码秒数 |", "| --- | --- | --- | --- | --- |"]
    for row in rows:
        report.append("| {sample} | {profile} | {saving:.1%} | {ssim:.6f} | {seconds:.2f} |".format(
            saving=1-row["output_bytes"]/row["original_bytes"], **row))
    (args.output / "report.md").write_text("\n".join(report), encoding="utf-8")


if __name__ == "__main__":
    main()
