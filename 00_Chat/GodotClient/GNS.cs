// SPDX-License-Identifier: 0BSD

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Valve.Sockets;

/// <summary>
/// Loads and unloads GameNetworkingSockets.
/// </summary>
public sealed class GNS : IDisposable
{
    private static readonly Lazy<GNS> LazyGNS = new(() => new GNS());

    private bool disposed;

    private IntPtr gnsLib;
    private GameNetworkingSockets gns;

    /// <summary>
    /// Initializes a new instance of the <see cref="GNS"/> class.
    /// This suppresses finalizing, because GNS won't be loaded in construction.
    /// </summary>
    private GNS()
    {
        this.disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizes an instance of the <see cref="GNS"/> class.
    /// </summary>
    ~GNS() => this.Dispose(false);

    /// <summary>
    /// Gets the singleton <see cref="GNS"/> object.
    /// </summary>
    public static GNS Instance
    {
        get
        {
            return LazyGNS.Value;
        }
    }

    /// <summary>
    /// Loads the GNS. If fails, this will throw an exception.
    /// </summary>
    public void Load()
    {
        // Prevent loading twice
        if (!this.disposed)
        {
            return;
        }

        this.disposed = false;
        GC.ReRegisterForFinalize(this);

        try
        {
            string libName;

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

            // Initialize `Valve.Sockets.Regen`
            this.gns = new GameNetworkingSockets();
        }
        catch
        {
            this.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Unloads the GNS.
    /// </summary>
    public void Unload()
    {
        this.Dispose();
    }

    /// <summary>
    /// Disposes the GNS.
    /// </summary>
    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            if (disposing)
            {
                this.gns?.Dispose();
                this.gns = null;
            }

            if (this.gnsLib != IntPtr.Zero)
            {
                NativeLibrary.Free(this.gnsLib);
                this.gnsLib = IntPtr.Zero;
            }

            this.disposed = true;
        }
    }
}
