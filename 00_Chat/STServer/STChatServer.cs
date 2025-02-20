// SPDX-License-Identifier: 0BSD

namespace GNSPrac.Chat;

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using ProtoBuf;
using Valve.Sockets;

/// <summary>
/// Single-threaded chat server.
/// </summary>
public class STChatServer : IAsyncDisposable, IDisposable
{
    private const ushort DefaultServerPort = 45700;

    private const int MaxMessagePerReceive = 100;

    private bool disposed;

    private IntPtr gnsLib;

    private GameNetworkingSockets? gns;
    private SteamNetworkingSockets? netSockets;
    private uint pollGroup = 0;
    private uint listenSocket;

    private Dictionary<uint, ClientInfo>? clients;

    private Task? serverTask;
    private CancellationTokenSource? cancelTokenSrc;
    private CancellationToken cancelToken;

    /// <summary>
    /// Initializes a new instance of the <see cref="STChatServer"/> class.
    /// This suppresses finalizing, because the server won't start in construction.
    /// </summary>
    public STChatServer()
    {
        this.disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizes an instance of the <see cref="STChatServer"/> class.
    /// </summary>
    ~STChatServer() => this.Dispose(false);

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
    /// Start the server with specified port.
    /// </summary>
    /// <param name="port">Port to listen.</param>
    /// <returns>Whether the server has been started to run, or errored.</returns>
    public bool Start(ushort port)
    {
        // Prevent starting twice
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

            this.gnsLib = NativeLibrary.Load(Path.Join(AppContext.BaseDirectory, libName));

            // Initialize `Valve.Sockets.AutoGen`
            this.gns = new GameNetworkingSockets();

            // Prepare net sockets & poll group
            this.netSockets = new SteamNetworkingSockets();
            this.pollGroup = this.netSockets.CreatePollGroup();

            // Manage connected clients' info with a dictionary.
            // Note that a client might not logged in yet.
            this.clients = [];

            // Setup the connection status changed callback delegate instance.
            // This delegate instance should live until the server is closed, because it's called from native dll.
            this.ConnectionStatusChanged = new(this.OnConnectionStatusChanged);

            // Setup configuration used for listen socket
            Span<SteamNetworkingConfigValue> configs = stackalloc SteamNetworkingConfigValue[1];
            unsafe
            {
                configs[0].SetPtr(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_Callback_ConnectionStatusChanged, (void*)Marshal.GetFunctionPointerForDelegate(this.ConnectionStatusChanged));
            }

            // Start listening
            SteamNetworkingIPAddr addr = default;
            addr.Clear();
            addr.m_port = port;
            this.listenSocket = this.netSockets.CreateListenSocketIP(addr, 1, configs);
            if (this.listenSocket == 0)
            {
                throw new Exception("Failed to create a listen socket");
            }

            // Setup the on message callback delegate instance.
            // This delegate instance should live until the server is closed, because it's called from native dll.
            this.MessageReceived = new(this.OnMessage);

            // Create the server loop as a task
            this.cancelTokenSrc = new();
            this.cancelToken = this.cancelTokenSrc.Token;
            this.serverTask = Task.Run(this.ServerLoop);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to start STChatServer: " + ex.Message);
            Console.WriteLine(ex.StackTrace);

            this.Dispose();

            return false;
        }

        return true;
    }

    /// <summary>
    /// Stop the server.
    /// </summary>
    /// <param name="lingerMilliseconds">Milliseconds to wait before dropping connections. This can be useful if you want to send a goodbye message or similar.</param>
    /// <returns>ValueTask stopping the server.</returns>
    public async ValueTask Stop(int lingerMilliseconds = 0)
    {
        if (this.disposed)
        {
            return;
        }

        Console.WriteLine($"Stopping the server loop...");

        // Stop the server loop
        await this.cancelTokenSrc!.CancelAsync();

        Console.WriteLine($"Closing connections...");

        // Close all the connections with linger enabled
        foreach (uint clientConn in this.clients!.Keys)
        {
            this.netSockets!.CloseConnection(clientConn, 0, "Server shutdown", true);
        }

        // Wait for the server loop task to stop
        await this.serverTask!;

        // Wait for the linger for a short period of time
        if (lingerMilliseconds > 0)
        {
            await Task.Delay(lingerMilliseconds);
        }

        // This should be AFTER lingering, because closing listen socket drops all connections accepted from it
        if (this.netSockets!.CloseListenSocket(this.listenSocket))
        {
            this.netSockets = null;
        }
        else
        {
            throw new UnreachableException("CloseListenSocket() failed");
        }

        await this.DisposeAsync();
    }

    /// <summary>
    /// Disposes the server asynchronously.
    /// If it was not stopped, it will be stopped asynchronously.
    /// </summary>
    /// <returns>ValueTask stopping the server.</returns>
    public async ValueTask DisposeAsync()
    {
        // Cleanup managed resources (async)
        await this.DisposeAsyncCore().ConfigureAwait(false);

        // Cleanup unmanaged resources (sync)
        this.Dispose(false);

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes the server synchronously.
    /// If it was not stopped, it will block to stop.
    /// </summary>
    public void Dispose()
    {
        // Cleanup everything (sync)
        this.Dispose(true);

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes the server synchronously.
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
                this.serverTask?.Wait();

                this.cancelToken = CancellationToken.None;
                this.cancelTokenSrc?.Dispose();
                this.cancelTokenSrc = null;
                this.serverTask?.Dispose();
                this.serverTask = null;

                this.MessageReceived = null;
            }

            // unmanaged
            if (this.listenSocket != 0)
            {
                this.netSockets?.CloseListenSocket(this.listenSocket);
                this.listenSocket = 0;
            }

            // managed
            if (disposing)
            {
                this.ConnectionStatusChanged = null;
                this.clients = null;
            }

            // unmanaged
            if (this.pollGroup != 0)
            {
                this.netSockets?.DestroyPollGroup(this.pollGroup);
                this.pollGroup = 0;
            }

            // managed
            if (disposing)
            {
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
    /// Disposes the server synchronously.
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

                if (this.serverTask != null)
                {
                    await this.serverTask;
                }
            }

            this.cancelToken = CancellationToken.None;
            this.cancelTokenSrc?.Dispose();
            this.cancelTokenSrc = null;
            this.serverTask?.Dispose();
            this.serverTask = null;

            this.MessageReceived = null;

            this.ConnectionStatusChanged = null;
            this.clients = null;

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
        Console.WriteLine("Single-threaded chat server in C# with Valve.Sockets.AutoGen");
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

        // Start the server with specified port
        await using STChatServer server = new();
        if (!server.Start(port))
        {
            Console.WriteLine("Too bad...");
            return;
        }

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

        // Let's quit the server now!

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

        // Send the final message
        // (Ideally, this should be done inside of `Stop()`'s callback, but eh)
        foreach (uint clientConn in server.clients!.Keys)
        {
            server.netSockets!.SendMessageToConnection(clientConn, finalMsgMS.GetBuffer(), Convert.ToUInt32(finalMsgMS.Position), Native.k_nSteamNetworkingSend_ReliableNoNagle, out Unsafe.NullRef<long>());
        }

        // Stop the server
        await server.Stop(lingerMilliseconds: 500);

        Console.WriteLine("Server closed!");
    }

    /// <summary>
    /// Receive data and run callbacks here.
    /// </summary>
    private async Task ServerLoop()
    {
        var nativeMsgs = new IntPtr[MaxMessagePerReceive];

        while (!this.cancelToken.IsCancellationRequested)
        {
            this.netSockets!.RunCallbacks();
            int receivedMsgCount = this.netSockets.ReceiveMessagesOnPollGroup(this.pollGroup, nativeMsgs, MaxMessagePerReceive);
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
    /// Callback that's called from the native dll when connection status changed for any client.
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

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                // Accept the connection.
                // You could also close the connection right away.
                EResult acceptResult = this.netSockets!.AcceptConnection(info.m_hConn);

                // If accept failed, clean up the connection.
                if (acceptResult != EResult.k_EResultOK)
                {
                    this.netSockets.CloseConnection(info.m_hConn, 0, "Accept failure", false);
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
                this.clients!.Add(info.m_hConn, newClientInfo);

                // Assign new client to the poll group
                if (!this.netSockets.SetConnectionPollGroup(info.m_hConn, this.pollGroup))
                {
                    this.clients.Remove(info.m_hConn);
                    this.netSockets.CloseConnection(info.m_hConn, 0, "Poll group assign failure", false);

                    Console.WriteLine($"Failed to assign poll group");
                    break;
                }

                Console.WriteLine($"New client #{info.m_hConn} connected!");

                break;

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                // Connection changed callbacks are dispatched in FIFO order.

                // Get the client from `clients`
                ClientInfo client = this.clients![info.m_hConn];

                // Print the reason of connection close
                SteamNetConnectionInfo connInfo = info.m_info;
                string clientName = client.Name ?? "(not logged-in client)";
                string state = connInfo.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer ? "closed by peer" : "problem detected locally";
                string? desc, dbg;
                unsafe
                {
                    desc = Marshal.PtrToStringAnsi((nint)connInfo.m_szConnectionDescription);
                    dbg = Marshal.PtrToStringAnsi((nint)connInfo.m_szEndDebug);
                }

                Console.WriteLine($"{clientName} ({connInfo.m_addrRemote}) {desc ?? "(Invalid desc)"} ({state}), reason {connInfo.m_eEndReason}: {dbg ?? "(Invalid dbg)"}");

                // Remove it from the clients dictionary
                this.clients.Remove(info.m_hConn);

                // Don't forget to clean up the connection!
                this.netSockets!.CloseConnection(info.m_hConn, 0, null, false);

                break;

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                // Callback after accepting the connection.
                // Nothing to do here, as we're the server.
                break;
        }
    }

    /// <summary>
    /// Callback that's called from the native dll when a message arrived from any client.
    /// </summary>
    /// <param name="netMsg">The message.</param>
    private void OnMessage(in SteamNetworkingMessage netMsg)
    {
        // Ignore the empty message.
        // In this case, `netMsg.data` is nullptr
        if (netMsg.m_cbSize == 0)
        {
            Console.WriteLine("Client sent an empty message");
            return;
        }

        // Unmarshall the unmanaged message
        ChatProtocol msg;
        unsafe
        {
            ReadOnlySpan<byte> msgRaw = new((void*)netMsg.m_pData, netMsg.m_cbSize);
            msg = ProtoBuf.Serializer.Deserialize<ChatProtocol>(msgRaw);
        }

        // Get the client from `clients` dictionary.
        // It must exist in the dictionary, because we added it on `ConnectionState.Connecting`
        ClientInfo client = this.clients![netMsg.m_conn];

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
                            SenderName = client.Name ?? $"Guest#{netMsg.m_conn}",
                            Content = msg.Chat.Content,
                        },
                    };

                    // Serialize the response to a memory stream.
                    // In real use case, you would get this from a pool.
                    MemoryStream responseMS = new();
                    ProtoBuf.Serializer.Serialize(responseMS, response);

                    // Propagate the response to other clients.
                    foreach (var otherClientConn in this.clients.Keys)
                    {
                        // Ignore itself
                        if (otherClientConn != netMsg.m_conn)
                        {
                            this.netSockets!.SendMessageToConnection(otherClientConn, responseMS.GetBuffer(), Convert.ToUInt32(responseMS.Position), Native.k_nSteamNetworkingSend_ReliableNoNagle, out Unsafe.NullRef<long>());
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
                        Console.WriteLine($"Client #{netMsg.m_conn} changed their name to {client.Name}");
                    }

                    // Prepare the response to the client about their current name
                    ChatProtocol response = new()
                    {
                        Type = ChatProtocol.MsgType.MsgTypeChat,
                        Chat = new()
                        {
                            SenderName = "Server",
                            Content = $"Your name is now {client.Name ?? $"Guest#{netMsg.m_conn}"}",
                        },
                    };

                    // Serialize the response to a memory stream.
                    // In real use case, you would get this from a pool.
                    MemoryStream responseMS = new();
                    ProtoBuf.Serializer.Serialize(responseMS, response);

                    // Notify to the client about their current name
                    this.netSockets!.SendMessageToConnection(netMsg.m_conn, responseMS.GetBuffer(), Convert.ToUInt32(responseMS.Position), Native.k_nSteamNetworkingSend_ReliableNoNagle, out Unsafe.NullRef<long>());
                    break;
                }

            default:
                // Client shouldn't send other type of messages
                Console.WriteLine($"Client sent an invalid message type: {msg.Type}");
                break;
        }
    }

    private class ClientInfo
    {
        public string? Name { get; set; }
    }
}
