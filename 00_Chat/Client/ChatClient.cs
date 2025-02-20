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
internal class ChatClient : IAsyncDisposable, IDisposable
{
    private const ushort DefaultServerPort = 45700;

    private const int MaxMessagePerReceive = 100;

    private bool disposed;

    private IntPtr gnsLib;

    private GameNetworkingSockets? gns;
    private SteamNetworkingSockets? netSockets;
    private uint connection;

    private Task? clientTask;
    private CancellationTokenSource? cancelTokenSrc;
    private CancellationToken cancelToken;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChatClient"/> class.
    /// This suppresses finalizing, because the client won't start in construction.
    /// </summary>
    public ChatClient()
    {
        this.disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizes an instance of the <see cref="ChatClient"/> class.
    /// </summary>
    ~ChatClient() => this.Dispose(false);

    /// <summary>
    /// Message received delegate type.
    /// </summary>
    /// <param name="message">Message arrived via GameNetworkingSockets.</param>
    public delegate void MessageReceivedEventHandler(in SteamNetworkingMessage message);

    /// <summary>
    /// Event that fires when a message arrives via GameNetworkingSockets.
    /// </summary>
    private event MessageReceivedEventHandler? MessageReceived;

    /// <summary>
    /// Event that fires when a connection status changed in GameNetworkingSockets.
    /// </summary>
    private event FnSteamNetConnectionStatusChanged? ConnectionStatusChanged;

    /// <summary>
    /// Connect to the server with specified host address & port.
    /// </summary>
    /// <param name="hostNameOrAddress">Host name or address to connect to.</param>
    /// <param name="hostPort">Host port to connect to.</param>
    /// <returns>Task that represents connection request. Note that returning `true` means request succeed, not connected.</returns>
    public async Task<bool> Connect(string hostNameOrAddress, ushort hostPort)
    {
        // Prevent connecting twice
        if (!this.disposed)
        {
            return false;
        }

        this.disposed = false;
        GC.ReRegisterForFinalize(this);

        try
        {
            string? libName;

            // Load native GameNetworkingSockets library and its dependencies
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                libName = "GameNetworkingSockets.dll";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                libName = "libGameNetworkingSockets.so";
            }
            else
            {
                throw new Exception("Unknown OS");
            }

            this.gnsLib = await Task.Run(() => NativeLibrary.Load(Path.Join(AppContext.BaseDirectory, libName)));

            // Initialize `Valve.Sockets.AutoGen`
            this.gns = new GameNetworkingSockets();

            // Setup the address
            IPAddress[] ipAddrs = await Dns.GetHostAddressesAsync(hostNameOrAddress);

            if (ipAddrs.Length == 0)
            {
                throw new Exception("Host not found");
            }

            // Setup the address
            SteamNetworkingIPAddr address = default;
            address.SetIPv6(ipAddrs[0].MapToIPv6().GetAddressBytes(), hostPort);

            // Prepare net sockets
            this.netSockets = new SteamNetworkingSockets();

            // Setup the connection status changed callback delegate instance.
            // This delegate instance should live until the connection is closed, because it's called from native dll.
            this.ConnectionStatusChanged = new(this.OnConnectionStatusChanged);

            // Setup configuration used for connection
            Span<SteamNetworkingConfigValue> configs = stackalloc SteamNetworkingConfigValue[1];
            unsafe
            {
                configs[0].SetPtr(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_Callback_ConnectionStatusChanged, (void*)Marshal.GetFunctionPointerForDelegate(this.ConnectionStatusChanged));
            }

            // Start connecting
            this.connection = this.netSockets.ConnectByIPAddress(address, configs.Length, configs);

            // Setup the on message callback delegate instance.
            // This delegate instance should live until the connection is closed, because it's called from native dll.
            this.MessageReceived = new(this.OnMessage);

            // Create the client loop as a task
            this.cancelTokenSrc = new();
            this.cancelToken = this.cancelTokenSrc.Token;
            this.clientTask = Task.Run(this.ClientLoop);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to connect to server: " + ex.Message);
            Console.WriteLine(ex.StackTrace);

            this.Dispose();

            return false;
        }

        return true;
    }

    /// <summary>
    /// Stop the client.
    /// </summary>
    /// <param name="lingerMilliseconds">Milliseconds to wait before dropping connections. This can be useful if you want to send a goodbye message or similar.</param>
    /// <returns>ValueTask stopping the client.</returns>
    public async ValueTask Stop(int lingerMilliseconds = 0)
    {
        if (this.disposed)
        {
            return;
        }

        Console.WriteLine($"Stopping the client loop...");

        // Stop the client loop
        await this.cancelTokenSrc!.CancelAsync();

        // Close the connection with linger enabled
        this.netSockets!.CloseConnection(this.connection, 0, "Client quit", true);

        // Stop the receive loop, and wait for it
        await this.clientTask!;

        // Wait for the linger for a short period of time
        if (lingerMilliseconds > 0)
        {
            await Task.Delay(lingerMilliseconds);
        }

        await this.DisposeAsync();
    }

    /// <summary>
    /// Disposes the client asynchronously.
    /// If it was not stopped, it will be stopped asynchronously.
    /// </summary>
    /// <returns>ValueTask stopping the client.</returns>
    public async ValueTask DisposeAsync()
    {
        // Cleanup managed resources (async)
        await this.DisposeAsyncCore().ConfigureAwait(false);

        // Cleanup unmanaged resources (sync)
        this.Dispose(false);

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes the client synchronously.
    /// If it was not stopped, it will block to stop.
    /// </summary>
    public void Dispose()
    {
        // Cleanup everything (sync)
        this.Dispose(true);

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes the client synchronously.
    /// If it was not stopped, it will block to stop.
    /// </summary>
    /// <param name="disposing">Disposing.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            // managed
            if (disposing)
            {
                this.cancelTokenSrc?.Cancel();
                this.clientTask?.Wait();

                this.cancelToken = CancellationToken.None;
                this.cancelTokenSrc?.Dispose();
                this.cancelTokenSrc = null;
                this.clientTask?.Dispose();
                this.clientTask = null;

                this.MessageReceived = null;
            }

            // unmanaged
            if (this.connection != 0)
            {
                this.netSockets?.CloseConnection(this.connection, 0, "Dispose", false);
                this.connection = 0;
            }

            // managed
            if (disposing)
            {
                this.ConnectionStatusChanged = null;

                this.netSockets?.Dispose();
                this.netSockets = null;

                this.gns?.Dispose();
                this.gns = null;
            }

            // unmanaged
            if (this.gnsLib != IntPtr.Zero)
            {
                NativeLibrary.Free(this.gnsLib);
                this.gnsLib = IntPtr.Zero;
            }

            this.disposed = true;
        }
    }

    /// <summary>
    /// Disposes the client synchronously.
    /// If it was not stopped, it will be stopped asynchronously.
    /// </summary>
    /// <returns>ValueTask representing dispose task.</returns>
    protected virtual async ValueTask DisposeAsyncCore()
    {
        if (!this.disposed)
        {
            if (this.cancelTokenSrc != null)
            {
                await this.cancelTokenSrc.CancelAsync();

                if (this.clientTask != null)
                {
                    await this.clientTask;
                }
            }

            this.cancelToken = CancellationToken.None;
            this.cancelTokenSrc?.Dispose();
            this.cancelTokenSrc = null;
            this.clientTask?.Dispose();
            this.clientTask = null;

            this.MessageReceived = null;

            this.ConnectionStatusChanged = null;

            this.netSockets?.Dispose();
            this.netSockets = null;

            this.gns?.Dispose();
            this.gns = null;

            this.disposed = true;
        }
    }

    private static async Task Main(string[] args)
    {
        Console.WriteLine("GNS-Practice #00: Chat");
        Console.WriteLine("Chat client in C# with Valve.Sockets.AutoGen");
        Console.WriteLine();

        // Prepare server infos
        string hostNameOrAddress = (args.Length >= 1) ? args[0] : string.Empty;
        ushort port = DefaultServerPort;

        // Check if host port is in range, if provided
        if (args.Length >= 2)
        {
            if (!ushort.TryParse(args[1], out port))
            {
                Console.WriteLine($"Invalid port: {args[1]}");
                return;
            }
        }

        Console.WriteLine($"Server Addr: {hostNameOrAddress}, Port: {port}");
        Console.WriteLine();

        ChatClient client = new();
        if (!await client.Connect(hostNameOrAddress, port))
        {
            Console.WriteLine("Too bad...");
            return;
        }

        Console.WriteLine("Connection requested, type /quit to quit.");
        Console.WriteLine();

        // User input loop
        while (!client.cancelToken.IsCancellationRequested)
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
            client.netSockets!.SendMessageToConnection(client.connection, msgMS.GetBuffer(), Convert.ToUInt32(msgMS.Position), Native.k_nSteamNetworkingSend_ReliableNoNagle, out Unsafe.NullRef<long>());
        }

        await client.Stop();

        Console.WriteLine("Quited!");
    }

    /// <summary>
    /// Receive data and run callbacks here.
    /// </summary>
    private async Task ClientLoop()
    {
        var nativeMsgs = new IntPtr[MaxMessagePerReceive];

        while (!this.cancelToken.IsCancellationRequested)
        {
            this.netSockets!.RunCallbacks();
            int receivedMsgCount = this.netSockets.ReceiveMessagesOnConnection(this.connection, nativeMsgs, MaxMessagePerReceive);
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

                    this.MessageReceived!.Invoke(in message[0]);

                    SteamNetworkingMessage.Release(nativeMsgs[i]);
                }
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Callback that's called from the native dll when connection status changed.
    /// </summary>
    /// <param name="info">Connection status changed info.</param>
    private void OnConnectionStatusChanged(ref SteamNetConnectionStatusChangedCallback info)
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
                this.cancelTokenSrc!.Cancel();
                break;
        }
    }

    /// <summary>
    /// Callback that's called from the native dll when a message arrived from the server.
    /// </summary>
    /// <param name="netMsg">The message.</param>
    private void OnMessage(in SteamNetworkingMessage netMsg)
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
}
