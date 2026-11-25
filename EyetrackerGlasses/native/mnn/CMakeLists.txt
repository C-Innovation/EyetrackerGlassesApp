cmake_minimum_required(VERSION 3.24)
project(EyetrackerMnn VERSION 1.0.0 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(MNN_SOURCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/_deps/MNN" CACHE PATH "Pinned MNN source tree")
option(EYETRACKER_MNN_CONVERTER "Build the offline ONNX converter" OFF)

if(NOT EXISTS "${MNN_SOURCE_DIR}/include/MNN/Interpreter.hpp")
  message(FATAL_ERROR "Run build.py to fetch the pinned MNN source first")
endif()

# A single portable CPU library. No OpenCV, CUDA, OpenCL, Metal or external GPU SDK.
set(MNN_BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(MNN_SEP_BUILD OFF CACHE BOOL "" FORCE)
set(MNN_WIN_RUNTIME_MT ON CACHE BOOL "" FORCE)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
foreach(option_name MNN_BUILD_TOOLS MNN_BUILD_DEMO MNN_BUILD_TEST MNN_BUILD_BENCHMARK
    MNN_BUILD_QUANTOOLS MNN_BUILD_TRAIN MNN_BUILD_LLM MNN_BUILD_DIFFUSION MNN_BUILD_OPENCV
    MNN_BUILD_AUDIO MNN_OPENMP MNN_CUDA MNN_OPENCL MNN_VULKAN MNN_METAL MNN_NPU
    MNN_COREML MNN_NNAPI MNN_QNN MNN_SME2 MNN_ARM82 MNN_AAPL_FMWK)
  set(${option_name} OFF CACHE BOOL "" FORCE)
endforeach()
set(MNN_BUILD_CONVERTER ${EYETRACKER_MNN_CONVERTER} CACHE BOOL "" FORCE)
set(MNN_BUILD_FOR_ANDROID_COMMAND ON CACHE BOOL "" FORCE)
if(MSVC AND CMAKE_VS_PLATFORM_NAME MATCHES "^ARM64")
  # MNN uses the host processor to select x86 kernels under the VS generator.
  # The portable ARM64 path avoids GNU assembly on the MSVC toolchain.
  set(MNN_USE_SSE OFF CACHE BOOL "" FORCE)
  set(ARCHS generic)
elseif(APPLE AND CMAKE_OSX_ARCHITECTURES)
  set(ARCHS "${CMAKE_OSX_ARCHITECTURES}")
endif()
add_subdirectory("${MNN_SOURCE_DIR}" mnn EXCLUDE_FROM_ALL)
if(EYETRACKER_MNN_CONVERTER)
  # Expose the excluded subproject's target in the top-level Visual Studio solution.
  add_custom_target(mnn_converter DEPENDS MNNConvert)
endif()

set(bridge_type SHARED)
if(CMAKE_SYSTEM_NAME STREQUAL "iOS")
  set(bridge_type STATIC)
endif()
add_library(eyetracker_mnn ${bridge_type} mnn_bridge.cpp)
target_include_directories(eyetracker_mnn PRIVATE "${MNN_SOURCE_DIR}/include")
target_compile_definitions(eyetracker_mnn PRIVATE MNN_BUILD_STATIC_LIBS)
set_target_properties(eyetracker_mnn PROPERTIES CXX_VISIBILITY_PRESET hidden VISIBILITY_INLINES_HIDDEN YES)
# Retain MNN's static operator/backend registrars on every linker.
if(CMAKE_SYSTEM_NAME STREQUAL "iOS")
  target_link_libraries(eyetracker_mnn PRIVATE MNN)
else()
  target_link_libraries(eyetracker_mnn PRIVATE "$<LINK_LIBRARY:WHOLE_ARCHIVE,MNN>")
endif()
if(ANDROID)
  # Android 15+ supports 16 KiB pages. Static libc++ avoids an extra APK dependency.
  target_link_options(eyetracker_mnn PRIVATE "-Wl,-z,max-page-size=16384")
endif()
if(UNIX AND NOT APPLE)
  target_link_options(eyetracker_mnn PRIVATE "-Wl,-z,relro,-z,now" "-Wl,--exclude-libs,ALL")
endif()

install(TARGETS eyetracker_mnn RUNTIME DESTINATION . LIBRARY DESTINATION . ARCHIVE DESTINATION .)
if(CMAKE_SYSTEM_NAME STREQUAL "iOS")
  # .NET iOS must force-load both static archives.
  install(FILES "$<TARGET_FILE:MNN>" DESTINATION .)
endif()
install(FILES "${MNN_SOURCE_DIR}/LICENSE.txt" DESTINATION . RENAME MNN-LICENSE.txt)
