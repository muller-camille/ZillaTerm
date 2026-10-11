# Compilation croisée de FreeRDP pour Windows x64 avec mingw-w64 (modèle de threads POSIX), depuis Linux.
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR AMD64)

set(CMAKE_C_COMPILER x86_64-w64-mingw32-gcc-posix)
set(CMAKE_CXX_COMPILER x86_64-w64-mingw32-g++-posix)
set(CMAKE_RC_COMPILER x86_64-w64-mingw32-windres)
set(CMAKE_AR x86_64-w64-mingw32-gcc-ar-posix)
set(CMAKE_RANLIB x86_64-w64-mingw32-gcc-ranlib-posix)

set(CMAKE_FIND_ROOT_PATH /usr/x86_64-w64-mingw32)
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY BOTH)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE BOTH)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE BOTH)

set(CMAKE_WINDOWS_VERSION "WIN10" CACHE STRING "")

# mingw-w64 n'a ni threads.h ni atomics C11.
add_compile_definitions(__STDC_NO_THREADS__)
add_compile_definitions(__STDC_NO_ATOMICS__)
