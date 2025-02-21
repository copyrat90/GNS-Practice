// SPDX-License-Identifier: 0BSD

using System;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Valve.Sockets;

/// <summary>
/// Client that connects to a server using GameNetworkingSockets.
/// </summary>
public class Client : IAsyncDisposable, IDisposable
{
    private const int DefaultLoopDelayMilliseconds = 10;
    private const int DefaultMaxMessagesPerReceive = 100;

    private static readonly Lazy<Client> LazyInstance = new(() => new Client());

    private bool disposed;

    private SteamNetworkingSockets netSockets;
    private uint connection;

    private Task clientTask;
    private CancellationTokenSource cancelTokenSrc;
    private CancellationToken cancelToken;
    private int loopDelayMilliseconds;

    private int maxMessagesPerReceive;

    /// <summary>
    /// Initializes a new instance of the <see cref="Client"/> class.
    /// This suppresses finalizing, because the client won't start in construction.
    /// </summary>
    private Client()
    {
        this.disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizes an instance of the <see cref="Client"/> class.
    /// </summary>
    ~Client() => this.Dispose(false);

    /// <summary>
    /// Message received delegate type.
    /// </summary>
    /// <param name="message">Message arrived via GameNetworkingSockets.</param>
    public delegate void MessageReceivedEventHandler(in SteamNetworkingMessage message);

    /// <summary>
    /// Event that fires when a message arrives via GameNetworkingSockets.
    /// You should set this before <see cref="Connect"/>, and unset this after <see cref="Stop"/>.
    /// </summary>
    public event MessageReceivedEventHandler MessageReceived;

    /// <summary>
    /// Event that fires when a connection status changed in GameNetworkingSockets.
    /// You should set this before <see cref="Connect"/>, and unset this after <see cref="Stop"/>.
    /// </summary>
    public event FnSteamNetConnectionStatusChanged ConnectionStatusChanged;

    /// <summary>
    /// Gets the singleton <see cref="Client"/> object.
    /// </summary>
    public static Client Instance
    {
        get { return LazyInstance.Value; }
    }

    /// <summary>
    /// Send a message to the host.
    /// </summary>
    /// <param name="data">Data to send.</param>
    /// <param name="sendFlags">Send flags (e.g. <see cref="Native.k_nSteamNetworkingSend_ReliableNoNagle"/> ).</param>
    /// <returns>Whether the send request has been successfully registered or not.</returns>
    public bool SendMessage(ReadOnlySpan<byte> data, int sendFlags)
    {
        if (this.disposed)
        {
            return false;
        }

        var result = this.netSockets.SendMessageToConnection(this.connection, data, Convert.ToUInt32(data.Length), sendFlags, out Unsafe.NullRef<long>());

        return result == EResult.k_EResultOK;
    }

    /// <summary>
    /// Connect to the server asynchronously with specified host address & port.
    /// If fails, this will throw an exception.
    /// </summary>
    /// <param name="hostNameOrAddress">Host name or address to connect to.</param>
    /// <param name="hostPort">Host port to connect to.</param>
    /// <param name="loopDelayMilliseconds">Delay in milliseconds for next message receives and callbacks.</param>
    /// <param name="maxMessagesPerReceive">How many messages will be received once.</param>
    /// <returns>Task that represents connection request. Note that returning `true` means request succeed, not connected.</returns>
    public async Task ConnectAsync(string hostNameOrAddress, ushort hostPort, int loopDelayMilliseconds = DefaultLoopDelayMilliseconds, int maxMessagesPerReceive = DefaultMaxMessagesPerReceive)
    {
        // Prevent connecting twice
        if (!this.disposed)
        {
            return;
        }

        this.disposed = false;
        GC.ReRegisterForFinalize(this);

        try
        {
            // Setup parameters
            if (loopDelayMilliseconds < 0)
            {
                throw new Exception($"Invalid loopDelayMilliseconds: {loopDelayMilliseconds}");
            }

            if (maxMessagesPerReceive <= 0)
            {
                throw new Exception($"Invalid maxMessagesPerReceive: {maxMessagesPerReceive}");
            }

            this.loopDelayMilliseconds = loopDelayMilliseconds;
            this.maxMessagesPerReceive = maxMessagesPerReceive;

            // Check if callbacks are properly set up
            if (this.ConnectionStatusChanged == null)
            {
                throw new Exception("ConnectionStatusChanged event is not set");
            }

            if (this.MessageReceived == null)
            {
                throw new Exception("MessageReceived event is not set");
            }

            // Setup the address
            IPAddress[] ipAddrs = await Dns.GetHostAddressesAsync(hostNameOrAddress);

            if (ipAddrs == null || ipAddrs.Length == 0)
            {
                throw new Exception("Host not found");
            }

            // Load GNS
            GNS.Instance.Load();

            // Setup the address
            SteamNetworkingIPAddr address = default;
            address.SetIPv6(ipAddrs[0].MapToIPv6().GetAddressBytes(), hostPort);

            // Prepare net sockets
            this.netSockets = new SteamNetworkingSockets();

            // Setup configuration used for connection
            Span<SteamNetworkingConfigValue> configs = stackalloc SteamNetworkingConfigValue[1];
            unsafe
            {
                configs[0].SetPtr(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_Callback_ConnectionStatusChanged, (void*)Marshal.GetFunctionPointerForDelegate(this.ConnectionStatusChanged));
            }

            // Start connecting
            this.connection = this.netSockets.ConnectByIPAddress(address, configs.Length, configs);

            // Create the client loop as a task
            this.cancelTokenSrc = new();
            this.cancelToken = this.cancelTokenSrc.Token;
            this.clientTask = Task.Run(this.ClientLoop, this.cancelToken);
        }
        catch (Exception ex)
        {
            GD.Print("Failed to connect to server: " + ex.Message);
            GD.Print(ex.StackTrace);

            this.Dispose();

            throw;
        }
    }

    /// <summary>
    /// Stop the client asynchronously.
    /// </summary>
    /// <param name="lingerMilliseconds">Milliseconds to wait before dropping connections. This can be useful if you want to send a goodbye message or similar.</param>
    /// <returns>ValueTask stopping the client.</returns>
    public async ValueTask StopAsync(int lingerMilliseconds = 0)
    {
        if (this.disposed)
        {
            return;
        }

        GD.Print($"Stopping the client loop...");

        // Stop the receive loop
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
    /// Stop the client.
    /// </summary>
    /// <param name="lingerMilliseconds">Milliseconds to wait before dropping connections. This can be useful if you want to send a goodbye message or similar.</param>
    public void Stop(int lingerMilliseconds = 0)
    {
        if (this.disposed)
        {
            return;
        }

        GD.Print($"Stopping the client loop...");

        // Stop the receive loop
        this.cancelTokenSrc!.Cancel();

        // Close the connection with linger enabled
        this.netSockets!.CloseConnection(this.connection, 0, "Client quit", true);

        // Stop the receive loop, and wait for it
        this.clientTask!.Wait();

        // Wait for the linger for a short period of time
        if (lingerMilliseconds > 0)
        {
            Thread.Sleep(lingerMilliseconds);
        }

        this.Dispose();
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
                this.netSockets?.Dispose();
                this.netSockets = null;
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

            this.netSockets?.Dispose();
            this.netSockets = null;

            this.disposed = true;
        }
    }

    /// <summary>
    /// Receive data and run callbacks here.
    /// </summary>
    private async Task ClientLoop()
    {
        var nativeMsgs = new IntPtr[this.maxMessagesPerReceive];

        try
        {
            while (!this.cancelToken.IsCancellationRequested)
            {
                this.netSockets!.RunCallbacks();
                int receivedMsgCount = this.netSockets.ReceiveMessagesOnConnection(this.connection, nativeMsgs, this.maxMessagesPerReceive);
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

                        this.MessageReceived.Invoke(in message[0]);

                        SteamNetworkingMessage.Release(nativeMsgs[i]);
                    }
                }

                await Task.Delay(this.loopDelayMilliseconds, this.cancelToken);
            }
        }
        catch (TaskCanceledException)
        {
        }
    }
}
