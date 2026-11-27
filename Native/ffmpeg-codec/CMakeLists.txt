cmake_minimum_required(VERSION 3.20)
project(SoundFlowFFmpeg C)

cmake_policy(SET CMP0135 NEW)

# LAME - Set macOS Deployment Target
if(CMAKE_SYSTEM_NAME STREQUAL "Darwin" AND NOT CMAKE_OSX_DEPLOYMENT_TARGET)
    set(CMAKE_OSX_DEPLOYMENT_TARGET "10.13")
    message(STATUS "CMAKE_OSX_DEPLOYMENT_TARGET not set. Defaulting to ${CMAKE_OSX_DEPLOYMENT_TARGET}")
endif()

# Helper – normalise CPU names that vary between OSes / toolchains
function(normalise_arch IN OUT)
    string(TOLOWER "${IN}" _arch)
    if(_arch STREQUAL "x86_64" OR _arch STREQUAL "amd64")
        set(${OUT} "x64"      PARENT_SCOPE)
    elseif(_arch STREQUAL "i686" OR _arch STREQUAL "x86")
        set(${OUT} "x86"      PARENT_SCOPE)
    elseif(_arch MATCHES "^armv?7")
        set(${OUT} "arm"      PARENT_SCOPE)
    elseif(_arch STREQUAL "aarch64" OR _arch STREQUAL "arm64")
        set(${OUT} "arm64"    PARENT_SCOPE)
    else()
        set(${OUT} "${_arch}" PARENT_SCOPE)   # fall-through
    endif()
endfunction()

# Platform / RID detection
if(CMAKE_SYSTEM_NAME STREQUAL "Windows")
    set(TARGET_OS "win")
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "ARM64|AARCH64|arm64|aarch64")
        normalise_arch("${CMAKE_SYSTEM_PROCESSOR}" TARGET_ARCH)
    elseif(CMAKE_SIZEOF_VOID_P EQUAL 8)
        set(TARGET_ARCH "x64")
    elseif(CMAKE_SIZEOF_VOID_P EQUAL 4)
        set(TARGET_ARCH "x86")
    else()
        # Fallback for any other unforeseen cases
        normalise_arch("${CMAKE_SYSTEM_PROCESSOR}" TARGET_ARCH)
    endif()

elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    set(TARGET_OS "linux")
    normalise_arch("${CMAKE_SYSTEM_PROCESSOR}" TARGET_ARCH)

elseif(CMAKE_SYSTEM_NAME STREQUAL "Darwin")
    set(TARGET_OS "osx")
    normalise_arch("${CMAKE_SYSTEM_PROCESSOR}" TARGET_ARCH)

elseif(CMAKE_SYSTEM_NAME STREQUAL "iOS")
    set(TARGET_OS "ios")
    set(TARGET_ARCH "arm64")

elseif(CMAKE_SYSTEM_NAME STREQUAL "Android")
    set(TARGET_OS "android")
    if(ANDROID_ABI STREQUAL "x86_64")
        set(TARGET_ARCH "x64")
    elseif(ANDROID_ABI STREQUAL "armeabi-v7a")
        set(TARGET_ARCH "arm")
    elseif(ANDROID_ABI STREQUAL "arm64-v8a")
        set(TARGET_ARCH "arm64")
    endif()

elseif(CMAKE_SYSTEM_NAME STREQUAL "FreeBSD")
    set(TARGET_OS "freebsd")
    normalise_arch("${CMAKE_SYSTEM_PROCESSOR}" TARGET_ARCH)
endif()

# Set symbolic linker flags for ELF-based systems
if(CMAKE_SYSTEM_NAME STREQUAL "Linux" OR
        CMAKE_SYSTEM_NAME STREQUAL "Android" OR
        CMAKE_SYSTEM_NAME STREQUAL "FreeBSD")
    set(CMAKE_SHARED_LINKER_FLAGS "-Wl,-Bsymbolic")
endif()

# .NET-style RID and output layout
set(RUNTIME_ID "${TARGET_OS}-${TARGET_ARCH}")
set(OUTPUT_DIR "${CMAKE_BINARY_DIR}/runtimes/${RUNTIME_ID}/native")
set(FFMPEG_INSTALL_DIR "${CMAKE_BINARY_DIR}/ffmpeg-install")
set(LAME_INSTALL_DIR "${CMAKE_BINARY_DIR}/lame-install")

# The profile is an allowlist; no codecs, muxers or demuxers are enabled implicitly.
file(READ "${CMAKE_CURRENT_SOURCE_DIR}/../../Packaging/FFmpeg.json" AUDIO_PROFILE)
string(JSON FFMPEG_VERSION GET "${AUDIO_PROFILE}" version)
string(JSON FFMPEG_URL GET "${AUDIO_PROFILE}" source url)
string(JSON FFMPEG_SHA256 GET "${AUDIO_PROFILE}" source sha256)
string(JSON LAME_URL GET "${AUDIO_PROFILE}" dependencies lame url)
string(JSON LAME_SHA256 GET "${AUDIO_PROFILE}" dependencies lame sha256)
set(AUDIO_INSTALL_DIR "${CMAKE_BINARY_DIR}/audio-deps")
set(LAME_INSTALL_DIR "${AUDIO_INSTALL_DIR}")
set(AUDIO_RIDS win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64 ios-arm64 android-arm64)
if(NOT RUNTIME_ID IN_LIST AUDIO_RIDS)
    message(FATAL_ERROR "Unsupported audio runtime: ${RUNTIME_ID}")
endif()
if(NOT CMAKE_BUILD_PARALLEL_LEVEL)
    set(CMAKE_BUILD_PARALLEL_LEVEL "$ENV{CMAKE_BUILD_PARALLEL_LEVEL}")
    if(NOT CMAKE_BUILD_PARALLEL_LEVEL)
        set(CMAKE_BUILD_PARALLEL_LEVEL 2)
    endif()
endif()
set(CMAKE_C_FLAGS_RELEASE "-O2 -DNDEBUG")
set(LAME_COMMON_FLAGS --disable-shared --enable-static --with-pic --disable-frontend --disable-decoder)
set(FFMPEG_COMMON_FLAGS
    --disable-everything --disable-autodetect --disable-gpl --disable-nonfree --enable-version3
    --disable-shared --enable-static --enable-pic --disable-symver --disable-debug --disable-doc
    --disable-programs --disable-avdevice --disable-avfilter --disable-swscale
    --enable-avcodec --enable-avformat --enable-avutil --enable-swresample --enable-network
    --enable-libmp3lame --enable-libopus --enable-libvorbis
    --optflags=-O2
    "--extra-cflags=-I${LAME_INSTALL_DIR}/include -I${AUDIO_INSTALL_DIR}/include"
    "--extra-ldflags=-L${LAME_INSTALL_DIR}/lib -L${AUDIO_INSTALL_DIR}/lib"
    --pkg-config-flags=--static --pkg-config=pkg-config)
foreach(category decoders encoders demuxers muxers parsers bsfs protocols)
    string(JSON count LENGTH "${AUDIO_PROFILE}" ${category})
    math(EXPR last "${count} - 1")
    set(names "")
    foreach(i RANGE ${last})
        string(JSON name GET "${AUDIO_PROFILE}" ${category} ${i})
        list(APPEND names "${name}")
    endforeach()
    list(JOIN names "," selected)
    string(REGEX REPLACE "s$" "" singular "${category}")
    list(APPEND FFMPEG_COMMON_FLAGS "--enable-${singular}=${selected}")
endforeach()
if(TARGET_OS STREQUAL "win")
    list(APPEND FFMPEG_COMMON_FLAGS --enable-schannel)
elseif(TARGET_OS STREQUAL "osx" OR TARGET_OS STREQUAL "ios")
    list(APPEND FFMPEG_COMMON_FLAGS --enable-securetransport)
else()
    list(APPEND FFMPEG_COMMON_FLAGS --enable-openssl)
endif()

# Per-platform additions
if(TARGET_OS STREQUAL "win")
    if(TARGET_ARCH STREQUAL "x64")
        set(FFMPEG_ARCH "x86_64")
    elseif(TARGET_ARCH STREQUAL "x86")
        set(FFMPEG_ARCH "x86")
    elseif(TARGET_ARCH STREQUAL "arm64")
        set(FFMPEG_ARCH "aarch64")
    endif()

    set(WIN_EXTRA_CFLAGS "-O2 -static -static-libgcc -static-libstdc++")
    set(WIN_EXTRA_LDFLAGS "-static")

    if(TARGET_ARCH STREQUAL "arm64")
        # clangarm64 does not provide the same runtime/linker semantics as GCC-based MinGW.
        set(WIN_EXTRA_CFLAGS "-O2 -static")
        list(APPEND FFMPEG_COMMON_FLAGS --enable-cross-compile)
    endif()

    # LAME MinGW setup
    list(APPEND LAME_COMMON_FLAGS --host=${FFMPEG_ARCH}-w64-mingw32
        "CC=${CMAKE_C_COMPILER}" "AR=${CMAKE_AR}" "RANLIB=${CMAKE_RANLIB}")

    list(APPEND FFMPEG_COMMON_FLAGS
            --target-os=mingw32
            --arch=${FFMPEG_ARCH}
            --cc=${CMAKE_C_COMPILER}
            --host-cc=${CMAKE_C_COMPILER}
            "--extra-cflags=${WIN_EXTRA_CFLAGS}"
            "--extra-ldflags=${WIN_EXTRA_LDFLAGS}")

    if(CMAKE_CROSSCOMPILING)
        list(APPEND FFMPEG_COMMON_FLAGS --cross-prefix=${FFMPEG_ARCH}-w64-mingw32-)
    endif()

elseif(TARGET_OS STREQUAL "osx")
    if(TARGET_ARCH STREQUAL "x64")
        set(FFMPEG_ARCH "x86_64")
        set(MACOS_ARCH_CLANG "x86_64")
        set(MACOS_TARGET "x86_64-apple-darwin")
    else()
        set(FFMPEG_ARCH "aarch64")
        set(MACOS_ARCH_CLANG "arm64")
        set(MACOS_TARGET "aarch64-apple-darwin")
    endif()

    # LAME macOS setup
    list(APPEND LAME_COMMON_FLAGS
            --host=${MACOS_TARGET}
            "CFLAGS=-O2 -g0 -arch ${MACOS_ARCH_CLANG} -mmacosx-version-min=${CMAKE_OSX_DEPLOYMENT_TARGET} -Wno-implicit-function-declaration"
            "LDFLAGS=-arch ${MACOS_ARCH_CLANG} -mmacosx-version-min=${CMAKE_OSX_DEPLOYMENT_TARGET}")

    list(APPEND FFMPEG_COMMON_FLAGS
            --target-os=darwin
            --arch=${FFMPEG_ARCH}
            --enable-cross-compile
            "--extra-cflags=-target ${MACOS_TARGET} -mmacosx-version-min=${CMAKE_OSX_DEPLOYMENT_TARGET}"
            "--extra-ldflags=-target ${MACOS_TARGET} -mmacosx-version-min=${CMAKE_OSX_DEPLOYMENT_TARGET}"
            --enable-runtime-cpudetect)

elseif(TARGET_OS STREQUAL "linux")
    if(TARGET_ARCH STREQUAL "x64")
        set(FFMPEG_ARCH "x86_64")
    elseif(TARGET_ARCH STREQUAL "arm")
        set(FFMPEG_ARCH "arm")
        set(LINUX_CROSS_PREFIX "arm-linux-gnueabihf-")
    else()
        set(FFMPEG_ARCH "aarch64")
        set(LINUX_CROSS_PREFIX "aarch64-linux-gnu-")
    endif()

    # LAME Linux setup
    if(CMAKE_CROSSCOMPILING)
        # remove the trailing dash from prefix to get the host
        string(REGEX REPLACE "-$" "" LAME_HOST "${LINUX_CROSS_PREFIX}")
        list(APPEND LAME_COMMON_FLAGS --host=${LAME_HOST})
    endif()

    list(APPEND FFMPEG_COMMON_FLAGS
            --target-os=linux
            --arch=${FFMPEG_ARCH}
            --enable-pic)

    if(CMAKE_CROSSCOMPILING)
        list(APPEND FFMPEG_COMMON_FLAGS
                --enable-cross-compile
                --cross-prefix=${LINUX_CROSS_PREFIX})
    endif()

elseif(TARGET_OS STREQUAL "android")
    set(ANDROID_API_LEVEL 21)

    # Determine FFmpeg arch and NDK toolchain triple from ANDROID_ABI
    if(ANDROID_ABI STREQUAL "x86_64")
        set(TARGET_ARCH "x64")
        set(FFMPEG_ARCH "x86_64")
        set(ANDROID_TARGET_TRIPLE "x86_64-linux-android")
    elseif(ANDROID_ABI STREQUAL "armeabi-v7a")
        set(TARGET_ARCH "arm")
        set(FFMPEG_ARCH "arm")
        set(ANDROID_TARGET_TRIPLE "armv7a-linux-androideabi")
    elseif(ANDROID_ABI STREQUAL "arm64-v8a")
        set(TARGET_ARCH "arm64")
        set(FFMPEG_ARCH "aarch64")
        set(ANDROID_TARGET_TRIPLE "aarch64-linux-android")
    endif()

    # Get the directory where the NDK toolchain binaries are located
    get_filename_component(NDK_TOOLCHAIN_BIN_DIR ${CMAKE_C_COMPILER} DIRECTORY)

    # Construct the full path to the correct NDK compiler wrapper script
    set(ANDROID_COMPILER_WRAPPER "${NDK_TOOLCHAIN_BIN_DIR}/${ANDROID_TARGET_TRIPLE}${ANDROID_API_LEVEL}-clang")

    # LAME Android setup
    list(APPEND LAME_COMMON_FLAGS
            --host=${ANDROID_TARGET_TRIPLE}
            --with-sysroot=${CMAKE_SYSROOT}
            "CC=${ANDROID_COMPILER_WRAPPER}"
            "AR=${NDK_TOOLCHAIN_BIN_DIR}/llvm-ar"
            "RANLIB=${NDK_TOOLCHAIN_BIN_DIR}/llvm-ranlib")

    list(APPEND FFMPEG_COMMON_FLAGS
            --target-os=android
            --arch=${FFMPEG_ARCH}
            --enable-cross-compile
            --cc=${ANDROID_COMPILER_WRAPPER}
            --ar=${NDK_TOOLCHAIN_BIN_DIR}/llvm-ar
            --ranlib=${NDK_TOOLCHAIN_BIN_DIR}/llvm-ranlib
            --strip=${NDK_TOOLCHAIN_BIN_DIR}/llvm-strip)

elseif(TARGET_OS STREQUAL "ios")
    # LAME iOS setup
    list(APPEND LAME_COMMON_FLAGS
            --host=aarch64-apple-darwin
            "CFLAGS=-O2 -g0 -arch arm64 -isysroot ${CMAKE_OSX_SYSROOT} -miphoneos-version-min=${CMAKE_OSX_DEPLOYMENT_TARGET} -Wno-implicit-function-declaration"
            "LDFLAGS=-arch arm64 -isysroot ${CMAKE_OSX_SYSROOT} -miphoneos-version-min=${CMAKE_OSX_DEPLOYMENT_TARGET}")

    list(APPEND FFMPEG_COMMON_FLAGS
            --target-os=darwin
            --arch=aarch64
            --enable-cross-compile
            --disable-asm
            "--extra-cflags=-arch arm64 -isysroot ${CMAKE_OSX_SYSROOT} -miphoneos-version-min=${CMAKE_OSX_DEPLOYMENT_TARGET}"
            "--extra-ldflags=-arch arm64 -isysroot ${CMAKE_OSX_SYSROOT} -miphoneos-version-min=${CMAKE_OSX_DEPLOYMENT_TARGET}")

elseif(TARGET_OS STREQUAL "freebsd")
    if(TARGET_ARCH STREQUAL "x64")
        set(FFMPEG_ARCH "x86_64")
    else()
        set(FFMPEG_ARCH "aarch64")
    endif()

    list(APPEND FFMPEG_COMMON_FLAGS
            --target-os=freebsd
            --arch=${FFMPEG_ARCH}
            --cc=cc)

    if(CMAKE_CROSSCOMPILING)
        list(APPEND FFMPEG_COMMON_FLAGS --cross-prefix=${FFMPEG_ARCH}-unknown-freebsd-)
    endif()
endif()

# External build of FFmpeg
find_program(BASH_EXECUTABLE bash REQUIRED)

find_program(MAKE_EXECUTABLE NAMES gmake make HINTS "C:/msys64/usr/bin" "/usr/bin")
if(NOT MAKE_EXECUTABLE)
    message(FATAL_ERROR "Could not find 'make' or 'gmake'. It is required to build FFmpeg/LAME via autotools.")
endif()
message(STATUS "Using Make program: ${MAKE_EXECUTABLE}")

include(ExternalProject)

# LAME Dependency
ExternalProject_Add(lame_dependency
    URL               ${LAME_URL}
    URL_HASH          SHA256=${LAME_SHA256}
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE
    PREFIX            ${CMAKE_BINARY_DIR}/lame
    INSTALL_DIR       ${LAME_INSTALL_DIR}
    BUILD_IN_SOURCE   1
    CONFIGURE_COMMAND ${CMAKE_COMMAND} -E env "CFLAGS=-O2 -g0 -fPIC" ${BASH_EXECUTABLE} <SOURCE_DIR>/configure
                      --prefix=${LAME_INSTALL_DIR}
                      ${LAME_COMMON_FLAGS}
    BUILD_COMMAND     ${MAKE_EXECUTABLE} -j${CMAKE_BUILD_PARALLEL_LEVEL}
    INSTALL_COMMAND   ${MAKE_EXECUTABLE} install
    LOG_OUTPUT_ON_FAILURE 1
    LOG_DOWNLOAD      1
    LOG_CONFIGURE     1
    LOG_BUILD         1
    LOG_INSTALL       1
)

# All third-party archives are static and installed below this build tree.
set(AUDIO_HOST_FLAGS "")
foreach(flag IN LISTS LAME_COMMON_FLAGS)
    if(flag MATCHES "^(--host=|--with-sysroot=|CFLAGS=|LDFLAGS=|CC=|AR=|RANLIB=)")
        list(APPEND AUDIO_HOST_FLAGS "${flag}")
    endif()
endforeach()
function(add_audio_dependency name)
    string(JSON url GET "${AUDIO_PROFILE}" dependencies ${name} url)
    string(JSON sha GET "${AUDIO_PROFILE}" dependencies ${name} sha256)
    ExternalProject_Add(${name}_dependency
        URL "${url}" URL_HASH "SHA256=${sha}" DOWNLOAD_EXTRACT_TIMESTAMP TRUE
        PREFIX "${CMAKE_BINARY_DIR}/${name}" BUILD_IN_SOURCE 1
        PATCH_COMMAND ${CMAKE_COMMAND} -DNAME=${name} -DSOURCE=<SOURCE_DIR>
            -P ${CMAKE_CURRENT_SOURCE_DIR}/PatchDependency.cmake
        CONFIGURE_COMMAND ${CMAKE_COMMAND} -E env "CFLAGS=-O2 -g0 -fPIC"
            "PKG_CONFIG_PATH=${AUDIO_INSTALL_DIR}/lib/pkgconfig"
            ${BASH_EXECUTABLE} <SOURCE_DIR>/configure --prefix=${AUDIO_INSTALL_DIR}
            --disable-shared --enable-static --with-pic --disable-maintainer-mode ${AUDIO_HOST_FLAGS} ${ARGN}
        BUILD_COMMAND ${MAKE_EXECUTABLE} -j${CMAKE_BUILD_PARALLEL_LEVEL} "noinst_PROGRAMS="
        INSTALL_COMMAND ${MAKE_EXECUTABLE} install
        LOG_DOWNLOAD 1 LOG_CONFIGURE 1 LOG_BUILD 1 LOG_INSTALL 1 LOG_OUTPUT_ON_FAILURE 1)
endfunction()
add_audio_dependency(ogg)
add_audio_dependency(vorbis --disable-docs --disable-examples)
add_dependencies(vorbis_dependency ogg_dependency)
add_audio_dependency(opus --disable-doc --disable-extra-programs)
set(AUDIO_DEPENDENCIES lame_dependency vorbis_dependency opus_dependency)
if(TARGET_OS STREQUAL "linux" OR TARGET_OS STREQUAL "android")
    string(JSON ssl_url GET "${AUDIO_PROFILE}" dependencies openssl url)
    string(JSON ssl_sha GET "${AUDIO_PROFILE}" dependencies openssl sha256)
    if(TARGET_OS STREQUAL "android")
        set(SSL_TARGET android-arm64)
        set(SSL_ENV "PATH=${NDK_TOOLCHAIN_BIN_DIR}:$ENV{PATH}" "ANDROID_NDK_ROOT=$ENV{ANDROID_NDK_ROOT}")
        set(SSL_FLAGS -D__ANDROID_API__=${ANDROID_API_LEVEL})
    else()
        set(SSL_TARGET linux-x86_64)
        if(TARGET_ARCH STREQUAL "arm64")
            set(SSL_TARGET linux-aarch64)
        endif()
        set(SSL_ENV "CC=${CMAKE_C_COMPILER}" "AR=${CMAKE_AR}" "RANLIB=${CMAKE_RANLIB}")
    endif()
    ExternalProject_Add(openssl_dependency
        URL "${ssl_url}" URL_HASH "SHA256=${ssl_sha}" DOWNLOAD_EXTRACT_TIMESTAMP TRUE
        PREFIX "${CMAKE_BINARY_DIR}/openssl" BUILD_IN_SOURCE 1
        CONFIGURE_COMMAND ${CMAKE_COMMAND} -E env ${SSL_ENV} perl <SOURCE_DIR>/Configure
            ${SSL_TARGET} --prefix=${AUDIO_INSTALL_DIR} --libdir=lib --openssldir=/etc/ssl
            no-shared no-tests no-apps no-module no-engine no-legacy no-dso -O2 -g0 -fPIC ${SSL_FLAGS}
        BUILD_COMMAND ${CMAKE_COMMAND} -E env ${SSL_ENV} ${MAKE_EXECUTABLE} -j${CMAKE_BUILD_PARALLEL_LEVEL}
        INSTALL_COMMAND ${CMAKE_COMMAND} -E env ${SSL_ENV} ${MAKE_EXECUTABLE} install_sw
        LOG_DOWNLOAD 1 LOG_CONFIGURE 1 LOG_BUILD 1 LOG_INSTALL 1 LOG_OUTPUT_ON_FAILURE 1)
    list(APPEND AUDIO_DEPENDENCIES openssl_dependency)
endif()

ExternalProject_Add(ffmpeg_dependency
        DEPENDS           ${AUDIO_DEPENDENCIES}
        URL               ${FFMPEG_URL}
        URL_HASH          SHA256=${FFMPEG_SHA256}
        DOWNLOAD_EXTRACT_TIMESTAMP TRUE
        PREFIX            ${CMAKE_BINARY_DIR}/ffmpeg
        INSTALL_DIR       ${FFMPEG_INSTALL_DIR}
        BUILD_IN_SOURCE   1
        CONFIGURE_COMMAND ${CMAKE_COMMAND} -E env
        "PKG_CONFIG_PATH=${AUDIO_INSTALL_DIR}/lib/pkgconfig"
        ${BASH_EXECUTABLE} <SOURCE_DIR>/configure
        --prefix=${FFMPEG_INSTALL_DIR}
        ${FFMPEG_COMMON_FLAGS}
        BUILD_COMMAND     ${MAKE_EXECUTABLE} -j${CMAKE_BUILD_PARALLEL_LEVEL}
        INSTALL_COMMAND   ${MAKE_EXECUTABLE} install
        LOG_OUTPUT_ON_FAILURE 1
        LOG_DOWNLOAD      1
        LOG_CONFIGURE     1
        LOG_BUILD         1
        LOG_INSTALL       1
)

# SoundFlow library
add_library(soundflow-ffmpeg SHARED
        soundflow-ffmpeg.c
        soundflow-ffmpeg.h)

add_dependencies(soundflow-ffmpeg ffmpeg_dependency)

# Naming / output
if(TARGET_OS STREQUAL "win")
    set_target_properties(soundflow-ffmpeg PROPERTIES
            PREFIX "" SUFFIX ".dll" OUTPUT_NAME "soundflow-ffmpeg")
elseif(TARGET_OS STREQUAL "linux" OR TARGET_OS STREQUAL "freebsd" OR TARGET_OS STREQUAL "android")
    set_target_properties(soundflow-ffmpeg PROPERTIES
            PREFIX "lib" SUFFIX ".so" OUTPUT_NAME "soundflow-ffmpeg")
elseif(TARGET_OS STREQUAL "osx")
    set_target_properties(soundflow-ffmpeg PROPERTIES
            PREFIX "lib" SUFFIX ".dylib" OUTPUT_NAME "soundflow-ffmpeg")
elseif(TARGET_OS STREQUAL "ios")
    set_target_properties(soundflow-ffmpeg PROPERTIES
            FRAMEWORK TRUE
            OUTPUT_NAME "soundflow-ffmpeg"
            FRAMEWORK_VERSION A
            MACOSX_FRAMEWORK_IDENTIFIER com.soundflow.ffmpeg)
endif()

set_target_properties(soundflow-ffmpeg PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY  "${OUTPUT_DIR}"
        LIBRARY_OUTPUT_DIRECTORY  "${OUTPUT_DIR}"
        ARCHIVE_OUTPUT_DIRECTORY  "${OUTPUT_DIR}")

# Include / link against FFmpeg
target_include_directories(soundflow-ffmpeg PRIVATE
        ${FFMPEG_INSTALL_DIR}/include)

target_link_directories(soundflow-ffmpeg PRIVATE
        ${FFMPEG_INSTALL_DIR}/lib
        ${LAME_INSTALL_DIR}/lib
        ${AUDIO_INSTALL_DIR}/lib)

# Link libraries by name.
target_link_libraries(soundflow-ffmpeg PRIVATE
        avformat avcodec swresample avutil
        mp3lame vorbisenc vorbis ogg opus)
if(TARGET_OS STREQUAL "linux" OR TARGET_OS STREQUAL "android")
    target_link_libraries(soundflow-ffmpeg PRIVATE ssl crypto)
endif()

# Platform system libraries
if(TARGET_OS STREQUAL "win")
    target_link_libraries(soundflow-ffmpeg PRIVATE
            bcrypt secur32 ncrypt crypt32 ws2_32 iphlpapi)
    target_link_options(soundflow-ffmpeg PRIVATE
            -static -static-libgcc -static-libstdc++)
elseif(TARGET_OS STREQUAL "osx" OR TARGET_OS STREQUAL "ios")
    find_library(CORE_FOUNDATION_FRAMEWORK CoreFoundation)
    find_library(SECURITY_FRAMEWORK Security REQUIRED)
    target_link_libraries(soundflow-ffmpeg PRIVATE
            m pthread
            ${CORE_FOUNDATION_FRAMEWORK}
            ${SECURITY_FRAMEWORK})
elseif(TARGET_OS STREQUAL "linux" OR TARGET_OS STREQUAL "freebsd")
    target_link_libraries(soundflow-ffmpeg PRIVATE m pthread)
elseif(TARGET_OS STREQUAL "android")
    target_link_libraries(soundflow-ffmpeg PRIVATE m atomic)
    target_link_options(soundflow-ffmpeg PRIVATE "-Wl,-z,max-page-size=16384")
endif()
