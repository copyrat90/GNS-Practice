cmake_minimum_required(VERSION 3.25)
project(GNS-Practice)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED TRUE)
set(CMAKE_CXX_EXTENSIONS FALSE)

find_package(Protobuf REQUIRED)

add_subdirectory(GameNetworkingSockets)

add_subdirectory(00_Chat)
