# jdyc-core：跨客户端共享核心（C ABI 动态库）
# 由 MSYS2 MinGW-w64 工具链编译为 jdyc-core.dll
cmake_minimum_required(VERSION 3.15)
project(jdyc-core C)

set(CMAKE_C_STANDARD 99)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

add_library(jdyc-core SHARED
  src/core.c
  src/process.c
  src/tap.c
)

target_include_directories(jdyc-core PUBLIC include PRIVATE src)

if(WIN32)
  target_link_libraries(jdyc-core PRIVATE advapi32)
  # unicode 宽字符 API
  target_compile_definitions(jdyc-core PRIVATE UNICODE _UNICODE)
endif()

set_target_properties(jdyc-core PROPERTIES
  OUTPUT_NAME "jdyc-core"
  PREFIX ""          # 直接输出 jdyc-core.dll（去掉 lib 前缀）
)
