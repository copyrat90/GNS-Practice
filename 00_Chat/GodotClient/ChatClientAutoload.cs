// SPDX-License-Identifier: 0BSD

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using GNSPrac.Chat;
using Godot;
using ProtoBuf;
using Valve.Sockets;

/// <summary>
/// Autoload to use the <see cref="Client"/> as a Godot chat client.
/// </summary>
public partial class ChatClientAutoload : Node
{
    private bool valid;

    private Lock chatsLock = new();
    private List<ChatProtocol> chats = new();
    private ThreadLocal<List<ChatProtocol>> chatsTls = new(() => new List<ChatProtocol>());

    /// <summary>
    /// Chat message event handler.
    /// </summary>
    /// <param name="chat">Chat content received.</param>
    public delegate void ChatMessageEventHandler(ChatProtocol chat);

    /// <summary>
    /// Gets the singleton <see cref="ChatClientAutoload"/> object.
    /// </summary>
    public static ChatClientAutoload Instance { get; private set; }

    /// <summary>
    /// Process all the pending chat messages with <paramref name="chatHandler"/>.
    /// </summary>
    /// <param name="chatHandler">Chat handler to process the chat.</param>
    public void ProcessAllPendingChatMessages(ChatMessageEventHandler chatHandler)
    {
        lock (this.chatsLock)
        {
            // Swap the received chats with thread local one
            var temp = this.chats;
            this.chats = this.chatsTls.Value;
            this.chatsTls.Value = temp;

            // Clear the remaining messages of the last process
            this.chats.Clear();
        }

        // Now it's safe to work with `this.chatsTls`!

        // Process the chat messages
        foreach (var chat in this.chatsTls.Value)
        {
            chatHandler?.Invoke(chat);
        }
    }

    /// <summary>
    /// Send a chat protocol message to the host.
    /// </summary>
    /// <param name="chat">Chat protocol message to send.</param>
    /// <returns>Whether the send request has been successfully registered or not.</returns>
    public bool SendChatProtocol(ChatProtocol chat)
    {
        // Serialize the `msg` to a memory stream.
        // In real use case, you would get this from a pool.
        int length = Convert.ToInt32(ProtoBuf.Serializer.Measure<ChatProtocol>(chat).Length);
        MemoryStream memoryStream = new(length);
        ProtoBuf.Serializer.Serialize(memoryStream, chat);

        return Client.Instance.SendMessage(new ReadOnlySpan<byte>(memoryStream.GetBuffer(), 0, length), 0);
    }

    /// <inheritdoc/>
    public override void _Ready()
    {
        if (Instance != null)
        {
            GD.PushError("Duplicated `Handlers`");
            this.QueueFree();
            return;
        }

        Client client = Client.Instance;
        client.ConnectionStatusChanged += this.OnConnectionStatusChanged;
        client.MessageReceived += this.OnMessageReceived;

        Instance = this;
        this.valid = true;
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        if (this.valid)
        {
            Client client = Client.Instance;
            client.ConnectionStatusChanged -= this.OnConnectionStatusChanged;
            client.MessageReceived -= this.OnMessageReceived;
        }
    }

    private void OnConnectionStatusChanged(ref SteamNetConnectionStatusChangedCallback info)
    {
        switch (info.m_info.m_eState)
        {
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                this.GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, "res://chat_scene.tscn");
                break;

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                // Parse the reason of connection close
                SteamNetConnectionInfo connInfo = info.m_info;

                string dbg;
                unsafe
                {
                    dbg = Marshal.PtrToStringUTF8((nint)connInfo.m_szEndDebug);
                }

                this.CallDeferred(MethodName.StopClient, dbg);
                break;
        }
    }

    private void OnMessageReceived(in SteamNetworkingMessage netMsg)
    {
        if (netMsg.m_cbSize == 0)
        {
            GD.PushError("Server sent an empty message");
            return;
        }

        try
        {
            // Unmarshall the unmanaged string
            ChatProtocol msg;
            unsafe
            {
                ReadOnlySpan<byte> msgRaw = new((void*)netMsg.m_pData, netMsg.m_cbSize);
                msg = ProtoBuf.Serializer.Deserialize<ChatProtocol>(msgRaw);
            }

            lock (this.chatsLock)
            {
                this.chats.Add(msg);
            }
        }
        catch (Exception ex)
        {
            GD.PushError($"Failed adding message: {ex.Message}");
        }
    }

    // This should be called from main thread (i.e. deferred)
    private void StopClient(string reason)
    {
        Client.Instance.Stop();

        var currentScene = this.GetTree().Root.GetChild(-1);
        if (currentScene is ConnectScene connectScene)
        {
            connectScene.Connecting = false;
            connectScene.SetDescriptionText(reason);
        }
        else
        {
            this.GetTree().ChangeSceneToFile("res://connect_scene.tscn");
            currentScene = this.GetTree().Root.GetChild(-1);
            if (currentScene is ConnectScene connectScene2)
            {
                connectScene2.SetDescriptionText(reason);
            }
        }
    }
}
