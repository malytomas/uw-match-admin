cmake_minimum_required(VERSION 3.22)
project(uw-match-admin CXX)

list(APPEND CMAKE_MODULE_PATH ${CMAKE_CURRENT_SOURCE_DIR}/externals/cage/cmake)
include(cage_all)
cage_default_init()

add_subdirectory(externals)
add_subdirectory(sources)

