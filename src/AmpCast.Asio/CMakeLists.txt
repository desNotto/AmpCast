cmake_minimum_required(VERSION 3.20)
project(AmpCastAsio LANGUAGES CXX)

# Build twice: -A x64 (64-bit DAWs) and -A Win32 (32-bit DAWs).
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Static CRT: the DLL is loaded into foreign DAW processes, it must not
# depend on a VC++ redistributable being installed.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_library(AmpCastAsio SHARED
    src/asio_abi.h
    src/SharedBus.h
    src/SharedBus.cpp
    src/ShareRenderer.h
    src/ShareRenderer.cpp
    src/AmpCastAsio.h
    src/AmpCastAsio.cpp
    src/dllmain.cpp
    AmpCastAsio.def)

target_compile_definitions(AmpCastAsio PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX _CRT_SECURE_NO_WARNINGS)
target_compile_options(AmpCastAsio PRIVATE /W4 /permissive- /utf-8 /O2)
target_link_libraries(AmpCastAsio PRIVATE ole32 advapi32 uuid user32 avrt)

if(CMAKE_SIZEOF_VOID_P EQUAL 8)
    set_target_properties(AmpCastAsio PROPERTIES OUTPUT_NAME AmpCastAsio64)
else()
    set_target_properties(AmpCastAsio PROPERTIES OUTPUT_NAME AmpCastAsio32)
endif()
