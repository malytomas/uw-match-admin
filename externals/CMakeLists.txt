cmake_minimum_required(VERSION 3.22)

file(GLOB uwapi-list "uwapi/csharp/uwapi/*.cs")
add_library(uwapi SHARED ${uwapi-list})
set_target_properties(uwapi PROPERTIES VS_GLOBAL_ROOTNAMESPACE "Unnatural")
set_target_properties(uwapi PROPERTIES VS_DOTNET_REFERENCES "System;System.Memory;System.Text.Json;Microsoft.CSharp.dll")

