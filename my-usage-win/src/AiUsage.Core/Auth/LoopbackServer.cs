using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace AiUsage.Auth;

/// <summary>What the browser handed back on the loopback redirect.</summary>
public sealed record CallbackResult(string? Code, string? State, string? Error);

/// <summary>
/// Catches the OAuth redirect on the loopback interface, so the browser hands the
/// code straight back -- no copy-pasting codes or tokens.
///
/// A raw <see cref="TcpListener"/> rather than <see cref="HttpListener"/>: the latter
/// needs a URL ACL or elevation for any prefix, which a tray app should not ask for.
/// The exchange is one GET and one tiny page, so parsing it by hand is cheaper than
/// the permission.
/// </summary>
public sealed class LoopbackServer : IDisposable
{
    private readonly List<TcpListener> _listeners = new();
    private readonly Channel<TcpClient> _accepted = Channel.CreateUnbounded<TcpClient>();
    private readonly string _path;

    public LoopbackServer(string path, int? fixedPort)
    {
        _path = path;

        // "localhost" rather than the 127.0.0.1 literal: Anthropic's OAuth client
        // only has the hostname registered, and rejects the IP form outright.
        var v4 = new TcpListener(IPAddress.Loopback, fixedPort ?? 0);
        v4.Start();
        _listeners.Add(v4);
        Port = ((IPEndPoint)v4.LocalEndpoint).Port;

        // localhost resolves to ::1 as well as 127.0.0.1, and the browser picks
        // whichever it likes, so both have to answer on the same port.
        try
        {
            var v6 = new TcpListener(IPAddress.IPv6Loopback, Port);
            v6.Start();
            _listeners.Add(v6);
        }
        catch (SocketException)
        {
            // No IPv6 on this machine; the v4 socket is enough.
        }

        foreach (var listener in _listeners) AcceptLoop(listener);
    }

    public int Port { get; }

    public string RedirectUri => $"http://localhost:{Port}{_path}";

    private void AcceptLoop(TcpListener listener)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch
                {
                    // Stopped, or the socket died; nothing left to accept.
                    break;
                }

                if (!_accepted.Writer.TryWrite(client))
                {
                    client.Dispose();
                    break;
                }
            }
        });
    }

    /// <summary>
    /// Waits for the redirect. Browsers ask for /favicon.ico unprompted, so anything
    /// that is not the callback path is answered and ignored rather than treated as
    /// the result.
    /// </summary>
    public async Task<CallbackResult> WaitAsync(TimeSpan timeout, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);

        while (true)
        {
            TcpClient client;
            try
            {
                client = await _accepted.Reader.ReadAsync(deadline.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ChannelClosedException)
            {
                throw new TimeoutException("Sign-in timed out");
            }

            using (client)
            {
                var request = await ReadRequestAsync(client, deadline.Token);
                var target = RequestTarget(request);
                var query = QueryOf(target);

                if (!target.StartsWith(_path, StringComparison.OrdinalIgnoreCase))
                {
                    await RespondAsync(client, "Not found", "Nothing here.", deadline.Token);
                    continue;
                }

                var result = new CallbackResult(
                    query.GetValueOrDefault("code"),
                    query.GetValueOrDefault("state"),
                    query.GetValueOrDefault("error"));

                var ok = result.Error == null && result.Code != null;
                await RespondAsync(
                    client,
                    ok ? "Signed in" : "Sign-in failed",
                    ok
                        ? "You can close this tab and return to usagetop."
                        : $"The provider said: {WebUtility.HtmlEncode(result.Error ?? "no code returned")}",
                    deadline.Token);

                return result;
            }
        }
    }

    private static async Task<string> ReadRequestAsync(TcpClient client, CancellationToken cancellation)
    {
        var stream = client.GetStream();
        var buffer = new byte[8192];
        var read = 0;

        // The request line is all that matters, and it arrives in the first packet.
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellation);
            if (count == 0) break;
            read += count;
            if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n", StringComparison.Ordinal)) break;
        }

        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    private static string RequestTarget(string request)
    {
        var line = request.Split('\n')[0].Trim();
        var parts = line.Split(' ');
        return parts.Length >= 2 ? parts[1] : "/";
    }

    private static Dictionary<string, string> QueryOf(string target)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var mark = target.IndexOf('?');
        if (mark < 0) return values;

        foreach (var pair in target[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals < 0) continue;
            var name = Uri.UnescapeDataString(pair[..equals]);
            var value = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            values[name] = value;
        }
        return values;
    }

    private static async Task RespondAsync(
        TcpClient client,
        string title,
        string message,
        CancellationToken cancellation)
    {
        var html = $"""
            <!doctype html>
            <html><head><meta charset="utf-8"><title>{WebUtility.HtmlEncode(title)}</title></head>
            <body style="margin:0;height:100vh;display:flex;align-items:center;justify-content:center;
                         background:#0A0A0A;color:#F2F2F2;font:16px/1.5 'Segoe UI',sans-serif">
              <div style="text-align:center;padding:32px">
                <h1 style="font-size:22px;font-weight:600;margin:0 0 8px">{WebUtility.HtmlEncode(title)}</h1>
                <p style="margin:0;color:#9B9B9B">{message}</p>
              </div>
            </body></html>
            """;

        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");

        try
        {
            var stream = client.GetStream();
            await stream.WriteAsync(header, cancellation);
            await stream.WriteAsync(body, cancellation);
            await stream.FlushAsync(cancellation);
        }
        catch
        {
            // The browser may have closed the tab already; the result still stands.
        }
    }

    public void Dispose()
    {
        _accepted.Writer.TryComplete();
        foreach (var listener in _listeners) listener.Stop();
    }
}
