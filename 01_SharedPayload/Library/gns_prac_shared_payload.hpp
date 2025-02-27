#pragma once

#ifdef _WIN32
#define GNS_PRAC_INTERFACE extern "C" __declspec(dllexport)
#else
#define GNS_PRAC_INTERFACE extern "C"
#endif

#include <cstdint>

struct SteamNetworkingMessage_t;

/// @brief Allocates the shared payload with a hidden reference count.
/// @param size Size to allocate space.
/// @return Allocated space if it succeeded, otherwise `nullptr`.
GNS_PRAC_INTERFACE void* gns_prac_allocate_shared_payload(std::int32_t size);

/// @brief Adds the shared payload to the message.
///
/// This increases the reference count of the payload.
///
/// You MUST use the shared payload allocated with `allocate_shared_payload()`, nothing else.
/// @param msg Message to add the payload to.
/// @param payload Payload to add to.
/// @param size Size of the payload.
GNS_PRAC_INTERFACE void gns_prac_add_shared_payload_to_message(SteamNetworkingMessage_t* msg, void* payload,
                                                               std::int32_t size);

/// @brief Removes the shared payload from the message.
///
/// This decreases the reference count of the payload, and deallocates the payload if the ref count reaches zero.
///
/// This is a callback function which automatically set when you call `add_shared_payload_to_message()`,
/// so you don't need to use this function directly.
/// @param msg Message to remove the payload from.
GNS_PRAC_INTERFACE void gns_prac_remove_shared_payload_from_message(SteamNetworkingMessage_t* msg);

/// @brief Force deallocate the shared payload.
///
/// This is only necessary if you have an exception in your program
/// which prevents sending the message with already allocated shared payload.
/// @param payload Payload to deallocate.
GNS_PRAC_INTERFACE void gns_prac_force_deallocate_shared_payload(void* payload);
