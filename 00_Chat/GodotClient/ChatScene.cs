// SPDX-License-Identifier: 0BSD

using GNSPrac.Chat;
using Godot;

public partial class ChatScene : Control
{
    private ScrollContainer chatLogScroll;
    private VScrollBar chatVScrollBar;
    private VBoxContainer chatLogVBox;
    private LineEdit chatEdit;

    private double maxScroll = 0;

    /// <inheritdoc/>
    public override void _Ready()
    {
        this.chatLogScroll = this.GetNode<ScrollContainer>("%ChatLogScroll");
        this.chatLogVBox = this.GetNode<VBoxContainer>("%ChatLogVBox");
        this.chatEdit = this.GetNode<LineEdit>("%ChatEdit");

        this.chatVScrollBar = this.chatLogScroll.GetVScrollBar();

        this.chatEdit.TextSubmitted += this.OnChatSendRequest;
        this.chatVScrollBar.Changed += this.OnScrollbarChanged;

        Label label = new() { Text = "To change your name, type /name <your new name>." };
        this.chatLogVBox.AddChild(label);
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        ChatClientAutoload.Instance.ProcessAllPendingChatMessages(this.OnChatReceived);
    }

    private void OnChatSendRequest(string message)
    {
        // Strip message
        message = message.StripEdges();

        // Ignore empty message
        if (message.Length != 0)
        {
            ChatProtocol msg = null;

            // If the user requested a new name
            string[] split = message.Split();
            if (split.Length > 0 && split[0] == "/name")
            {
                if (split.Length < 2)
                {
                    this.AddLine("You should provide a new name after /name");
                }
                else
                {
                    // Prepare new name
                    string newName = string.Join(' ', split[1..]);
                    msg = new()
                    {
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
                    Chat = new() { Content = message },
                };

                // Add chat as a label to the vbox
                this.AddLine("You: " + message);
            }

            if (msg != null)
            {
                // Send the chat protocol message
                ChatClientAutoload.Instance.SendChatProtocol(msg);
            }
        }

        // Clear line edit
        this.chatEdit.Text = string.Empty;
    }

    private void OnChatReceived(ChatProtocol chat)
    {
        if (chat.MsgCase == ChatProtocol.MsgOneofCase.Chat)
        {
            // Error on empty chat sent from server
            if (chat.Chat == null || chat.Chat.Content == null || chat.Chat.Content.Length == 0)
            {
                GD.PushError("Server sent an empty chat");
                return;
            }

            // Add chat as a label to the vbox
            this.AddLine($"{chat.Chat.SenderName ?? "(Invalid sender)"}: {chat.Chat.Content}");
        }
        else
        {
            // Server shouldn't send other type of messages
            GD.PushError($"Server sent an invalid message type: {chat.MsgCase}");
        }
    }

    private void OnScrollbarChanged()
    {
        // If max scroll size changed, because of new chat message
        if (this.maxScroll != this.chatVScrollBar.MaxValue)
        {
            this.maxScroll = this.chatVScrollBar.MaxValue;

            // Scroll to bottom
            this.chatLogScroll.ScrollVertical = (int)this.maxScroll;
        }
    }

    private void AddLine(string line)
    {
        Label label = new() { Text = line };
        this.chatLogVBox.AddChild(label);
    }
}
