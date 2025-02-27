
# shared payload library
add_library(gns_prac_shared_payload SHARED Library/gns_prac_shared_payload.cpp)
target_include_directories(gns_prac_shared_payload PUBLIC Library/
"$<TARGET_PROPERTY:GameNetworkingSockets,INTERFACE_INCLUDE_DIRECTORIES>"
)
target_compile_options(gns_prac_shared_payload PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4 /Zc:preprocessor /Zc:__cplusplus /utf-8>
    $<$<CXX_COMPILER_ID:GNU>:-Wall -Wextra -Wpedantic>
    $<$<CXX_COMPILER_ID:Clang>:-Wall -Wextra -Wpedantic>
)

# validate


# benchmark
