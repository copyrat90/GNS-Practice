#include "gns_prac_shared_payload.hpp"

#include <steam/steamnetworkingtypes.h>

#include <atomic>
#include <cstdlib>

#ifdef _MSC_VER // MSVCRT
#define GNS_PRAC_ALIGNED_ALLOC(alignment, size) _aligned_malloc(size, alignment)
#define GNS_PRAC_ALIGNED_FREE(ptr) _aligned_free(ptr)
#else // standard
#define GNS_PRAC_ALIGNED_ALLOC(alignment, size) std::aligned_alloc(alignment, size)
#define GNS_PRAC_ALIGNED_FREE(ptr) std::free(ptr)
#endif

using ref_count_t = std::atomic_int32_t;

GNS_PRAC_INTERFACE void* gns_prac_allocate_shared_payload(std::int32_t size)
{
    if (size <= 0)
        return nullptr;

    // allocate space for (ref count + payload size)
    void* ptr = GNS_PRAC_ALIGNED_ALLOC(alignof(ref_count_t), sizeof(ref_count_t) + size);
    if (!ptr)
        return nullptr;

    // use the front space as a ref count
    ref_count_t* ref_count = ::new (static_cast<void*>(ptr)) ref_count_t;
    ;
#if __cplusplus < 202002L // explicit zero init required before C++20
    ref_count->store(0, std::memory_order_relaxed);
#else
    ((void)ref_count); // suppress unused variable warning
#endif

    // return the payload space (i.e. ref count is hidden)
    return (std::byte*)ptr + sizeof(ref_count_t);
}

GNS_PRAC_INTERFACE void gns_prac_add_shared_payload_to_message(SteamNetworkingMessage_t* msg, void* payload,
                                                               std::int32_t size)
{
    // ref count should exist before the payload
    ref_count_t* ref_count = reinterpret_cast<ref_count_t*>((std::byte*)payload - sizeof(ref_count_t));

    // increase the ref count
    ref_count->fetch_add(1, std::memory_order_relaxed);

    // add the payload to the message
    msg->m_pData = payload;
    msg->m_cbSize = size;
    msg->m_pfnFreeData = gns_prac_remove_shared_payload_from_message;
}

GNS_PRAC_INTERFACE void gns_prac_remove_shared_payload_from_message(SteamNetworkingMessage_t* msg)
{
    // ref count should exist before the payload
    ref_count_t* ref_count = reinterpret_cast<ref_count_t*>((std::byte*)msg->m_pData - sizeof(ref_count_t));

    // if this was the last shared reference
    if (1 == ref_count->fetch_sub(1, std::memory_order_relaxed))
    {
        // destroy the ref count
        ref_count->~ref_count_t();

        // free the space (ref count is the alloc address)
        GNS_PRAC_ALIGNED_FREE(ref_count);
    }
}

GNS_PRAC_INTERFACE void gns_prac_force_deallocate_shared_payload(void* payload)
{
    // ref count should exist before the payload
    ref_count_t* ref_count = reinterpret_cast<ref_count_t*>((std::byte*)payload - sizeof(ref_count_t));

    // destroy the ref count
    ref_count->~ref_count_t();

    // free the space (ref count is the alloc address)
    GNS_PRAC_ALIGNED_FREE(ref_count);
}
