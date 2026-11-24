#!/usr/bin/env python3
"""Reproducible CPU bridge builds. Requires Python 3.10+, CMake 3.24+, git and a C++ compiler."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess

ROOT = Path(__file__).resolve().parent
REVISION = "7e3c6fb5225d3ccf22807ee641332669df34650d"  # MNN 3.6.2
RIDS = ("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64",
        "android-arm64", "android-x64", "ios-arm64", "iossimulator-arm64", "iossimulator-x64")


def run(*args):
    print("+", " ".join(map(str, args)), flush=True)
    subprocess.run(list(map(str, args)), check=True)


def fetch():
    source = ROOT / "_deps" / "MNN"
    if not source.exists():
        source.parent.mkdir(parents=True, exist_ok=True)
        run("git", "clone", "--config", "core.autocrlf=false", "--depth", "1", "--branch", "3.6.2",
            "https://github.com/alibaba/MNN.git", source)
    actual = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if actual != REVISION:
        raise RuntimeError(f"MNN revision mismatch: expected {REVISION}, found {actual}")
    status = ["-C", str(source), "status", "--porcelain", "--untracked-files=no"]
    git = ["git", "-c", "core.filemode=false"]
    # Existing Windows checkouts may have CRLF on disk when reused from WSL.
    dirty = subprocess.check_output(git + status, text=True).strip()
    if dirty and subprocess.check_output(git + ["-c", "core.autocrlf=true"] + status, text=True).strip():
        raise RuntimeError("Pinned MNN sources have local modifications; refusing to build")
    return source


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=RIDS)
    parser.add_argument("--converter", action="store_true", help="Also build the offline host converter")
    parser.add_argument("--ndk", type=Path, help="Android NDK root, or set ANDROID_NDK_HOME")
    parser.add_argument("--jobs", type=int, default=min(os.cpu_count() or 2, 8))
    args = parser.parse_args()
    rid = args.rid
    host = platform.system()
    source = fetch()
    build = ROOT / "build" / rid
    output = ROOT.parent.parent / "artifacts" / "mnn" / "runtimes" / rid / "native"
    configure = ["cmake", "-S", ROOT, "-B", build, "-DCMAKE_BUILD_TYPE=Release",
                 f"-DMNN_SOURCE_DIR={source}", "-DCMAKE_POLICY_VERSION_MINIMUM=3.5",
                 f"-DEYETRACKER_MNN_CONVERTER={'ON' if args.converter else 'OFF'}"]
    if rid.startswith("win-"):
        if host != "Windows":
            raise RuntimeError("Windows binaries require MSVC on Windows")
        configure += ["-G", "Visual Studio 17 2022", "-A", "ARM64" if rid.endswith("arm64") else "x64"]
    elif rid.startswith("android-"):
        if args.converter:
            raise RuntimeError("Build MNNConvert for the host, not Android")
        ndk = args.ndk or (Path(os.environ["ANDROID_NDK_HOME"]) if "ANDROID_NDK_HOME" in os.environ else None)
        if ndk is None or not (ndk / "build/cmake/android.toolchain.cmake").exists():
            raise RuntimeError("Pass --ndk <NDK-root> or set ANDROID_NDK_HOME (tested with r29)")
        abi = "arm64-v8a" if rid.endswith("arm64") else "x86_64"
        configure += ["-G", "Ninja", f"-DCMAKE_TOOLCHAIN_FILE={ndk / 'build/cmake/android.toolchain.cmake'}",
                      f"-DANDROID_ABI={abi}", "-DANDROID_PLATFORM=android-23", "-DANDROID_STL=c++_static"]
        if host == "Windows" and not shutil.which("ninja"):
            visual_studio = Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Microsoft Visual Studio"
            ninja = sorted(visual_studio.glob("*/*/Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe"))
            if not ninja:
                raise RuntimeError("Install Ninja or the Visual Studio CMake tools")
            configure += [f"-DCMAKE_MAKE_PROGRAM={ninja[-1]}"]
    elif rid.startswith(("osx-", "ios")):
        if host != "Darwin":
            raise RuntimeError("macOS/iOS libraries require macOS with Xcode")
        arch = "arm64" if rid.endswith("arm64") else "x86_64"
        configure += [f"-DCMAKE_OSX_ARCHITECTURES={arch}"]
        if rid.startswith("ios"):
            if args.converter:
                raise RuntimeError("Build MNNConvert for macOS, not iOS")
            sdk = "iphonesimulator" if rid.startswith("iossimulator") else "iphoneos"
            configure += ["-DCMAKE_SYSTEM_NAME=iOS", f"-DCMAKE_OSX_SYSROOT={sdk}",
                          "-DCMAKE_OSX_DEPLOYMENT_TARGET=15.0", "-DCMAKE_XCODE_ATTRIBUTE_CODE_SIGNING_ALLOWED=NO"]
        else:
            configure += ["-DCMAKE_OSX_DEPLOYMENT_TARGET=12.0"]
    elif rid.startswith("linux-"):
        if host != "Linux":
            raise RuntimeError("Run Linux builds on Linux or in WSL")
        configure += ["-G", "Ninja" if shutil.which("ninja") else "Unix Makefiles"]
        if rid == "linux-arm64" and platform.machine() not in ("aarch64", "arm64"):
            if args.converter:
                raise RuntimeError("Build MNNConvert for the native host")
            configure += ["-DCMAKE_SYSTEM_NAME=Linux", "-DCMAKE_SYSTEM_PROCESSOR=aarch64",
                          "-DCMAKE_C_COMPILER=aarch64-linux-gnu-gcc", "-DCMAKE_CXX_COMPILER=aarch64-linux-gnu-g++"]
        elif rid == "linux-x64" and platform.machine() not in ("x86_64", "amd64"):
            raise RuntimeError("linux-x64 must be built on an x64 Linux host")
    run(*configure)
    targets = ["eyetracker_mnn"] + (["mnn_converter"] if args.converter else [])
    run("cmake", "--build", build, "--config", "Release", "--target", *targets, "--parallel", args.jobs)
    install = ["cmake", "--install", build, "--config", "Release", "--prefix", output]
    if rid.startswith("android-"):
        # Remove NDK debug sections from the packaged .so; retain symbols in build/.
        install.append("--strip")
    run(*install)
    if args.converter:
        name = "MNNConvert.exe" if host == "Windows" else "MNNConvert"
        candidates = list(build.rglob(name))
        if len(candidates) != 1:
            raise RuntimeError(f"Could not uniquely locate built {name}: {candidates}")
        tools = output.parents[2] / "tools" / rid
        tools.mkdir(parents=True, exist_ok=True)
        shutil.copy2(candidates[0], tools / name)
    files = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in output.iterdir() if p.is_file()}
    manifest = {"mnn_version": "3.6.2", "mnn_revision": REVISION, "rid": rid,
                "backend": "CPU", "bridge_abi": 1, "sha256": files}
    (output / "build-info.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Native artifacts: {output}")


if __name__ == "__main__":
    main()
