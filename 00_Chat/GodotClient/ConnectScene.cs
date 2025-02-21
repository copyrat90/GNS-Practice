// SPDX-License-Identifier: 0BSD

using System;
using System.Collections.Generic;
using System.Threading;
using Godot;

public partial class ConnectScene : CenterContainer
{
    private LineEdit addrEdit;
    private LineEdit portEdit;
    private Button connectButton;
    private ProgressBar progressBar;
    private Label description;

    private byte connecting = 0;

    /// <summary>
    /// Gets or sets a value indicating whether it's currently connecting or not.
    /// Setting this updates the UI respectively.
    /// </summary>
    public bool Connecting
    {
        get
        {
            return this.connecting != (byte)0;
        }

        set
        {
            byte val = value ? (byte)1 : (byte)0;
            byte notVal = value ? (byte)0 : (byte)1;

            if (notVal == Interlocked.CompareExchange(ref this.connecting, val, notVal))
            {
                this.connectButton.SetDeferred(Button.PropertyName.Disabled, value);
                this.progressBar.SetDeferred(ProgressBar.PropertyName.Indeterminate, value);
            }
        }
    }

    /// <inheritdoc/>
    public override void _Ready()
    {
        this.addrEdit = this.GetNode<LineEdit>("%AddrEdit");
        this.portEdit = this.GetNode<LineEdit>("%PortEdit");
        this.connectButton = this.GetNode<Button>("%ConnectButton");
        this.progressBar = this.GetNode<ProgressBar>("%ProgressBar");
        this.description = this.GetNode<Label>("%Description");

        this.connectButton.Pressed += this.OnConnectRequested;
        this.addrEdit.TextSubmitted += (rawAddr) => this.OnConnectRequested();
        this.portEdit.TextSubmitted += (rawPort) => this.OnConnectRequested();
    }

    /// <summary>
    /// Set the description text shown on screen.
    /// </summary>
    /// <param name="text">String to show.</param>
    public void SetDescriptionText(string text)
    {
        this.description.SetDeferred(Label.PropertyName.Text, text);
    }

    private async void OnConnectRequested()
    {
        // Parse IP address & port
        string rawAddr = this.addrEdit.Text;
        string rawPort = this.portEdit.Text;

        if (!ushort.TryParse(rawPort, out ushort port))
        {
            this.description.Text = $"Invalid port: {rawPort}";
            return;
        }

        this.Connecting = true;

        try
        {
            await Client.Instance.ConnectAsync(rawAddr, port);
        }
        catch (Exception ex)
        {
            this.SetDescriptionText($"Error: {ex.Message}");
            this.Connecting = false;
        }
    }
}
