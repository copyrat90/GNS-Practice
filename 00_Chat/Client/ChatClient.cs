// SPDX-License-Identifier: 0BSD

using System.Diagnostics;
using System.Net;
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
        Console.WriteLine("Chat client in C# with ValveSockets-CSharp");
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

        // Initialize `ValveSockets-CSharp`
        if (!Valve.Sockets.Library.Initialize())
        {
            Console.WriteLine("Failed to initialize ValveSocket-CSharp");
            return;
        }

        // Setup the address
        Address address = default;
        address.SetAddress(addr.ToString(), port);

        // Prepare socket
        NetworkingSockets client = new();

        // CancellationToken to stop the receive loop
        CancellationTokenSource cancelTokenSrc = new();
        CancellationToken cancelToken = cancelTokenSrc.Token;

        void OnConnectionStatusChanged(ref ConnectionStatusChangedInfo info)
        {
            switch (info.connectionInfo.state)
            {
                case ConnectionState.None:
                    // This is when you destroy the connection.
                    // Nothing to do here.
                    break;

                case ConnectionState.Connected:
                    Console.WriteLine("Successfully connected to server!\nTo change your name, type /name <your new name>.");
                    break;

                case ConnectionState.ClosedByPeer:
                case ConnectionState.ProblemDetectedLocally:
                    // Print the reason of connection close
                    ConnectionInfo connInfo = info.connectionInfo;
                    string state = connInfo.state == ConnectionState.ClosedByPeer ? "closed by peer" : "problem detected locally";
                    Console.WriteLine($"{connInfo.connectionDescription} ({state}), reason {connInfo.endReason}: {connInfo.endDebug}");
                    cancelTokenSrc.Cancel();
                    break;
            }
        }

        var clientConfigs = new Valve.Sockets.Configuration[1];
        clientConfigs[0].SetConnectionStatusChangedCallback(OnConnectionStatusChanged);

        // Connect to server
        uint connection = client.Connect(ref address, clientConfigs);

        void OnMessage(in NetworkingMessage netMsg)
        {
            // Ignore the empty message.
            // In this case, `netMsg.data` is nullptr
            if (netMsg.length == 0)
            {
                Console.WriteLine("Server sent an empty message");
                return;
            }

            // Unmarshall the unmanaged string
            ChatProtocol msg;
            unsafe
            {
                ReadOnlySpan<byte> msgRaw = new((void*)netMsg.data, netMsg.length);
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
            while (!cancelToken.IsCancellationRequested)
            {
                client.RunCallbacks();
                client.ReceiveMessagesOnConnection(connection, OnMessage, MaxMessagePerReceive);
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
            client.SendMessageToConnection(connection, msgMS.GetBuffer(), Convert.ToInt32(msgMS.Position), SendFlags.Reliable | SendFlags.NoNagle);
        }

        Console.WriteLine("Quiting...");

        // Stop the receive loop, and wait for it
        cancelTokenSrc.Cancel();
        await receiveTask;

        client.CloseConnection(connection, 0, "Client quit", true);

        // Wait for the linger for a short period of time
        Thread.Sleep(500);

        // Destroy `ValveSockets-CSharp`
        Valve.Sockets.Library.Deinitialize();

        Console.WriteLine("Quited!");
    }
}
