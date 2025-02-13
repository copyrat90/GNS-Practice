// SPDX-License-Identifier: 0BSD

using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
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
        Console.WriteLine("Type 'quit' to quit.");
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
                    Console.WriteLine("Successfully connected to server!\nType your first message, which will be your name.");
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
            // Unmarshall the unmanaged string
            string message = Marshal.PtrToStringUTF8(netMsg.data, netMsg.length)!;

            // Print the chat message
            Console.WriteLine(message);
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

            if (message == "quit")
            {
                break;
            }

            byte[] messageRaw = Encoding.UTF8.GetBytes(message);
            client.SendMessageToConnection(connection, messageRaw, SendFlags.Reliable | SendFlags.NoNagle);
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
