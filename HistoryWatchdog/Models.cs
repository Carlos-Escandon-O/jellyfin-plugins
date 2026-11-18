using System;
using System.Collections.Generic;

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

/// <summary>Ráfaga de marcas pendientes de confirmación del usuario (el clásico "marqué la serie entera por accidente").</summary>
public class PendingBurst
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public Guid UserId { get; set; }

    public string? UserName { get; set; }

    public DateTime Utc { get; set; } = DateTime.UtcNow;

    /// <summary>Marca más reciente de la ráfaga (ayuda a saber si sigue viva al recargar).</summary>
    public DateTime LastMarkUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Items distintos marcados (se actualiza en vivo mientras sigan llegando marcas).</summary>
    public List<MarkEvent> Items { get; set; } = new();

    public string? SeriesSummary { get; set; }

    /// <summary>"" = pendiente, "keep" = dejar marcado, "revert" = deshacer.</summary>
    public string Decision { get; set; } = string.Empty;
}

/// <summary>Estado persistente del watchdog (state.json).</summary>
public class WatchdogState
{
    public List<MarkEvent> Events { get; set; } = new();

    public HashSet<Guid> Reverted { get; set; } = new();
}
