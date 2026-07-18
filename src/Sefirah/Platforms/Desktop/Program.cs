using System.Net.Sockets;
using Sefirah;
using Sefirah.Platforms.Desktop.Tray.DBus;
using Tmds.DBus.Protocol;
using Uno.UI.Hosting;

internal class Program
{
    private const string InstanceServiceName = "com.castle.sefirah";
    private const string InstanceObjectPath = "/com/castle/sefirah";
    private static readonly TimeSpan DBusTimeout = TimeSpan.FromSeconds(2);

    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsMacOS())
        {
            if (!MacOsSingleInstance.TryAcquire(out var singleInstance))
                return;

            try
            {
                RunHost();
            }
            finally
            {
                singleInstance.Dispose();
            }

            return;
        }

        RunWithDBusSingleInstance();
    }

    private static void RunHost()
    {
        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseMacOS()
            .UseX11()
            .UseLinuxFrameBuffer()
            .Build();

        host.Run();
    }

    private static void RunWithDBusSingleInstance()
    {
        DBusConnection? connection = null;
        InstanceHandler? handler = null;
        var shouldRedirect = false;

        var sessionAddress = DBusAddress.Session;
        if (sessionAddress is not null)
        {
            try
            {
                connection = new DBusConnection(sessionAddress);
                connection.ConnectAsync()
                    .AsTask()
                    .WaitAsync(DBusTimeout)
                    .GetAwaiter()
                    .GetResult();

                handler = new InstanceHandler(connection, () =>
                {
                    if (App.Current is App)
                        App.MainWindow.DispatcherQueue.TryEnqueue(App.ShowMainWindow);
                });
                connection.AddMethodHandler(handler);

                shouldRedirect = !connection
                    .TryRequestNameAsync(InstanceServiceName, default)
                    .WaitAsync(DBusTimeout)
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                if (handler is not null)
                    connection?.RemoveMethodHandler(handler.Path);

                connection?.Dispose();
                connection = null;
                handler = null;
            }
        }

        if (shouldRedirect && connection is not null)
        {
            if (handler is not null)
                connection.RemoveMethodHandler(handler.Path);

            try
            {
                MessageBuffer message;
                using (var writer = connection.GetMessageWriter())
                {
                    writer.WriteMethodCallHeader(
                        destination: InstanceServiceName,
                        path: InstanceObjectPath,
                        @interface: "com.castle.sefirah.SingleInstance",
                        signature: default,
                        member: "Activate");
                    message = writer.CreateMessage();
                }

                connection.CallMethodAsync(message)
                    .WaitAsync(DBusTimeout)
                    .GetAwaiter()
                    .GetResult();
            }
            finally
            {
                connection.Dispose();
            }

            return;
        }

        try
        {
            RunHost();
        }
        finally
        {
            if (handler is not null)
                connection?.RemoveMethodHandler(handler.Path);

            connection?.Dispose();
        }
    }

    private sealed class InstanceHandler(DBusConnection connection, Action activated) : DBusHandler(connection, InstanceObjectPath, handlesChildPaths: false), ISingleInstanceHandler
    {
        ValueTask ISingleInstanceHandler.ActivateAsync()
        {
            activated();
            return default;
        }
    }
}

/// <summary>
/// File-lock + Unix domain socket single-instance for macOS (no session D-Bus).
/// </summary>
file sealed class MacOsSingleInstance : IDisposable
{
    private static readonly string SupportDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sefirah");

    private static readonly string LockPath = Path.Combine(SupportDir, "instance.lock");
    private static readonly string SocketPath = Path.Combine(SupportDir, "instance.sock");

    private readonly FileStream lockStream;
    private readonly Socket? listenSocket;
    private readonly CancellationTokenSource cts = new();
    private bool disposed;

    private MacOsSingleInstance(FileStream lockStream, Socket? listenSocket)
    {
        this.lockStream = lockStream;
        this.listenSocket = listenSocket;
        if (listenSocket is not null)
            _ = AcceptLoopAsync(cts.Token);
    }

    public static bool TryAcquire(out MacOsSingleInstance instance)
    {
        Directory.CreateDirectory(SupportDir);

        try
        {
            var stream = new FileStream(
                LockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            TryCleanupSocket();
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            listener.Listen(1);

            instance = new MacOsSingleInstance(stream, listener);
            return true;
        }
        catch (IOException)
        {
            // Another instance holds the lock — ask it to activate.
            TryActivateExisting();
            instance = null!;
            return false;
        }
        catch
        {
            // If socket setup fails, still allow the app to run without single-instance.
            try
            {
                var stream = new FileStream(
                    LockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
                instance = new MacOsSingleInstance(stream, null);
                return true;
            }
            catch (IOException)
            {
                TryActivateExisting();
                instance = null!;
                return false;
            }
        }
    }

    private static void TryActivateExisting()
    {
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(SocketPath));
            client.Send("activate"u8);
        }
        catch
        {
            // Best-effort; primary instance may not be listening yet.
        }
    }

    private static void TryCleanupSocket()
    {
        try
        {
            if (File.Exists(SocketPath))
                File.Delete(SocketPath);
        }
        catch
        {
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (listenSocket is null)
            return;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listenSocket.AcceptAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch
        {
        }
    }

    private static async Task HandleClientAsync(Socket client, CancellationToken cancellationToken)
    {
        try
        {
            using (client)
            {
                var buffer = new byte[32];
                _ = await client.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            }

            if (App.Current is App)
                App.MainWindow.DispatcherQueue.TryEnqueue(App.ShowMainWindow);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        cts.Cancel();
        cts.Dispose();
        listenSocket?.Dispose();
        lockStream.Dispose();
        TryCleanupSocket();
    }
}
