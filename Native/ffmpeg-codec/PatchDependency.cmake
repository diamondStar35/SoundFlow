# libvorbis 1.3.7 passes an obsolete Apple linker option even when CFLAGS are supplied.
# Patch the release configure script before running it; source hashes are checked before this step.
if(NAME STREQUAL "vorbis")
    file(READ "${SOURCE}/configure" script)
    string(REPLACE "-force_cpusubtype_ALL" "" script "${script}")
    file(WRITE "${SOURCE}/configure" "${script}")
endif()
