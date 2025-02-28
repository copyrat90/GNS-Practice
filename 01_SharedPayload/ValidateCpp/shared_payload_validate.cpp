#include "../Library/gns_prac_shared_payload.hpp"

#include <steam/isteamnetworkingutils.h>
#include <steam/steamnetworkingsockets.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <format>
#include <iostream>
#include <mutex>
#include <ranges>
#include <stdexcept>
#include <thread>
#include <vector>

constexpr std::uint16_t SERVER_PORT = 45700;
constexpr int CLIENTS = 95;
constexpr int MESSAGES = 950;
constexpr int MSG_SIZE = 256;

void do_server();
void do_clients();
void do_callback();

void on_server_connection_status_changed(SteamNetConnectionStatusChangedCallback_t*);
void on_client_connection_status_changed(SteamNetConnectionStatusChangedCallback_t*);

void debug_output(ESteamNetworkingSocketsDebugOutputType nType, const char* pszMsg);

std::atomic<int> phase = 0;

enum Phase
{
    INIT = 0,
    SERVER_LISTENING = 1,
    ALL_CLIENT_CONNECTED = 2,
    CLOSING_CONNECTIONS = 3,
    QUIT = 4,
};

std::mutex s_clients_lock;
std::vector<HSteamNetConnection> s_clients; // server side

using namespace std::chrono_literals;

int main()
{
    std::cout << "01_SharedPayload validate test" << '\n';
    std::cout << "clients=" << CLIENTS << ", msgs=" << MESSAGES << ", msg_size=" << MSG_SIZE << '\n';

    // Initialize `GameNetworkingSockets`
    {
        SteamDatagramErrMsg err_msg;
        if (!GameNetworkingSockets_Init(nullptr, err_msg))
        {
            std::cerr << err_msg << '\n';
            throw std::runtime_error(err_msg);
        }
    }
    SteamNetworkingUtils()->SetDebugOutputFunction(k_ESteamNetworkingSocketsDebugOutputType_Msg, debug_output);

    std::thread callback_thread(do_callback);
    std::thread server_thread(do_server);
    std::thread client_thread(do_clients);

    server_thread.join();
    client_thread.join();

    phase.store(QUIT);

    callback_thread.join();

    GameNetworkingSockets_Kill();

    std::cout << "All is well!\n";
}

void do_server()
{
    // Setup configuration used for listen socket
    SteamNetworkingConfigValue_t server_configs[1]{};
    server_configs[0].SetPtr(k_ESteamNetworkingConfig_Callback_ConnectionStatusChanged,
                             (void*)on_server_connection_status_changed);

    // Start listening
    SteamNetworkingIPAddr listen_addr{};
    listen_addr.m_port = SERVER_PORT;
    HSteamListenSocket listen_socket = SteamNetworkingSockets()->CreateListenSocketIP(listen_addr, 1, server_configs);
    if (listen_socket == k_HSteamListenSocket_Invalid)
    {
        std::cerr << "Failed to create a listen socket\n";
        throw std::runtime_error("Failed to create a listen socket");
    }

    phase.store(SERVER_LISTENING);
    phase.notify_all();
    phase.wait(SERVER_LISTENING);

    // Send messages to client
    std::vector<SteamNetworkingMessage_t*> server_msgs;
    std::vector<int64> send_results(CLIENTS);
    server_msgs.reserve(CLIENTS);
    for (int i = 0; i < MESSAGES; ++i)
    {
        server_msgs.clear();

        // Prepare shared payload
        void* payload = gns_prac_allocate_shared_payload(MSG_SIZE);
        std::memset(payload, i % 256, MSG_SIZE);

        // Prepare messages for each client with shared payload
        s_clients_lock.lock();
        for (const auto client : s_clients)
        {
            SteamNetworkingMessage_t* msg = SteamNetworkingUtils()->AllocateMessage(0);
            gns_prac_add_shared_payload_to_message(msg, payload, MSG_SIZE);
            msg->m_conn = client;
            msg->m_nFlags = k_nSteamNetworkingSend_Reliable;
            server_msgs.push_back(msg);
        }
        s_clients_lock.unlock();

        // Send all messages to clients
        SteamNetworkingSockets()->SendMessages(CLIENTS, server_msgs.data(), send_results.data());
        if (std::ranges::any_of(send_results, [](std::int64_t result) { return result < 0; }))
        {
            std::cerr << "send failed\n";
            throw std::runtime_error("send failed");
        }
    }

    std::cout << std::format("All {} messages sent to all {} clients!\n", MESSAGES, CLIENTS);

    phase.wait(ALL_CLIENT_CONNECTED);

    // Cleanup
    SteamNetworkingSockets()->CloseListenSocket(listen_socket);
}

void do_clients()
{
    // Setup configuration used for clients
    SteamNetworkingConfigValue_t client_configs[1]{};
    client_configs[0].SetPtr(k_ESteamNetworkingConfig_Callback_ConnectionStatusChanged,
                             (void*)on_client_connection_status_changed);

    phase.wait(INIT);

    // Connect to server
    s_clients_lock.lock();
    s_clients.reserve(CLIENTS); // server side
    s_clients_lock.unlock();
    std::vector<HSteamNetConnection> c_clients(CLIENTS); // client side
    SteamNetworkingIPAddr connect_addr;
    if (!connect_addr.ParseString("::1"))
    {
        std::cerr << "Failed parsing localhost address?\n";
        throw std::logic_error("Failed parsing localhost address?");
    }
    connect_addr.m_port = SERVER_PORT;
    for (auto& client : c_clients)
        client = SteamNetworkingSockets()->ConnectByIPAddress(connect_addr, 1, client_configs);

    s_clients_lock.lock();
    while (s_clients.size() != CLIENTS)
    {
        s_clients_lock.unlock();
        std::this_thread::sleep_for(10ms);
        s_clients_lock.lock();
    }
    s_clients_lock.unlock();

    std::cout << std::format("All {} clients connected to the server!\n", CLIENTS);

    phase.store(ALL_CLIENT_CONNECTED);
    phase.notify_all();

    // Receive messages from server
    std::vector<std::vector<SteamNetworkingMessage_t*>> clients_msgs(CLIENTS,
                                                                     std::vector<SteamNetworkingMessage_t*>(MESSAGES));
    std::vector<int> clients_msgs_count(CLIENTS, 0);

    while (true)
    {
        for (int c = 0; c < CLIENTS; ++c)
        {
            auto& msg_count = clients_msgs_count[c];
            if (msg_count == MESSAGES)
                continue;

            auto& client_msgs = clients_msgs[c];
            const auto client = c_clients[c];

            const int received_cnt = SteamNetworkingSockets()->ReceiveMessagesOnConnection(
                client, client_msgs.data() + msg_count, MESSAGES - msg_count);
            if (received_cnt == -1)
            {
                std::cerr << "receive failed\n";
                throw std::runtime_error("receive failed");
            }
            msg_count += received_cnt;
        }

        if (MESSAGES == *std::min_element(clients_msgs_count.begin(), clients_msgs_count.end()))
            break;

        std::this_thread::sleep_for(10ms);
    }

    // Validate messages
    for (int c = 0; c < CLIENTS; ++c)
    {
        auto& client_msgs = clients_msgs[c];

        // Validate all messages for the client
        for (int msg_idx = 0; msg_idx < MESSAGES; ++msg_idx)
        {
            auto* msg = client_msgs[msg_idx];

            // Check if payload is not corrupted
            auto* payload = (std::uint8_t*)msg->m_pData;
            for (int i = 0; i < MSG_SIZE; ++i)
                if (payload[i] != msg_idx % 256)
                {
                    auto err_msg = std::format("Corrupted payload: {} (expected {})\n", payload[i], msg_idx % 256);
                    std::cerr << err_msg;
                    throw std::logic_error(err_msg);
                }

            // Release the message
            msg->Release();
        }
    }

    phase.store(CLOSING_CONNECTIONS);
    phase.notify_all();

    // Cleanup
    for (auto client : c_clients)
        SteamNetworkingSockets()->CloseConnection(client, 0, nullptr, false);
}

void do_callback()
{
    while (phase.load(std::memory_order_relaxed) != QUIT)
    {
        SteamNetworkingSockets()->RunCallbacks();
        std::this_thread::sleep_for(10ms);
    }
}

void on_server_connection_status_changed(SteamNetConnectionStatusChangedCallback_t* info)
{
    switch (info->m_info.m_eState)
    {
    case k_ESteamNetworkingConnectionState_Connecting: {
        EResult accept_result = SteamNetworkingSockets()->AcceptConnection(info->m_hConn);

        if (accept_result != k_EResultOK)
        {
            SteamNetworkingSockets()->CloseConnection(info->m_hConn, 0, "Accept failure", false);
            std::cerr << "accept failed\n";
            throw std::runtime_error("accept failed");
        }

        std::lock_guard<std::mutex> guard(s_clients_lock);
        s_clients.push_back(info->m_hConn);

        break;
    }

    case k_ESteamNetworkingConnectionState_ClosedByPeer:
    case k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
        SteamNetworkingSockets()->CloseConnection(info->m_hConn, 0, nullptr, false);
        break;

    default:
        break;
    }
}

void on_client_connection_status_changed(SteamNetConnectionStatusChangedCallback_t* info)
{
    switch (info->m_info.m_eState)
    {
    case k_ESteamNetworkingConnectionState_ClosedByPeer:
    case k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
        SteamNetworkingSockets()->CloseConnection(info->m_hConn, 0, nullptr, false);
        break;

    default:
        break;
    }
}

void debug_output(ESteamNetworkingSocketsDebugOutputType, const char* pszMsg)
{
    std::cout << std::format("{}\n", pszMsg);
}
