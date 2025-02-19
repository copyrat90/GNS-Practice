
# Compile *.proto with protoc executable
set(PROTO_DIR "${CMAKE_CURRENT_LIST_DIR}/../Proto")
file(GLOB PROTO_FILES "${PROTO_DIR}/*.proto")
foreach(PROTO_FILE ${PROTO_FILES})
    get_filename_component(FILE_BASENAME ${PROTO_FILE} NAME_WLE)
    set(PROTO_SRC "${PROTO_DIR}/${FILE_BASENAME}.pb.cc")
    set(PROTO_HDR "${PROTO_DIR}/${FILE_BASENAME}.pb.h")

    add_custom_command(OUTPUT "${PROTO_SRC}" "${PROTO_HDR}"
        COMMAND ${Protobuf_PROTOC_EXECUTABLE}
        ARGS --cpp_out="${PROTO_DIR}" -I"${PROTO_DIR}" "${FILE_BASENAME}.proto"
        DEPENDS ${PROTO_FILE}
        COMMENT "Processed ${PROTO_DIR}/${FILE_BASENAME}.proto"
    )
    list(APPEND PROTO_SRCS ${PROTO_SRC})
endforeach()

add_executable(st_chat_server st_chat_server.cpp ${PROTO_SRCS})

target_include_directories(st_chat_server PRIVATE ${PROTO_DIR})
target_link_libraries(st_chat_server PRIVATE GameNetworkingSockets::static)
