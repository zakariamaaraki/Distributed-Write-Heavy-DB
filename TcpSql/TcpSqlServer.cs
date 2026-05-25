using System.Net;
using System.Net.Sockets;
using LsmWriteDb.Sql;
using Microsoft.Extensions.Options;

namespace LsmWriteDb.TcpSql;

public sealed class TcpSqlServer : BackgroundService
{
    private readonly TcpSqlOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TcpSqlServer> _logger;
    private TcpListener? _listener;

    public TcpSqlServer(
        IOptions<TcpSqlOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<TcpSqlServer> logger)
    {
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("TCP SQL server is disabled.");
            return;
        }

        var address = IPAddress.Parse(_options.Host);
        _listener = new TcpListener(address, _options.Port);
        _listener.Start();
        _logger.LogInformation("TCP SQL server listening on {Host}:{Port}.", _options.Host, _options.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                _ = Task.Run(() => RunSessionAsync(client, stoppingToken), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task RunSessionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sql = scope.ServiceProvider.GetRequiredService<SqlEngine>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<TcpSqlSession>>();
            var session = new TcpSqlSession(client, sql, _options, logger);
            await session.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TCP SQL session failed.");
        }
    }
}
