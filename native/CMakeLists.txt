cmake_minimum_required(VERSION 3.21)

# The version comes from the release tag in CI (-DBC_VERSION=1.2.3).
set(BC_VERSION "0.3.1" CACHE STRING "Module version")
project(gmsv_betterconsole VERSION ${BC_VERSION} LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()

if(NOT WIN32)
  message(FATAL_ERROR "gmsv_betterconsole is Windows only (it talks to BetterConsole.exe over a named pipe)")
endif()

# Static CRT: a game server machine may not have the matching Visual C++ runtime.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

if(CMAKE_SIZEOF_VOID_P EQUAL 8)
  set(BC_PLATFORM "win64")
else()
  set(BC_PLATFORM "win32")
endif()

add_library(betterconsole SHARED
  src/module.cpp
  src/pipe_client.cpp
  src/pipe_client.h
  src/engine.cpp
  src/engine.h)

target_include_directories(betterconsole PRIVATE
  "${CMAKE_CURRENT_SOURCE_DIR}/third_party/gmod-module-base/include")

target_compile_definitions(betterconsole PRIVATE
  BC_VERSION="${PROJECT_VERSION}"
  WIN32_LEAN_AND_MEAN NOMINMAX _CRT_SECURE_NO_WARNINGS _WIN32_WINNT=0x0601)

if(MSVC)
  # /EHa: the console-command walk reads engine memory under SEH.
  target_compile_options(betterconsole PRIVATE /W4 /permissive- /utf-8 /EHa /O2)
endif()

set_target_properties(betterconsole PROPERTIES
  PREFIX ""
  OUTPUT_NAME "gmsv_betterconsole_${BC_PLATFORM}"
  SUFFIX ".dll"
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
  LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
