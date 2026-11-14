using System;

namespace Jellyfin.Plugin.HistoryWatchdog;

/// <summary>Una marca 'visto' registrada en tiempo real por el evento UserDataSaved.</summary>
public class MarkEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime Utc { get; set; }

    public Guid UserId { get; set; }

    public Guid ItemId { get; set; }

    public string? ItemName { get; set; }

    public string? ItemType { get; set; }

    public string? SeriesName { get; set; }

    public int PlayCountAfter { get; set; }

    public long PositionAfter { get; set; }

    public DateTime? LastPlayedAfter { get; set; }

    public int Reason { get; set; }
}

/// <summary>Estado persistente del watchdog (state.json).</summary>
public class WatchdogState
{
    public List<MarkEvent> Events { get; set; } = new();

    public HashSet<Guid> Reverted { get; set; } = new();
}
