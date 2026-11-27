"""Build the Android runtimes for LSL and the OpenCvSharp camera pipeline."""
import argparse
from pathlib import Path
import shutil
import subprocess
from urllib.request import urlretrieve
from zipfile import ZipFile

REPO = Path(__file__).resolve().parents[2]
WORK = Path(__file__).resolve().parent / "_work"
PLATFORMS = {"android-arm64": "arm64-v8a", "android-x64": "x86_64"}


def source(folder, repository, tag):
    path = WORK / folder
    if path.is_dir():
        return path

    archive = WORK / f"{folder}.zip"
    if not archive.exists():
        temporary = archive.with_suffix(".partial")
        urlretrieve(f"https://codeload.github.com/{repository}/zip/refs/tags/{tag}", temporary)
        temporary.replace(archive)
    with ZipFile(archive) as bundle:
        bundle.extractall(WORK)
    return path


def configure(source_dir, build, options):
    subprocess.run(["cmake", "-S", str(source_dir), "-B", str(build), "-G", "Ninja", *options], check=True)


def compile_project(build, jobs, target=None):
    command = ["cmake", "--build", str(build), "--parallel", str(jobs)]
    if target:
        command.extend(["--target", target])
    subprocess.run(command, check=True)


def build_runtime(args):
    WORK.mkdir(parents=True, exist_ok=True)
    lsl = source("liblsl-1.16.2", "sccn/liblsl", "v1.16.2")
    cv = source("opencv-4.13.0", "opencv/opencv", "4.13.0")
    sharp = source("opencvsharp-4.13.0.20260627", "shimat/opencvsharp", "4.13.0.20260627")
    arch = args.rid.removeprefix("android-")
    common = [
        f"-DCMAKE_TOOLCHAIN_FILE={args.ndk / 'build/cmake/android.toolchain.cmake'}",
        f"-DANDROID_ABI={PLATFORMS[args.rid]}",
        "-DANDROID_STL=c++_static", "-DCMAKE_BUILD_TYPE=Release",
    ]
    if args.ninja:
        common.append(f"-DCMAKE_MAKE_PROGRAM={args.ninja}")
    lsl_build = WORK / f"lsl-{arch}-api24"
    cv_build = WORK / f"cv-{arch}"
    install = WORK / f"cv-install-{arch}"
    sharp_build = WORK / f"sharp-{arch}"

    # Android exposes getifaddrs from API 24, which liblsl uses to find multicast interfaces.
    configure(lsl, lsl_build, [*common, "-DANDROID_PLATFORM=android-24", "-DLSL_OPTIMIZATIONS=OFF"])
    compile_project(lsl_build, args.jobs, "lsl")
    common.append("-DANDROID_PLATFORM=android-23")
    configure(cv, cv_build, [
        *common, f"-DCMAKE_INSTALL_PREFIX={install}", "-DBUILD_LIST=core,imgproc,imgcodecs",
        "-DBUILD_SHARED_LIBS=OFF", "-DBUILD_JAVA=OFF", "-DBUILD_ANDROID_EXAMPLES=OFF",
        "-DBUILD_ANDROID_PROJECTS=OFF", "-DBUILD_TESTS=OFF", "-DBUILD_PERF_TESTS=OFF",
        "-DBUILD_EXAMPLES=OFF", "-DWITH_OPENCL=OFF", "-DWITH_IPP=OFF", "-DWITH_ITT=OFF",
        "-DWITH_PROTOBUF=OFF", "-DWITH_ADE=OFF", "-DWITH_KLEIDICV=OFF",
        "-DWITH_QUIRC=OFF", "-DWITH_WEBP=OFF", "-DWITH_OPENJPEG=OFF", "-DWITH_JASPER=OFF",
        "-DWITH_TIFF=OFF", "-DWITH_OPENEXR=OFF", "-DBUILD_JPEG=ON", "-DBUILD_PNG=ON",
    ])
    compile_project(cv_build, args.jobs, "install")
    config = next(install.rglob("OpenCVConfig.cmake")).parent
    disabled = ["CONTRIB", "STITCHING", "CALIB3D", "VIDEO", "FEATURES2D", "FLANN",
                "DNN", "ML", "OBJDETECT", "PHOTO", "BARCODE", "HIGHGUI", "VIDEOIO"]
    configure(sharp / "src", sharp_build, [
        *common, f"-DOpenCV_DIR={config}", *(f"-DNO_{module}=ON" for module in disabled),
    ])
    compile_project(sharp_build, args.jobs, "OpenCvSharpExtern")

    native = REPO / "artifacts/android/runtimes" / args.rid / "native"
    native.mkdir(parents=True, exist_ok=True)
    shutil.copy2(lsl_build / "liblsl.so", native / "liblsl.so")
    shutil.copy2(sharp_build / "OpenCvSharpExtern/libOpenCvSharpExtern.so", native / "libOpenCvSharpExtern.so")
    for project, filename in [(lsl, "liblsl"), (cv, "OpenCV"), (sharp, "OpenCvSharp")]:
        license_file = next(file for file in project.glob("LICENSE*") if file.is_file())
        shutil.copy2(license_file, native / f"{filename}-LICENSE.txt")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ndk", type=Path, required=True)
    parser.add_argument("--rid", choices=PLATFORMS, required=True)
    parser.add_argument("--ninja", type=Path)
    parser.add_argument("--jobs", type=int, default=6)
    build_runtime(parser.parse_args())
