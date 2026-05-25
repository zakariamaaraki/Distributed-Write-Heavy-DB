namespace LsmWriteDb.TcpSql;

public sealed class TcpSqlOptions
{
    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 6543;

    public int MaxQueryBytes { get; set; } = 64 * 1024;
}
