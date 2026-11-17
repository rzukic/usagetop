using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace AiUsage.Net;

public sealed record HttpResponse(int Code, string Body);

/// <summary>One shared client: a fresh one per call would leak sockets.</summary>
public static class Http
{
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    static Http()
    {
        // Meta's edge answers a request with no User-Agent with an HTML error page
        // instead of JSON, which is what broke Muse sign-in. A per-request header
        // still wins: HttpClient only copies a default when the request lacks it.
        Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "ai-usage/1.0");
    }

    public static async Task<HttpResponse> GetAsync(
        string url,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        Apply(request, headers);
        return await SendAsync(request, cancellation);
    }

    public static Task<HttpResponse> PostJsonAsync(
        string url,
        IReadOnlyDictionary<string, string> headers,
        string body,
        CancellationToken cancellation = default) =>
        // No charset here: StringContent appends its own, and rejects a media type
        // that already carries a parameter.
        PostAsync(url, headers, body, "application/json", cancellation);

    public static async Task<HttpResponse> PutJsonAsync(
        string url,
        IReadOnlyDictionary<string, string> headers,
        string body,
        CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        Apply(request, headers);
        return await SendAsync(request, cancellation);
    }

    public static Task<HttpResponse> PostFormAsync(
        string url,
        IReadOnlyDictionary<string, string> headers,
        string body,
        CancellationToken cancellation = default) =>
        PostAsync(url, headers, body, "application/x-www-form-urlencoded", cancellation);

    public static async Task<HttpResponse> PatchJsonAsync(
        string url,
        IReadOnlyDictionary<string, string> headers,
        string body,
        CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        Apply(request, headers);
        return await SendAsync(request, cancellation);
    }

    public static async Task<HttpResponse> DeleteAsync(
        string url,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        Apply(request, headers);
        return await SendAsync(request, cancellation);
    }

    private static async Task<HttpResponse> PostAsync(
        string url,
        IReadOnlyDictionary<string, string> headers,
        string body,
        string contentType,
        CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        Apply(request, headers);
        return await SendAsync(request, cancellation);
    }

    private static void Apply(HttpRequestMessage request, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (name, value) in headers)
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static async Task<HttpResponse> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellation)
    {
        using var response = await Client.SendAsync(request, cancellation);
        var body = await response.Content.ReadAsStringAsync(cancellation);
        return new HttpResponse((int)response.StatusCode, body);
    }
}
