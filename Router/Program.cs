using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

const string MonitoringPage = """
<!doctype html><html><head><meta charset="utf-8"><title>LSM Cluster Monitoring</title><style>body{font-family:system-ui;margin:24px;background:#f5f7fb;color:#172033}.muted{color:#65728a}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:12px}.card{background:white;border:1px solid #dbe2ee;border-radius:10px;padding:14px;margin:12px 0}.ok{color:#16834b}.down{color:#c33}.leader{background:#e9f8ef}table{border-collapse:collapse;width:100%}th,td{padding:9px;border-bottom:1px solid #e5eaf2;text-align:left}th{background:#edf2fa}</style></head><body><h1>LSM Cluster Monitoring</h1><div class="muted">Router-local view · refreshes every 3 seconds · <span id="time">loading...</span></div><h2>Nodes</h2><div id="nodes" class="grid"></div><h2>Table ownership</h2><div id="tables"></div><script>async function refresh(){try{const d=await fetch('/monitoring/api/status').then(r=>r.json());document.getElementById('time').textContent=new Date(d.generatedAt).toLocaleString();document.getElementById('nodes').innerHTML=d.nodes.map(n=>`<div class="card"><b>${n.id}</b><div>${n.url}</div><p class="${n.reachable?'ok':'down'}">${n.reachable?'● reachable':'● unavailable'}</p></div>`).join('');document.getElementById('tables').innerHTML=d.tables.length?d.tables.map(t=>`<div class="card"><h3>${t.table}</h3><table><tr><th>Node</th><th>Role</th><th>Term</th><th>Leader</th></tr>${t.states.map(s=>`<tr class="${String(s.role).toLowerCase().includes('leader')?'leader':''}"><td>${s.node}</td><td>${s.role??'Unavailable'}</td><td>${s.term??'-'}</td><td>${s.leader??'-'}</td></tr>`).join('')}</table></div>`).join(''):'<p class="muted">No tables discovered.</p>'}catch(e){document.getElementById('time').textContent='monitoring unavailable: '+e}}refresh();setInterval(refresh,3000);</script></body></html>
""";


var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<LeaderRouter>();
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", role = "database-router" }));
app.MapGet("/monitoring", () => Results.Content(MonitoringPage, "text/html; charset=utf-8"));
app.MapGet("/monitoring/api/status", async (LeaderRouter router, CancellationToken cancellationToken) => Results.Ok(await router.GetMonitoringAsync(cancellationToken)));
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
    private readonly ConcurrentDictionary<Guid, string> _transactionCoordinators = new();
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
        var requestTransactionId = TryReadTransactionId(requestBody);
        var statement = TryReadSqlStatement(requestBody);
        var target = statement is "COMMIT" or "ROLLBACK"
            && requestTransactionId is Guid completionId
            && _transactionCoordinators.TryGetValue(completionId, out var coordinator)
                ? coordinator
                : await ResolveLeaderAsync(table, cancellationToken);
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target + context.Request.PathBase + context.Request.Path + context.Request.QueryString);
        foreach (var header in context.Request.Headers)
        {
            if (!string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase) && !string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase) && !string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            request.Content = new ByteArrayContent(requestBody ?? Array.Empty<byte>());
            if (context.Request.ContentType is not null) request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
        }

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
            if (!string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && !string.Equals(header.Key, "Connection", StringComparison.OrdinalIgnoreCase)) context.Response.Headers[header.Key] = header.Value.ToArray();
        foreach (var header in response.Content.Headers)
            if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) && !string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) context.Response.Headers[header.Key] = header.Value.ToArray();
        context.Response.Headers.Remove("Transfer-Encoding");
        context.Response.ContentLength = body.Length;
        context.Response.StatusCode = (int)response.StatusCode;
        await context.Response.Body.WriteAsync(body, cancellationToken);
        if (response.IsSuccessStatusCode
            && string.Equals(context.Request.Path, "/sql", StringComparison.OrdinalIgnoreCase))
        {
            await RememberTransactionAsync(body, target, cancellationToken);
        }
        return Results.Empty;
    }

    private async Task RememberTransactionAsync(byte[] body, string coordinator, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("transactionId", out var value)
                || value.ValueKind != JsonValueKind.String
                || !Guid.TryParse(value.GetString(), out var id))
                return;

            var statement = root.TryGetProperty("statementType", out var type)
                ? type.GetString()
                : null;
            if (statement is "COMMIT" or "ROLLBACK")
            {
                _transactionCoordinators.TryRemove(id, out _);
                await ForgetTransactionAsync(id, cancellationToken);
                return;
            }

            _transactionCoordinators[id] = coordinator;
            foreach (var node in new[] { _localNode }.Concat(_nodes.Values).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var response = await _client.PostAsync(
                        $"{node}/transactions/{id}/register", content: null, cancellationToken);
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            }
        }
        catch (JsonException) { }
    }

    private async Task ForgetTransactionAsync(Guid id, CancellationToken cancellationToken)
    {
        foreach (var node in new[] { _localNode }.Concat(_nodes.Values).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var response = await _client.DeleteAsync(
                    $"{node}/transactions/{id}/register", cancellationToken);
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
    }

    private static Guid? TryReadTransactionId(byte[]? body)
    {
        if (body is null) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var value = document.RootElement.GetProperty("transactionId");
            return value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) ? id : null;
        }
        catch (Exception) when (body is not null) { return null; }
    }

    private static string? TryReadSqlStatement(byte[]? body)
    {
        if (body is null) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var query = document.RootElement.GetProperty("query").GetString() ?? string.Empty;
            return Regex.Match(query.Trim(), "^(BEGIN|COMMIT|ROLLBACK)", RegexOptions.IgnoreCase).Value.ToUpperInvariant();
        }
        catch (Exception) when (body is not null) { return null; }
    }
    public async Task<object> GetMonitoringAsync(CancellationToken cancellationToken)
    {
        var nodes = new[] { (Id: Environment.GetEnvironmentVariable("ROUTER_NODE_ID") ?? "local", Url: _localNode) }.Concat(_nodes.Select(pair => (Id: pair.Key, Url: pair.Value))).ToList();
        var nodeResults = new List<object>();
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            try
            {
                using var response = await _client.GetAsync(node.Url + "/tables", cancellationToken);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                foreach (var table in document.RootElement.EnumerateArray())
                {
                    if (table.TryGetProperty("name", out var name) || table.TryGetProperty("table", out name))
                        tables.Add(name.GetString() ?? string.Empty);
                }
                nodeResults.Add(new { id = node.Id, url = node.Url, reachable = true });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { nodeResults.Add(new { id = node.Id, url = node.Url, reachable = false, error = ex.Message }); }
        }
        var tableResults = new List<object>();
        foreach (var table in tables.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var states = new List<object>();
            foreach (var node in nodes)
            {
                try { var state = await _client.GetFromJsonAsync<MonitoringTableState>(node.Url + "/raft/tables/" + Uri.EscapeDataString(table) + "/state", cancellationToken); states.Add(new { node = node.Id, role = state?.Role, term = state?.CurrentTerm, leader = state?.LeaderId, leaderUrl = state?.LeaderUrl }); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { states.Add(new { node = node.Id, role = "Unavailable", error = ex.Message }); }
            }
            tableResults.Add(new { table, states });
        }
        return new { generatedAt = DateTimeOffset.UtcNow, nodes = nodeResults, tables = tableResults };
    }
    private async Task<string> ResolveLeaderAsync(string table, CancellationToken cancellationToken)
    {
        if (_leaders.TryGetValue(table, out var cached))
            return cached;

        var nodes = new[] { _localNode }.Concat(_nodes.Values)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var tableExists = false;
        foreach (var node in nodes)
        {
            try
            {
                var tables = await _client.GetFromJsonAsync<List<TableInfo>>($"{node}/tables", cancellationToken);
                if (tables?.Any(item => string.Equals(item.Name, table, StringComparison.OrdinalIgnoreCase)) == true)
                    tableExists = true;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }

        if (!tableExists)
            return await SelectLeastLoadedNodeAsync(nodes, cancellationToken);

        foreach (var node in nodes)
        {
            try
            {
                var state = await _client.GetFromJsonAsync<RaftStatus>(
                    $"{node}/raft/tables/{Uri.EscapeDataString(table)}/state", cancellationToken);
                if (state is not null && IsLeaderRole(state.Role) && !string.IsNullOrWhiteSpace(state.LeaderUrl))
                {
                    _leaders[table] = state.LeaderUrl.TrimEnd('/');
                    return _leaders[table];
                }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }

        return await SelectLeastLoadedNodeAsync(nodes, cancellationToken);
    }

    private async Task<string> SelectLeastLoadedNodeAsync(
        IReadOnlyList<string> nodes,
        CancellationToken cancellationToken)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            try
            {
                var localTables = await _client.GetFromJsonAsync<List<TableInfo>>($"{node}/tables", cancellationToken);
                if (localTables is not null)
                    foreach (var table in localTables)
                        tables.Add(table.Name);
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }

        var leaderCounts = nodes.ToDictionary(node => node, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            foreach (var node in nodes)
            {
                try
                {
                    var state = await _client.GetFromJsonAsync<RaftStatus>(
                        $"{node}/raft/tables/{Uri.EscapeDataString(table)}/state", cancellationToken);
                    if (state is not null && IsLeaderRole(state.Role) && !string.IsNullOrWhiteSpace(state.LeaderUrl))
                    {
                        var leader = nodes.FirstOrDefault(candidate =>
                            string.Equals(candidate.TrimEnd('/'), state.LeaderUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
                        if (leader is not null)
                            leaderCounts[leader]++;
                        break;
                    }
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            }
        }

        return nodes
            .OrderBy(node => leaderCounts[node])
            .ThenBy(node => node, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? _localNode;
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
            var sql = Regex.Match(query, "(?:CREATE\\s+TABLE|FROM|INTO|UPDATE|DELETE\\s+FROM|JOIN)\\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase);
            return sql.Success ? sql.Groups[1].Value : "kv";
        }
        return "kv";
    }

    private static IReadOnlyDictionary<string, string> ParseNodes(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].TrimEnd('/'), StringComparer.OrdinalIgnoreCase);

    private static bool IsLeaderRole(JsonElement role)
    {
        return role.ValueKind == JsonValueKind.Number && role.TryGetInt32(out var numeric) && numeric == 2
            || role.ValueKind == JsonValueKind.String && string.Equals(role.GetString(), "Leader", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record TableInfo(string Name);
    private sealed record RaftStatus(string? LeaderUrl, JsonElement Role);
    private sealed record MonitoringTableState(string? LeaderUrl, string? LeaderId, object? Role, long CurrentTerm);
}
