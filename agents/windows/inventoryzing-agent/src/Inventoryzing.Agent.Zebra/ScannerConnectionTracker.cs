using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Zebra;

public enum ScannerConnectionState
{
    Connected,
    Disconnected,
}

public sealed record ScannerConnection(ScanSource Source, ScannerConnectionState State);

public sealed class ScannerConnectionTracker
{
    private readonly Dictionary<string, ScannerConnection> connections = new(StringComparer.Ordinal);

    public IReadOnlyCollection<ScannerConnection> Connections => [.. connections.Values];

    public ScannerConnection Connect(ScanSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.StableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.RuntimeId);

        var connection = new ScannerConnection(source, ScannerConnectionState.Connected);
        connections[source.StableId] = connection;
        return connection;
    }

    public bool Disconnect(string runtimeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeId);
        var connection = connections.Values.FirstOrDefault(candidate =>
            candidate.State == ScannerConnectionState.Connected &&
            string.Equals(candidate.Source.RuntimeId, runtimeId, StringComparison.Ordinal));
        if (connection is null)
        {
            return false;
        }

        connections[connection.Source.StableId] = connection with { State = ScannerConnectionState.Disconnected };
        return true;
    }

    public bool IsCurrent(ScanSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return connections.TryGetValue(source.StableId, out var current) &&
            current.State == ScannerConnectionState.Connected &&
            string.Equals(current.Source.RuntimeId, source.RuntimeId, StringComparison.Ordinal);
    }
}