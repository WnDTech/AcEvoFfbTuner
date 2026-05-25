cmake_minimum_required(VERSION 3.20)
project(sc_bridge VERSION 1.0.0)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED TRUE)

add_library(sc_bridge SHARED sc_bridge.cpp)

set(SC_API_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../sc-api")
target_include_directories(sc_bridge PRIVATE ${SC_API_DIR}/inc ${SC_API_DIR}/core/inc)
target_link_directories(sc_bridge PRIVATE 
    "${SC_API_DIR}/build/Release"
    "${SC_API_DIR}/build/core/Release"
    "${SC_API_DIR}/build/core/src/libeddsa/Release"
)
target_link_libraries(sc_bridge PRIVATE sc-api sc-api-core libeddsa ws2_32)

add_custom_command(TARGET sc_bridge POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${SC_API_DIR}/build/core/Release/sc-api-core.lib"
        $<TARGET_FILE_DIR:sc_bridge>
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${SC_API_DIR}/build/core/src/libeddsa/Release/libeddsa.lib"
        $<TARGET_FILE_DIR:sc_bridge>
)

target_compile_definitions(sc_bridge PRIVATE SC_API_DEFINE_WINNT)
