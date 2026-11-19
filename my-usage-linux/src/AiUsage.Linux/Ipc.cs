using System.Net.Sockets;
using System.Text;

namespace AiUsage;

/// <summary>
/// One copy of the app per session, and a way to talk to it: `usagetop toggle`
/// from a window manager's key binding, `usagetop status` from a bar. A Unix
/// socket in the session's runtime folder; one line in, the answer out.
/// </summary>
public static class Ipc
{
    public static string SocketPath => Path.Combine(Desktop.RuntimeDir, "usagetop.sock");

    /// <summary>The running copy's answer, or null when none is running.</summary>
    public static string? Send(string command, int timeoutMs = 3000)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = socket.ReceiveTimeout = timeoutMs;
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
            socket.Send(Encoding.UTF8.GetBytes(command + "\n"));
            socket.Shutdown(SocketShutdown.Send);

            var answer = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = socket.Receive(buffer)) > 0) answer.Write(buffer, 0, read);
            return Encoding.UTF8.GetString(answer.ToArray());
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Starts answering. False when another copy already is: this one should
    /// hand over and leave. A socket file nobody answers on is a crashed
    /// copy's, and is taken over.
    /// </summary>
    public static bool TryListen(Func<string, Task<string>> handle, out IDisposable? listener)
    {
        listener = null;
        var path = SocketPath;
        if (File.Exists(path))
        {
            if (Send("ping", 1000) != null) return false;
            File.Delete(path);
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(path));
        }
        catch (SocketException)
        {
            // Another copy bound it between the look and the bind.
            socket.Dispose();
            return false;
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        socket.Listen(8);
        _ = AcceptAsync(socket, handle);
        listener = new Listener(socket, path);
        return true;
    }

    private static async Task AcceptAsync(Socket socket, Func<string, Task<string>> handle)
    {
        while (true)
        {
            Socket client;
            try
            {
                client = await socket.AcceptAsync();
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = AnswerAsync(client, handle);
        }
    }

    private static async Task AnswerAsync(Socket client, Func<string, Task<string>> handle)
    {
        try
        {
            using (client)
            await using (var stream = new NetworkStream(client))
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
                var command = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) ?? "";
                var answer = await handle(command.Trim());
                await stream.WriteAsync(Encoding.UTF8.GetBytes(answer));
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or TimeoutException or ObjectDisposedException)
        {
            // Whoever asked has gone; there is nobody to tell.
        }
    }

    private sealed class Listener(Socket socket, string path) : IDisposable
    {
        public void Dispose()
        {
            socket.Dispose();
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
