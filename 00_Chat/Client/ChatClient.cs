// SPDX-License-Identifier: 0BSD

using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using GNSPrac.Chat;
using Valve.Sockets;

/// <summary>
/// Chat client.
/// </summary>
internal class ChatClient
{
    private const ushort DefaultServerPort = 45700;

    private const int MaxMessagePerReceive = 100;

    private static async Task Main(string[] args)
    {
        Console.WriteLine("GNS-Practice #00: Chat");
        Console.WriteLine("Chat client in C# with Valve.Sockets.AutoGen");
        Console.WriteLine();

        // Parse IP address & port from `args`
        IPAddress? addr = IPAddress.IPv6Loopback;
        ushort port = DefaultServerPort;

        if (args.Length >= 1)
        {
            if (!IPAddress.TryParse(args[0], out addr))
            {
                Console.WriteLine($"Invalid IP address: {args[0]}");
                return;
            }

            if (args.Length >= 2)
            {
                if (!ushort.TryParse(args[1], out port))
                {
                    Console.WriteLine($"Invalid port: {args[1]}");
                    return;
                }
            }
        }

        Console.WriteLine($"Server IP: {addr}, Port: {port}");
        Console.WriteLine("Type /quit to quit.");
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

        // Initialize `Valve.Sockets.AutoGen`
        GameNetworkingSockets gns;
        try
        {
            gns = new GameNetworkingSockets();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return;
        }

        // Setup the address
        SteamNetworkingIPAddr address = default;
        address.SetIPv6(addr.MapToIPv6().GetAddressBytes(), port);

        // Prepare socket
        SteamNetworkingSockets client = new();

        // CancellationToken to stop the receive loop
        CancellationTokenSource cancelTokenSrc = new();
        CancellationToken cancelToken = cancelTokenSrc.Token;

        FnSteamNetConnectionStatusChanged onConnStatsChanged;

        unsafe
        {
            onConnStatsChanged = (ref SteamNetConnectionStatusChangedCallback info) =>
            {
                switch (info.m_info.m_eState)
                {
                    case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None:
                        // This is when you destroy the connection.
                        // Nothing to do here.
                        break;

                    case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                        Console.WriteLine("Successfully connected to server!\nTo change your name, type /name <your new name>.");
                        break;

                    case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                    case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                        // Print the reason of connection close
                        SteamNetConnectionInfo connInfo = info.m_info;

                        string? desc, dbg;
                        string state = connInfo.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer ? "closed by peer" : "problem detected locally";
                        unsafe
                        {
                            desc = Marshal.PtrToStringAnsi((nint)connInfo.m_szConnectionDescription);
                            dbg = Marshal.PtrToStringAnsi((nint)connInfo.m_szEndDebug);
                        }

                        Console.WriteLine($"{desc ?? "(Invalid desc)"} ({state}), reason {connInfo.m_eEndReason}: {dbg ?? "(Invalid dbg)"}");
                        cancelTokenSrc.Cancel();
                        break;
                }
            };
        }

        Span<SteamNetworkingConfigValue> clientConfigs = stackalloc SteamNetworkingConfigValue[1];
        unsafe
        {
            clientConfigs[0].SetPtr(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_Callback_ConnectionStatusChanged, (void*)Marshal.GetFunctionPointerForDelegate(onConnStatsChanged));
        }

        // Connect to server
        uint connection = client.ConnectByIPAddress(address, clientConfigs.Length, clientConfigs);

        void OnMessage(in SteamNetworkingMessage netMsg)
        {
            // Ignore the empty message.
            // In this case, `netMsg.data` is nullptr
            if (netMsg.m_cbSize == 0)
            {
                Console.WriteLine("Server sent an empty message");
                return;
            }

            // Unmarshall the unmanaged string
            ChatProtocol msg;
            unsafe
            {
                ReadOnlySpan<byte> msgRaw = new((void*)netMsg.m_pData, netMsg.m_cbSize);
                msg = ProtoBuf.Serializer.Deserialize<ChatProtocol>(msgRaw);
            }

            // Handle the message based on its type
            switch (msg.Type)
            {
                case ChatProtocol.MsgType.MsgTypeChat:
                    // Print the chat message
                    Console.WriteLine($"{msg.Chat.SenderName ?? "???"}: {msg.Chat.Content ?? string.Empty}");
                    break;

                default:
                    // Server shouldn't send other type of messages
                    Console.WriteLine($"Server sent an invalid message type: {msg.Type}");
                    break;
            }
        }

        // Receive loop
        Task receiveTask = Task.Run(() =>
        {
            Span<IntPtr> nativeMsgs = stackalloc IntPtr[MaxMessagePerReceive];

            while (!cancelToken.IsCancellationRequested)
            {
                client.RunCallbacks();
                int receivedMsgCount = client.ReceiveMessagesOnConnection(connection, nativeMsgs, MaxMessagePerReceive);
                if (receivedMsgCount == -1)
                {
                    throw new Exception($"receive msg failed");
                }
                else
                {
                    for (int i = 0; i < receivedMsgCount; ++i)
                    {
                        Span<SteamNetworkingMessage> message;
                        unsafe
                        {
                            message = new Span<SteamNetworkingMessage>((void*)nativeMsgs[i], 1);
                        }

                        OnMessage(in message[0]);

                        SteamNetworkingMessage.Release(nativeMsgs[i]);
                    }
                }

                Thread.Sleep(1);
            }
        });

        // User input loop
        while (!cancelToken.IsCancellationRequested)
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

            ChatProtocol? msg;

            // If the user requested a new name
            string[] split = message.Split();
            if (split.Length > 0 && split[0] == "/name")
            {
                if (split.Length < 2)
                {
                    Console.WriteLine("You should provide a new name after /name");
                    continue;
                }
                else
                {
                    // Prepare new name
                    string newName = string.Join(' ', split[1..]);
                    msg = new()
                    {
                        Type = ChatProtocol.MsgType.MsgTypeNameChange,
                        NameChange = new() { Name = newName },
                    };
                }
            }

            // If the user typed a chat message
            else
            {
                // Prepare chat message
                msg = new()
                {
                    Type = ChatProtocol.MsgType.MsgTypeChat,
                    Chat = new() { Content = message },
                };
            }

            // Serialize the `msg` to a memory stream.
            // In real use case, you would get this from a pool.
            MemoryStream msgMS = new();
            ProtoBuf.Serializer.Serialize(msgMS, msg);

            // Send the message
            client.SendMessageToConnection(connection, msgMS.GetBuffer(), Convert.ToUInt32(msgMS.Position), Native.k_nSteamNetworkingSend_ReliableNoNagle, out Unsafe.NullRef<long>());
        }

        Console.WriteLine("Quiting...");

        // Stop the receive loop, and wait for it
        cancelTokenSrc.Cancel();
        await receiveTask;

        client.CloseConnection(connection, 0, "Client quit", true);

        // Wait for the linger for a short period of time
        Thread.Sleep(500);

        Console.WriteLine("Quited!");
    }
}
