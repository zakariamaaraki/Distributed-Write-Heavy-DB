using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<LeaderRouter>();
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", role = "database-router" }));
app.MapMethods("/{**path}", ["GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS"], async (
    HttpContext context,
    LeaderRouter router,
    CancellationToken cancellationToken) => await router.ForwardAsync(context, cancellationToken));

app.Run();

sealed class LeaderRouter
{
    private readonly HttpClient _client;
    private readonly string _localNode;
    private readonly IReadOnlyDictionary<string, string> _nodes;
    private readonly ConcurrentDictionary<string, string> _leaders = new(StringComparer.OrdinalIgnoreCase);

    public LeaderRouter()
    {
        _localNode = Environment.GetEnvironmentVariable("ROUTER_DATABASE_URL")?.TrimEnd('/')
            ?? throw new InvalidOperationException("ROUTER_DATABASE_URL is required.");
        _nodes = ParseNodes(Environment.GetEnvironmentVariable("ROUTER_PEERS") ?? string.Empty);
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(15),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
            EnableMultipleHttp2Connections = true
        };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<IResult> ForwardAsync(HttpContext context, CancellationToken cancellationToken)
    {
        byte[]? requestBody = null;
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            context.Request.EnableBuffering();
            using var requestBuffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(requestBuffer, cancellationToken);
            requestBody = requestBuffer.ToArray();
            context.Request.Body.Position = 0;
        }
        var table = await ResolveTableAsync(context, cancellationToken);
        var target = await ResolveLeaderAsync(table, cancellationToken);
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target + context.Request.PathBase + context.Request.Path + context.Request.QueryString);
        foreach (var header in context.Request.Headers)
        {
            if (!string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            request.Content = new ByteArrayContent(requestBody ?? Array.Empty<byte>());

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.MovedPermanently or HttpStatusCode.TemporaryRedirect)
        {
            _leaders.TryRemove(table, out _);
            var retryTarget = await ResolveLeaderAsync(table, cancellationToken);
            if (!string.Equals(retryTarget, target, StringComparison.Ordinal))
            {
                request.Dispose();
                return await ForwardAsync(context, cancellationToken);
            }
        }

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        foreach (var header in response.Headers)
            context.Response.Headers[header.Key] = header.Value.ToArray();
        foreach (var header in response.Content.Headers)
            context.Response.Headers[header.Key] = header.Value.ToArray();
        context.Response.StatusCode = (int)response.StatusCode;
        await context.Response.Body.WriteAsync(body, cancellationToken);
        return Results.Empty;
    }

    private async Task<string> ResolveLeaderAsync(string table, CancellationToken cancellationToken)
    {
        if (_leaders.TryGetValue(table, out var cached))
            return cached;
        foreach (var node in new[] { _localNode }.Concat(_nodes.Values).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var state = await _client.GetFromJsonAsync<RaftStatus>($"{node}/raft/tables/{Uri.EscapeDataString(table)}/state", cancellationToken);
                if (state?.Role is 2 or "Leader" && !string.IsNullOrWhiteSpace(state.LeaderUrl))
                {
                    _leaders[table] = state.LeaderUrl.TrimEnd('/');
                    return _leaders[table];
                }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
        return _localNode;
    }

    private async Task<string> ResolveTableAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var match = Regex.Match(path, "^/tables/([^/]+)", RegexOptions.IgnoreCase);
        if (match.Success)
            return Uri.UnescapeDataString(match.Groups[1].Value);
        if (path.StartsWith("/kv", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/transactions", StringComparison.OrdinalIgnoreCase))
            return "kv";
        if (path.Equals("/sql", StringComparison.OrdinalIgnoreCase))
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            var json = await reader.ReadToEndAsync(cancellationToken);
            context.Request.Body.Position = 0;
            var query = JsonDocument.Parse(json).RootElement.GetProperty("query").GetString() ?? string.Empty;
            var sql = Regex.Match(query, "(?:FROM|INTO|UPDATE|DELETE\\s+FROM|JOIN)\\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase);
            return sql.Success ? sql.Groups[1].Value : "kv";
        }
        return "kv";
    }

    private static IReadOnlyDictionary<string, string> ParseNodes(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].TrimEnd('/'), StringComparer.OrdinalIgnoreCase);

    private sealed record RaftStatus(string? LeaderUrl, object? Role);
}
