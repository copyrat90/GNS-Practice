// SPDX-License-Identifier: 0BSD

#include "../Proto/ChatProtocol.pb.h"

#include <steam/isteamnetworkingutils.h>
#include <steam/steamnetworkingsockets.h>
#include <steam/steamnetworkingtypes.h>

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <format>
#include <iostream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <vector>

class st_chat_server
{
public:
    static constexpr std::uint16_t DEFAULT_SERVER_PORT = 45700;
    static constexpr int MAX_MESSAGES_PER_RECEIVE = 100;

private:
    struct client_info
    {
        std::string name;
    };

private:
    static std::atomic<st_chat_server*> _instance;

    bool _disposed = true;

    bool _gns_initialized = false;

    HSteamNetPollGroup _poll_group = k_HSteamNetPollGroup_Invalid;
    HSteamListenSocket _listen_socket = k_HSteamListenSocket_Invalid;

    std::unordered_map<std::uint32_t, client_info> _clients;

    std::atomic<bool> _quit_requested;
    std::thread _server_thread;

public:
    /// @brief Constructor to prevent multiple instance of `st_chat_server`.
    /// This is due to GNS's callbacks using function pointers.
    /// I need to find a way to remove this limitation in the future.
    st_chat_server()
    {
        st_chat_server* expected = nullptr;
        if (!_instance.compare_exchange_strong(expected, this, std::memory_order_relaxed))
            throw std::logic_error("There are multiple `st_chat_server` instances");
    }

    ~st_chat_server()
    {
        _instance.store(nullptr, std::memory_order_relaxed);
    }

public:
    /// @brief Start the server with specified port.
    /// @param port Port to listen.
    /// @return Whether the server has been started to run, or errored.
    bool start(std::uint16_t port)
    {
        _disposed = false;

        try
        {
            // Initialize `GameNetworkingSockets`
            SteamDatagramErrMsg err_msg;
            _gns_initialized = GameNetworkingSockets_Init(nullptr, err_msg);
            if (!_gns_initialized)
                throw std::runtime_error(err_msg);

            // Prepare poll group
            _poll_group = SteamNetworkingSockets()->CreatePollGroup();

            // Manage connected clients' info with `std::unordered_map`.
            // Note that a client might not logged in yet.
            _clients.clear();

            // Setup configuration used for listen socket
            SteamNetworkingConfigValue_t configs[1]{};
            configs[0].SetPtr(k_ESteamNetworkingConfig_Callback_ConnectionStatusChanged,
                              (void*)on_connection_status_changed);

            // Start listening
            SteamNetworkingIPAddr addr{};
            addr.m_port = port;
            _listen_socket = SteamNetworkingSockets()->CreateListenSocketIP(addr, 1, configs);
            if (_listen_socket == k_HSteamListenSocket_Invalid)
            {
                throw std::runtime_error("Failed to create a listen socket");
            }

            // Create the server loop as a seperate thread
            _quit_requested.store(false, std::memory_order_relaxed);
            _server_thread = std::thread(&st_chat_server::server_loop, this);
        }
        catch (const std::exception& ex)
        {
            std::cout << "Failed to start st_chat_server: " << ex.what() << '\n';

            dispose();

            return false;
        }

        return true;
    }

    /// @brief Stop the server.
    /// @param linger_milliseconds Milliseconds to wait before dropping connections.
    /// This can be useful if you want to send a goodbye message or similar.
    void stop(int linger_milliseconds = 0)
    {
        if (_disposed)
            return;

        std::cout << "Stopping the server loop..." << std::endl;

        // Stop the server loop
        _quit_requested.store(true, std::memory_order_relaxed);

        std::cout << "Closing connections..." << std::endl;

        // Close all the connections with linger enabled
        for (const auto& client : _clients)
        {
            SteamNetworkingSockets()->CloseConnection(client.first, 0, "Server shutdown", true);
        }

        // Wait for the server loop task to stop
        _server_thread.join();

        // Wait for the linger for a short period of time
        if (linger_milliseconds > 0)
            std::this_thread::sleep_for(std::chrono::milliseconds(linger_milliseconds));

        // This should be AFTER lingering, because closing listen socket drops all connections accepted from it
        SteamNetworkingSockets()->CloseListenSocket(_listen_socket);

        dispose();
    }

    /// @brief Disposes the server synchronously.
    /// If it was not stopped, it will block to stop.
    void dispose()
    {
        if (!_disposed)
        {
            _quit_requested.store(true, std::memory_order_relaxed);
            if (_server_thread.joinable())
                _server_thread.join();

            if (_listen_socket != k_HSteamListenSocket_Invalid)
            {
                SteamNetworkingSockets()->CloseListenSocket(_listen_socket);
                _listen_socket = k_HSteamListenSocket_Invalid;
            }

            _clients.clear();

            if (_poll_group != k_HSteamNetPollGroup_Invalid)
            {
                SteamNetworkingSockets()->DestroyPollGroup(_poll_group);
                _poll_group = k_HSteamNetPollGroup_Invalid;
            }

            _disposed = true;
        }
    }

private:
    /// @brief Receive data and run callbacks here.
    void server_loop()
    {
        while (!_quit_requested.load(std::memory_order_relaxed))
        {
            SteamNetworkingSockets()->RunCallbacks();

            SteamNetworkingMessage_t* msgs[MAX_MESSAGES_PER_RECEIVE];
            int received_msg_count =
                SteamNetworkingSockets()->ReceiveMessagesOnPollGroup(_poll_group, msgs, MAX_MESSAGES_PER_RECEIVE);
            if (received_msg_count == -1)
            {
                throw std::runtime_error("receive msg failed");
            }
            else
            {
                for (int i = 0; i < received_msg_count; ++i)
                {
                    on_message(*msgs[i]);

                    msgs[i]->Release();
                }
            }

            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
    }

    /// @brief Callback that's called from the GNS when connection status changed for any client.
    /// This function is static, due to GNS's callbacks using function pointers;
    /// I need to find a way to remove this limitation in the future.
    /// @param info Connection status changed info.
    static void on_connection_status_changed(SteamNetConnectionStatusChangedCallback_t* info)
    {
        auto& server = *_instance.load(std::memory_order_relaxed);

        switch (info->m_info.m_eState)
        {
        case k_ESteamNetworkingConnectionState_None:
            // This is when you destroy the connection.
            // Nothing to do here.
            break;

        case k_ESteamNetworkingConnectionState_Connecting: {

            // Accept the connection.
            // You could also close the connection right away.
            EResult accept_result = SteamNetworkingSockets()->AcceptConnection(info->m_hConn);

            // If accept failed, clean up the connection.
            if (accept_result != k_EResultOK)
            {
                SteamNetworkingSockets()->CloseConnection(info->m_hConn, 0, "Accept failure", false);
                std::cout << "Accept failed with " << accept_result << std::endl;
                break;
            }

            // Add new client to `clients` map
            // It doesn't have a name yet, which means it's not properly logged in.
            //
            // Note that we do this BEFORE assign it to the poll group.
            // If we do the opposite, and if the message callback runs on a seperate thread,
            // it might not find this client from `clients` map, because it's not added at that point.
            //
            // But actually, it's a single-threaded code now, so it doesn't matter for now.
            server._clients.try_emplace(info->m_hConn, client_info{});

            // Assign new client to the poll group
            if (!SteamNetworkingSockets()->SetConnectionPollGroup(info->m_hConn, server._poll_group))
            {
                server._clients.erase(info->m_hConn);
                SteamNetworkingSockets()->CloseConnection(info->m_hConn, 0, "Poll group assign failure", false);

                std::cout << "Failed to assign poll group" << std::endl;
                break;
            }

            std::cout << std::format("New client #{} connected!", info->m_hConn) << std::endl;

            break;
        }

        case k_ESteamNetworkingConnectionState_ClosedByPeer:
        case k_ESteamNetworkingConnectionState_ProblemDetectedLocally: {
            // Connection changed callbacks are dispatched in FIFO order.

            // Get the client from `clients`
            client_info& client = server._clients[info->m_hConn];

            // Print the reason of connection close
            SteamNetConnectionInfo_t& conn_info = info->m_info;
            std::string_view client_name = "(not logged-in client)";
            if (!client.name.empty())
                client_name = client.name;
            std::string_view state = conn_info.m_eState == k_ESteamNetworkingConnectionState_ClosedByPeer
                                         ? "closed by peer"
                                         : "problem detected locally";
            std::string_view desc =
                conn_info.m_szConnectionDescription ? conn_info.m_szConnectionDescription : "(Invalid desc)";
            std::string_view dbg = conn_info.m_szEndDebug ? conn_info.m_szEndDebug : "(Invalid dbg)";
            char addr_str[SteamNetworkingIPAddr::k_cchMaxString];
            conn_info.m_addrRemote.ToString(addr_str, sizeof addr_str, true);

            std::cout << std::format("{} ({}) {} ({}), reason {}: {}", client_name, addr_str, desc, state,
                                     conn_info.m_eEndReason, dbg)
                      << std::endl;

            // Remove it from the clients map
            server._clients.erase(info->m_hConn);

            // Don't forget to clean up the connection!
            SteamNetworkingSockets()->CloseConnection(info->m_hConn, 0, nullptr, false);

            break;
        }

        case k_ESteamNetworkingConnectionState_Connected:
            // Callback after accepting the connection.
            // Nothing to do here, as we're the server.
            break;
        }
    }

    /// @brief Callback that's called when a message arrived from any client.
    void on_message(const SteamNetworkingMessage_t& net_msg)
    {
        // Ignore the empty message.
        // In this case, `netMsg.data` is nullptr
        if (net_msg.m_cbSize == 0)
        {
            std::cout << "Client sent an empty message" << std::endl;
            return;
        }

        // Unmarshall the protobuf message
        GNSPrac::Chat::ChatProtocol msg;
        if (!msg.ParseFromArray(net_msg.m_pData, net_msg.m_cbSize))
        {
            std::cout << "Client sent an invalid message" << std::endl;
            return;
        }

        // Get the client from `clients` map.
        // It must exist in the map, because we added it on `ConnectionState::Connecting`
        client_info& client = _clients[net_msg.m_conn];

        // Handle the message based on its type
        switch (msg.msg_case())
        {
            using msg_case = GNSPrac::Chat::ChatProtocol::MsgCase;

        case msg_case::kChat: {
            // We could reuse the same `msg`, but we'll just create another one to demonstrate.
            // I'm omitting checks for simplicity, but you should always validate a client message.
            GNSPrac::Chat::ChatProtocol response;
            auto& chat = *response.mutable_chat();
            *chat.mutable_sender_name() = client.name.empty() ? std::format("Guest#{}", net_msg.m_conn) : client.name;
            *chat.mutable_content() = msg.chat().content();

            // Serialize the response to a byte vector.
            // In real use case, you would get this from a pool.
            const std::uint32_t response_size = (std::uint32_t)response.ByteSizeLong();
            std::vector<std::byte> response_vec(response_size);
            response.SerializeToArray(response_vec.data(), response_size);

            // Propagate the response to other clients.
            for (const auto& other_client : _clients)
            {
                const auto other_conn = other_client.first;

                // Ignore itself
                if (other_conn != net_msg.m_conn)
                {
                    SteamNetworkingSockets()->SendMessageToConnection(other_conn, response_vec.data(), response_size,
                                                                      k_nSteamNetworkingSend_ReliableNoNagle, nullptr);
                }
            }

            // Print the chat message on the server side, too.
            std::cout << std::format("{}: {}", response.chat().sender_name(), response.chat().content()) << std::endl;
            break;
        }

        case msg_case::kNameChange: {
            // Set the new name if not null
            if (msg.has_name_change() && !msg.name_change().name().empty())
            {
                client.name = msg.name_change().name();
                std::cout << std::format("Client #{} changed their name to {}", net_msg.m_conn, client.name)
                          << std::endl;
            }

            // Prepare the response to the client about their current name
            GNSPrac::Chat::ChatProtocol response;
            auto& chat = *response.mutable_chat();
            *chat.mutable_sender_name() = "Server";
            *chat.mutable_content() = std::format(
                "Your name is now {}", client.name.empty() ? std::format("Guest#{}", net_msg.m_conn) : client.name);

            // Serialize the response to a byte vector.
            // In real use case, you would get this from a pool.
            const std::uint32_t response_size = (std::uint32_t)response.ByteSizeLong();
            std::vector<std::byte> response_vec(response_size);
            response.SerializeToArray(response_vec.data(), response_size);

            // Notify to the client about their current name
            SteamNetworkingSockets()->SendMessageToConnection(net_msg.m_conn, response_vec.data(), response_size,
                                                              k_nSteamNetworkingSend_ReliableNoNagle, nullptr);
            break;
        }

        default:
            // Client shouldn't send other type of messages
            std::cout << std::format("Client sent an invalid message type: {}", (int)msg.msg_case()) << std::endl;
            break;
        }
    }
};

std::atomic<st_chat_server*> st_chat_server::_instance = nullptr;

int main(int argc, char** args)
{
    std::cout << "GNS-Practice #00: Chat" << std::endl;
    std::cout << "Single-threaded chat server in C++ with GameNetworkingSockets\n" << std::endl;

    // Parse port from `args`
    std::uint16_t port = st_chat_server::DEFAULT_SERVER_PORT;

    if (argc >= 2)
    {
        char* end;
        long parsed_port = std::strtol(args[1], &end, 0);
        if (parsed_port < 0 || parsed_port >= 65536)
        {
            std::cout << "Invalid port: " << args[1] << std::endl;
            return 0;
        }
        else
        {
            port = (std::uint16_t)parsed_port;
        }
    }

    std::cout << "Server port: " << port << '\n' << std::endl;

    // Start the server with specified port
    st_chat_server server;
    if (!server.start(port))
    {
        std::cout << "Too bad..." << std::endl;
        return 0;
    }

    std::cout << "Server started, type /quit to quit" << std::endl;

    while (true)
    {
        std::string message;
        std::getline(std::cin, message);

        if (message.empty())
            continue;

        if (message == "/quit")
            break;
    }

    // Let's quit the server now!

    // Stop the server
    server.stop(500);

    std::cout << "Server closed!" << std::endl;
}
