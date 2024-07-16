cmake_minimum_required(VERSION 3.22)

file(GLOB match-admin-list "*.cs")
add_executable(match-admin ${match-admin-list})
target_link_libraries(match-admin uwapi)
set_target_properties(match-admin PROPERTIES VS_GLOBAL_ROOTNAMESPACE "Unnatural")
set_target_properties(match-admin PROPERTIES VS_DOTNET_REFERENCES "System;Microsoft.CSharp.dll")
set_target_properties(match-admin PROPERTIES VS_PACKAGE_REFERENCES "CommandLineParser_2.9.*")
set_property(DIRECTORY "${PROJECT_SOURCE_DIR}" PROPERTY VS_STARTUP_PROJECT match-admin)
