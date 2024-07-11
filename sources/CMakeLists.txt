cmake_minimum_required(VERSION 3.22)

message(STATUS "----------------------------------------------------------------")
message(STATUS "uw-match-admin")
message(STATUS "----------------------------------------------------------------")

file(GLOB match-admin-list "*.cpp" "*.h")
add_executable(match-admin ${match-admin-list})
target_link_libraries(match-admin cage-core)
cage_ide_category(match-admin unnatural)
cage_ide_working_dir_in_place(match-admin)
