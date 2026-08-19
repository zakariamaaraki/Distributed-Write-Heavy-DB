using System.Security.Cryptography;
using System.Text;

namespace LsmWriteDb.Raft;

public sealed record TableOwnershipRecord(
    string Table,
    long Term,
    string LeaderId,
    string LeaderUrl,
    IReadOnlyList<string> Members,
    string RebalanceId,
    DateTimeOffset UpdatedAt);

public static class TableOwnershipPlanner
{
    public static IReadOnlyList<TableOwnershipRecord> Rebalance(
        IReadOnlyList<string> tables,
        IReadOnlyList<RaftPeerOptions> nodes,
        int replicationFactor,
        IReadOnlyDictionary<string, TableOwnershipRecord>? previous = null,
        DateTimeOffset? now = null)
    {
        var allNodes = nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.NodeId) && !string.IsNullOrWhiteSpace(node.Url))
            .OrderBy(node => node.NodeId, StringComparer.Ordinal)
            .ToList();
        if (allNodes.Count == 0)
            return [];

        var count = Math.Clamp(replicationFactor, 1, allNodes.Count);
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var result = new List<TableOwnershipRecord>();
        foreach (var table in tables.Select(Storage.TableNames.Normalize).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            var offset = StableOffset(table, allNodes.Count);
            var members = Enumerable.Range(0, count)
                .Select(index => allNodes[(offset + index) % allNodes.Count])
                .ToList();
            var old = previous is not null && previous.TryGetValue(table, out var existing) ? existing : null;
            var leader = members.FirstOrDefault(member => string.Equals(member.NodeId, old?.LeaderId, StringComparison.Ordinal)) ?? members[0];
            var term = old is null ? 1 : old.Term + (members.Any(member => member.NodeId == old.LeaderId) ? 0 : 1);
            result.Add(new TableOwnershipRecord(
                table,
                term,
                leader.NodeId,
                leader.Url,
                members.Select(member => member.NodeId).ToList(),
                $"rebalance-{Guid.NewGuid():N}",
                timestamp));
        }
        return result;
    }

    private static int StableOffset(string table, int count)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(table));
        return BitConverter.ToUInt16(hash, 0) % count;
    }
}
