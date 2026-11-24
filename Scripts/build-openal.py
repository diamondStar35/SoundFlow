"""Build the pinned OpenAL Soft implementation into a single RID's native asset directory.

Used only by .github/workflows/build-openal.yml; local native compilation is disabled.
The source checkout must be the immutable revision in Packaging/NativePlatforms.json.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
POLICY = json.loads((ROOT / "Packaging/NativePlatforms.json").read_text())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=POLICY["retained"])
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/openal/runtimes")
    parser.add_argument("--build", type=Path, default=ROOT / "artifacts/openal/build")
    parser.add_argument("--jobs", type=int, default=min(os.cpu_count() or 2, 4))
    args = parser.parse_args()
    if os.environ.get("GITHUB_ACTIONS") != "true":
        parser.error("OpenAL Soft is built only by the GitHub Actions workflow; local builds are disabled.")
    source = args.source.resolve()
    actual = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    if actual != POLICY["openal"]["commit"]:
        raise SystemExit(f"Expected pinned source {POLICY['openal']['commit']}; found {actual}")
    patches = sorted((ROOT / "Native/openal-soft-patches").glob("*.patch"))
    for patch in patches:
        already_applied = subprocess.run(["git", "-C", str(source), "apply", "--reverse", "--check", "--ignore-whitespace", str(patch)],
                                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0
        if not already_applied:
            subprocess.run(["git", "-C", str(source), "apply", "--check", "--ignore-whitespace", str(patch)], check=True)
            subprocess.run(["git", "-C", str(source), "apply", "--ignore-whitespace", str(patch)], check=True)
    build = args.build.resolve() / args.rid
    destination = args.output.resolve() / args.rid / "native"
    options = ["-DCMAKE_BUILD_TYPE=Release", "-DLIBTYPE=SHARED", "-DALSOFT_BUILD_ROUTER=OFF",
               "-DALSOFT_UTILS=OFF", "-DALSOFT_EXAMPLES=OFF", "-DALSOFT_TESTS=OFF",
               "-DALSOFT_INSTALL=OFF", "-DALSOFT_EMBED_HRTF_DATA=ON", "-DALSOFT_ENABLE_MODULES=OFF"]
    rid = args.rid
    if rid.startswith("win-"):
        # Keep C++/compiler runtimes inside the DLL, independently of the chosen MinGW/Clang architecture.
        options += ["-DCMAKE_SHARED_LINKER_FLAGS=-static", "-DALSOFT_REQUIRE_WASAPI=ON"]
        if os.environ.get("RC"):
            options += ["-DCMAKE_RC_COMPILER=" + Path(os.environ["RC"]).as_posix()]
        if os.environ.get("MSYSTEM") == "MINGW64":
            options += ["-DALSOFT_STATIC_LIBGCC=ON", "-DALSOFT_STATIC_STDCXX=ON", "-DALSOFT_STATIC_WINPTHREAD=ON"]
    elif rid.startswith("linux-"):
        options += ["-DALSOFT_REQUIRE_ALSA=ON", "-DALSOFT_REQUIRE_PULSEAUDIO=ON", "-DALSOFT_REQUIRE_PIPEWIRE=ON"]
    elif rid.startswith("osx-"):
        options += ["-DCMAKE_OSX_ARCHITECTURES=" + ("arm64" if rid.endswith("arm64") else "x86_64"),
                    "-DCMAKE_OSX_DEPLOYMENT_TARGET=11.0", "-DALSOFT_REQUIRE_COREAUDIO=ON"]
    elif rid == "ios-arm64":
        options += ["-DCMAKE_SYSTEM_NAME=iOS", "-DCMAKE_OSX_ARCHITECTURES=arm64", "-DCMAKE_OSX_DEPLOYMENT_TARGET=12.0",
                    "-DALSOFT_OSX_FRAMEWORK=ON", "-DALSOFT_REQUIRE_COREAUDIO=ON",
                    "-DCMAKE_XCODE_ATTRIBUTE_CODE_SIGNING_ALLOWED=NO", "-DCMAKE_XCODE_ATTRIBUTE_CODE_SIGNING_REQUIRED=NO"]
    elif rid == "android-arm64":
        ndk = os.environ.get("ANDROID_NDK_ROOT") or os.environ.get("ANDROID_NDK_HOME")
        if not ndk:
            raise SystemExit("ANDROID_NDK_ROOT must name an installed Android NDK")
        options += [f"-DCMAKE_TOOLCHAIN_FILE={Path(ndk) / 'build/cmake/android.toolchain.cmake'}",
                    "-DANDROID_ABI=arm64-v8a", "-DANDROID_PLATFORM=21", "-DANDROID_STL=c++_static",
                    "-DALSOFT_BACKEND_OBOE=OFF", "-DALSOFT_REQUIRE_OPENSL=ON",
                    "-DCMAKE_SHARED_LINKER_FLAGS=-Wl,-z,max-page-size=16384"]
    subprocess.run(["cmake", "-S", str(source), "-B", str(build), "-G", "Ninja", *options], check=True)
    subprocess.run(["cmake", "--build", str(build), "--parallel", str(args.jobs)], check=True)
    destination.mkdir(parents=True, exist_ok=True)
    if rid == "ios-arm64":
        framework = next(build.rglob("soft_oal.framework"))
        shutil.copytree(framework, destination / framework.name, dirs_exist_ok=True, symlinks=True)
    else:
        name = "soft_oal.dll" if rid.startswith("win-") else "libopenal.dylib" if rid.startswith("osx-") else "libopenal.so"
        candidates = list(build.rglob(name))
        if not candidates and rid.startswith("win-"):
            candidates = list(build.rglob("OpenAL32.dll"))
        if not candidates:
            raise SystemExit(f"Build did not produce {name}")
        shutil.copy2(candidates[0], destination / name, follow_symlinks=True)
    (destination.parent / "provenance.json").write_text(json.dumps({
        **POLICY["openal"], "rid": rid, "cmake_options": options,
        "patches": [patch.name for patch in patches],
        "compiler_environment": {key: os.environ[key] for key in ["CC", "CXX", "CXXFLAGS", "LDFLAGS"] if key in os.environ},
    }, indent=2) + "\n")
    print(destination)


if __name__ == "__main__":
    main()
