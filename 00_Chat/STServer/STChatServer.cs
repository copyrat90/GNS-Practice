// SPDX-License-Identifier: 0BSD

namespace GNSPrac.Chat;

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using ProtoBuf;
using Valve.Sockets;

/// <summary>
/// Single-threaded chat server.
/// </summary>
internal class STChatServer
{
    private const ushort DefaultServerPort = 45700;

    private const int MaxMessagePerReceive = 100;

    private static async Task Main(string[] args)
    {
        Console.WriteLine("GNS-Practice #00: Chat");
        Console.WriteLine("Single-threaded chat server in C# with ValveSocket-CSharp");
        Console.WriteLine();

        // Parse port from `args`
        ushort port = DefaultServerPort;

        if (args.Length >= 1)
        {
            if (!ushort.TryParse(args[0], out port))
            {
                Console.WriteLine($"Invalid port: {args[0]}");
                return;
            }
        }

        Console.WriteLine($"Server port: {port}");
        Console.WriteLine();

        try
        {
            // Load native `GameNetworkingSockets.dll` and its dependencies
            NativeLibrary.Load(Path.Join(AppContext.BaseDirectory, "GameNetworkingSockets.dll"));
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to load `GameNetworkingSockets.dll` or its dependencies.");
            Console.WriteLine(ex.Message);
            return;
        }

        // Initialize `ValveSockets-CSharp`
        if (!Valve.Sockets.Library.Initialize())
        {
            Console.WriteLine("Failed to initialize ValveSocket-CSharp");
            return;
        }

        // Prepare listen socket & poll group
        NetworkingSockets server = new();
        uint pollGroup = server.CreatePollGroup();

        // Manage connected clients' info with a dictionary.
        // Note that a client might not logged in yet.
        Dictionary<uint, ClientInfo> clients = [];

        // The connection status changed callback method
        void OnConnectionStatusChanged(ref ConnectionStatusChangedInfo info)
        {
            switch (info.connectionInfo.state)
            {
                case ConnectionState.None:
                    // This is when you destroy the connection.
                    // Nothing to do here.
                    break;

                case ConnectionState.Connecting:
                    // Accept the connection.
                    // You could also close the connection right away.
                    Result acceptResult = server.AcceptConnection(info.connection);

                    // If accept failed, clean up the connection.
                    if (acceptResult != Result.OK)
                    {
                        server.CloseConnection(info.connection);
                        Console.WriteLine($"Accept failed with {acceptResult}");
                        break;
                    }

                    // Add new client to `clients` dictionary
                    // It doesn't have a name yet, which means it's not properly logged in.
                    //
                    // Note that we do this BEFORE assign it to the poll group.
                    // If we do the opposite, and if the message callback runs on a seperate thread,
                    // it might not find this client from `clients` dictionary, because it's not added at that point.
                    //
                    // But actually, it's a single-threaded code now, so it doesn't matter for now.
                    ClientInfo newClientInfo = new();
                    clients.Add(info.connection, newClientInfo);

                    // Assign new client to the poll group
                    if (!server.SetConnectionPollGroup(pollGroup, info.connection))
                    {
                        clients.Remove(info.connection);
                        server.CloseConnection(info.connection);

                        Console.WriteLine($"Failed to assign poll group");
                    }

                    Console.WriteLine($"New client #{info.connection} connected!");

                    break;

                case ConnectionState.ClosedByPeer:
                case ConnectionState.ProblemDetectedLocally:
                    // Connection changed callbacks are dispatched in FIFO order.

                    // Get the client from `clients`
                    ClientInfo client = clients[info.connection];

                    // Print the reason of connection close
                    ConnectionInfo connInfo = info.connectionInfo;
                    string clientName = client.Name ?? "(not logged-in client)";
                    string state = connInfo.state == ConnectionState.ClosedByPeer ? "closed by peer" : "problem detected locally";
                    Console.WriteLine($"{clientName} ({connInfo.address}) {connInfo.connectionDescription} ({state}), reason {connInfo.endReason}: {connInfo.endDebug}");

                    // Remove it from the clients dictionary
                    clients.Remove(info.connection);

                    // Don't forget to clean up the connection!
                    server.CloseConnection(info.connection);

                    break;

                case ConnectionState.Connected:
                    // Callback after accepting the connection.
                    // Nothing to do here, as we're the server.
                    break;
            }
        }

        // Setup configuration used for listen socket
        var listenSocketConfigs = new Valve.Sockets.Configuration[1];
        listenSocketConfigs[0].SetConnectionStatusChangedCallback(OnConnectionStatusChanged);

        // Start listening
        Address addr = default;
        addr.SetAddress("::", port);
        uint listenSocket = server.CreateListenSocket(ref addr, listenSocketConfigs);

        // The message callback method
        void OnMessage(in NetworkingMessage netMsg)
        {
            // Ignore the empty message.
            // In this case, `netMsg.data` is nullptr
            if (netMsg.length == 0)
            {
                Console.WriteLine("Client sent an empty message");
                return;
            }

            // Unmarshall the unmanaged message
            ChatProtocol msg;
            unsafe
            {
                ReadOnlySpan<byte> msgRaw = new((void*)netMsg.data, netMsg.length);
                msg = ProtoBuf.Serializer.Deserialize<ChatProtocol>(msgRaw);
            }

            // Get the client from `clients` dictionary.
            // It must exist in the dictionary, because we added it on `ConnectionState.Connecting`
            ClientInfo client = clients[netMsg.connection];

            // Handle the message based on its type
            switch (msg.Type)
            {
                case ChatProtocol.MsgType.MsgTypeChat:
                    {
                        // We could reuse the same `msg`, but we'll just create another one to demonstrate.
                        ChatProtocol response = new()
                        {
                            Type = ChatProtocol.MsgType.MsgTypeChat,
                            Chat = new()
                            {
                                SenderName = client.Name ?? $"Guest#{netMsg.connection}",
                                Content = msg.Chat.Content,
                            },
                        };

                        // Serialize the response to a memory stream.
                        // In real use case, you would get this from a pool.
                        MemoryStream responseMS = new();
                        ProtoBuf.Serializer.Serialize(responseMS, response);

                        // Propagate the response to other clients.
                        foreach (var otherClientConn in clients.Keys)
                        {
                            // Ignore itself
                            if (otherClientConn != netMsg.connection)
                            {
                                server.SendMessageToConnection(otherClientConn, responseMS.GetBuffer(), Convert.ToInt32(responseMS.Position), SendFlags.Reliable | SendFlags.NoNagle);
                            }
                        }

                        // Print the chat message on the server side, too.
                        Console.WriteLine($"{response.Chat.SenderName}: {response.Chat.Content}");
                        break;
                    }

                case ChatProtocol.MsgType.MsgTypeNameChange:
                    {
                        // Set the new name if not null
                        if (msg.NameChange.Name != null)
                        {
                            client.Name = msg.NameChange.Name;
                            Console.WriteLine($"Client #{netMsg.connection} changed their name to {client.Name}");
                        }

                        // Prepare the response to the client about their current name
                        ChatProtocol response = new()
                        {
                            Type = ChatProtocol.MsgType.MsgTypeChat,
                            Chat = new()
                            {
                                SenderName = "Server",
                                Content = $"Your name is now {client.Name ?? $"Guest#{netMsg.connection}"}",
                            },
                        };

                        // Serialize the response to a memory stream.
                        // In real use case, you would get this from a pool.
                        MemoryStream responseMS = new();
                        ProtoBuf.Serializer.Serialize(responseMS, response);

                        // Notify to the client about their current name
                        server.SendMessageToConnection(netMsg.connection, responseMS.GetBuffer(), Convert.ToInt32(responseMS.Position), SendFlags.Reliable | SendFlags.NoNagle);
                        break;
                    }

                default:
                    // Client shouldn't send other type of messages
                    Console.WriteLine($"Client sent an invalid message type: {msg.Type}");
                    break;
            }
        }

        // CancellationToken to stop the receive loop
        CancellationTokenSource cancelTokenSrc = new();
        CancellationToken cancelToken = cancelTokenSrc.Token;

        // Receive loop
        Task receiveTask = Task.Run(() =>
        {
            while (!cancelToken.IsCancellationRequested)
            {
                server.RunCallbacks();
                server.ReceiveMessagesOnPollGroup(pollGroup, OnMessage, MaxMessagePerReceive);
                Thread.Sleep(1);
            }
        });

        Console.WriteLine("Server started, type /quit to quit");

        // User input loop
        while (true)
        {
            string? message = Console.ReadLine();

            if (message == null || message.Length <= 0)
            {
                continue;
            }

            if (message == "/quit")
            {
                break;
            }
        }

        Console.WriteLine("Stop receiving...");

        // Stop the receive loop
        cancelTokenSrc.Cancel();

        Console.WriteLine("Closing connections...");

        // Prepare the final message from the server
        ChatProtocol finalMsg = new()
        {
            Type = ChatProtocol.MsgType.MsgTypeChat,
            Chat = new()
            {
                SenderName = "Server",
                Content = "Server is shutting down. Goodbye.",
            },
        };
        MemoryStream finalMsgMS = new();
        ProtoBuf.Serializer.Serialize(finalMsgMS, finalMsg);

        // Close all the connections
        foreach (uint clientConn in clients.Keys)
        {
            // Send the final message
            server.SendMessageToConnection(clientConn, finalMsgMS.GetBuffer(), Convert.ToInt32(finalMsgMS.Position), SendFlags.Reliable | SendFlags.NoNagle);

            // Close the connection with linger enabled
            server.CloseConnection(clientConn, 0, "Server shutdown", true);
        }

        clients.Clear();

        // Wait for the receive thread to exit
        await receiveTask;

        // Clean up the poll group
        server.DestroyPollGroup(pollGroup);

        // Wait for the linger for a short period of time
        Thread.Sleep(500);

        // This should be AFTER wait, because closing listen socket destroys all connections accepted from it
        server.CloseListenSocket(listenSocket);

        // Destroy `ValveSockets-CSharp`
        Valve.Sockets.Library.Deinitialize();

        Console.WriteLine("Server closed!");
    }

    private class ClientInfo
    {
        public string? Name { get; set; }
    }
}
