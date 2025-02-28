
set(comp_opts
    $<$<CXX_COMPILER_ID:MSVC>:/W4 /Zc:preprocessor /Zc:__cplusplus /utf-8>
    $<$<CXX_COMPILER_ID:GNU>:-Wall -Wextra -Wpedantic>
    $<$<CXX_COMPILER_ID:Clang>:-Wall -Wextra -Wpedantic>
)

# shared payload library
add_library(gns_prac_shared_payload SHARED Library/gns_prac_shared_payload.cpp)
target_include_directories(gns_prac_shared_payload PUBLIC Library/
"$<TARGET_PROPERTY:GameNetworkingSockets,INTERFACE_INCLUDE_DIRECTORIES>"
)
target_compile_options(gns_prac_shared_payload PRIVATE ${comp_opts})

# validate
include(CTest)
enable_testing()

set(MSVC_SANITIZER FALSE)
if(MSVC)
    set(MSVC_SANITIZER TRUE)
endif()

set(GCC_SANITIZER FALSE)
if(NOT WIN32)
    if(${CMAKE_CXX_COMPILER_ID} MATCHES "Clang" OR ${CMAKE_CXX_COMPILER_ID} STREQUAL "GNU")
        set(GCC_SANITIZER TRUE)
    endif()
endif()

add_executable(shared_payload_validate ValidateCpp/shared_payload_validate.cpp)
target_compile_options(shared_payload_validate PRIVATE ${comp_opts})
target_link_libraries(shared_payload_validate PRIVATE gns_prac_shared_payload GameNetworkingSockets::static)
if(MSVC_SANITIZER)
    target_compile_options(shared_payload_validate PRIVATE /fsanitize=address)
    target_compile_definitions(shared_payload_validate PRIVATE _DISABLE_STRING_ANNOTATION=1 _DISABLE_VECTOR_ANNOTATION=1)
    target_link_options(shared_payload_validate PRIVATE /DEBUG /INCREMENTAL:NO)
elseif(GCC_SANITIZER)
    target_compile_options(shared_payload_validate PRIVATE -fsanitize=address)
    target_link_options(shared_payload_validate PRIVATE -fsanitize=address)

    add_executable(shared_payload_validate_tsan ValidateCpp/shared_payload_validate.cpp)
    target_compile_options(shared_payload_validate_tsan PRIVATE ${comp_opts})
    target_link_libraries(shared_payload_validate_tsan PRIVATE gns_prac_shared_payload GameNetworkingSockets::static)
    target_compile_options(shared_payload_validate_tsan PRIVATE -fsanitize=thread)
    target_link_options(shared_payload_validate_tsan PRIVATE -fsanitize=thread)
endif()

add_test(NAME test_shared_payload_validate COMMAND shared_payload_validate)

# benchmark
