using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HistoryWatchdog;

/// <summary>
/// Perro guardián del historial: escucha en vivo cada guardado de UserData y
/// acumula las marcas 'visto'. Nunca revierte nada por su cuenta: si un usuario
/// marca muchos episodios de golpe (ráfaga), avisa y espera tu confirmación
/// desde el Panel (History Watchdog) — "sí, estoy seguro" deja todo como está
/// sin importar cuántos episodios sean.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly IServerApplicationPaths _appPaths;
    private readonly IUserManager _userManager;
    private readonly ILogger<Plugin> _logger;
    private readonly object _lock = new();
    private WatchdogState _state = new();
    private DateTime _lastFlush = DateTime.MinValue;
    private DateTime _lastConfigFlush = DateTime.MinValue;
    private int _reverting;

    public Plugin(IServerApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, IUserDataManager userDataManager, IUserManager userManager, ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        _appPaths = applicationPaths;
        _userManager = userManager;
        _logger = logger;
        Instance = this;
        LoadState();
        userDataManager.UserDataSaved += OnUserDataSaved;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "History Watchdog";

    public override Guid Id => Guid.Parse("4e1c8a9f-2b7d-4c36-9e5a-7f0d3b8c6a21");

    public override string Description => "Perro guardián del historial: detecta ráfagas de marcas 'visto' (muchos episodios de golpe), avisa y pide confirmación en el Panel; opción de dejarlas marcadas o deshacerlas. Nunca revierte nada solo.";

    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = "History Watchdog",
            DisplayName = "History Watchdog",
            EmbeddedResourcePath = "Jellyfin.Plugin.HistoryWatchdog.Web.Configuration.html",
            EnableInMainMenu = false
        }
    };

    private string StateDir => Path.Combine(_appPaths.DataPath, "plugins", "HistoryWatchdog");

    private string StatePath => Path.Combine(StateDir, "state.json");

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            if (Volatile.Read(ref _reverting) == 1)
            {
                return; // Son nuestras propias reversiones: no registrar.
            }

            var cfg = Configuration;
            if (!cfg.Enabled)
            {
                return;
            }

            var ud = e.UserData;
            var item = e.Item;
            if (ud is null || item is null || !ud.Played)
            {
                return;
            }

            if (e.SaveReason is not (UserDataSaveReason.PlaybackFinished
                or UserDataSaveReason.TogglePlayed
                or UserDataSaveReason.UpdateUserData))
            {
                return;
            }

            var ev = new MarkEvent
            {
                Utc = DateTime.UtcNow,
                UserId = e.UserId,
                ItemId = item.Id,
                ItemName = item.Name,
                ItemType = item.GetType().Name,
                SeriesName = (item as Episode)?.SeriesName,
                PlayCountAfter = ud.PlayCount,
                PositionAfter = ud.PlaybackPositionTicks,
                LastPlayedAfter = ud.LastPlayedDate,
                Reason = (int)e.SaveReason
            };

            cfg.PendingBursts ??= new List<PendingBurst>();

            lock (_lock)
            {
                _state.Events.Add(ev);
                if ((DateTime.UtcNow - _lastFlush).TotalSeconds >= 2)
                {
                    FlushStateLocked();
                }

                AnalyzeBurstLocked(e.UserId, cfg);
            }
        }
        catch
        {
            // Nunca propagar excepciones al pipeline de guardado del servidor.
        }
    }

    /// <summary>Analiza si el usuario acaba de superar el umbral de ráfaga y, si es así, crea o actualiza el aviso pendiente.</summary>
    private void AnalyzeBurstLocked(Guid userId, PluginConfiguration cfg)
    {
        var window = TimeSpan.FromSeconds(Math.Max(1, cfg.WindowSeconds));
        var list = _state.Events.Where(x => x.UserId == userId).OrderBy(x => x.Utc).ToList();
        if (list.Count == 0)
        {
            return;
        }

        // Cadena consecutiva que termina en la marca más reciente.
        var chain = new List<MarkEvent> { list[^1] };
        for (var i = list.Count - 2; i >= 0; i--)
        {
            if (list[i + 1].Utc - list[i].Utc <= window)
            {
                chain.Add(list[i]);
            }
            else
            {
                break;
            }
        }

        chain.Reverse();
        var distinct = chain.GroupBy(x => x.ItemId).Select(g => g.Last()).ToList();
        var threshold = Math.Max(2, cfg.Threshold);
        if (distinct.Count < threshold)
        {
            return;
        }

        var userName = _userManager.GetUserById(userId)?.Username;
        var existing = cfg.PendingBursts.FirstOrDefault(b => b.UserId == userId && b.Decision == string.Empty);
        if (existing is null)
        {
            var burst = new PendingBurst
            {
                UserId = userId,
                UserName = userName,
                Utc = DateTime.UtcNow,
                LastMarkUtc = DateTime.UtcNow,
                Items = distinct,
                SeriesSummary = SeriesSummary(distinct)
            };
            cfg.PendingBursts.Add(burst);
            _lastConfigFlush = DateTime.UtcNow;
            SaveConfiguration();
            _logger.LogWarning(
                "[Watchdog] ALERTA: {User} marcó {Count} episodios de golpe ({Summary}). Abre Panel → Plugins → History Watchdog para decidir: 'sí, estoy seguro' (se quedan todos) o deshacerlos.",
                userName,
                burst.Items.Count,
                burst.SeriesSummary);
            WriteAlertCsv(burst);
            return;
        }

        existing.Items = distinct;
        existing.LastMarkUtc = DateTime.UtcNow;
        existing.SeriesSummary = SeriesSummary(distinct);
        if ((DateTime.UtcNow - _lastConfigFlush).TotalSeconds >= 1)
        {
            _lastConfigFlush = DateTime.UtcNow;
            SaveConfiguration();
        }
    }

    private static string SeriesSummary(IReadOnlyList<MarkEvent> items)
    {
        var series = items.Select(i => i.SeriesName).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
        if (series.Count == 1)
        {
            return series[0]!;
        }

        if (series.Count > 1)
        {
            var head = string.Join(", ", series.Take(3));
            return series.Count > 3 ? head + "…" : head;
        }

        return "episodios de varias series";
    }

    /// <summary>Copia del buffer + ids ya revertidos, para la tarea programada.</summary>
    public (List<MarkEvent> Events, HashSet<Guid> Reverted) Snapshot()
    {
        lock (_lock)
        {
            FlushStateLocked();
            return (new List<MarkEvent>(_state.Events), new HashSet<Guid>(_state.Reverted));
        }
    }

    public void MarkReverted(IEnumerable<Guid> ids)
    {
        lock (_lock)
        {
            foreach (var id in ids)
            {
                _state.Reverted.Add(id);
            }

            FlushStateLocked();
        }
    }

    /// <summary>Descarta los eventos ya revertidos y los más viejos que cutoffUtc.</summary>
    public void Prune(DateTime cutoffUtc)
    {
        lock (_lock)
        {
            _state.Events.RemoveAll(ev => ev.Utc < cutoffUtc || _state.Reverted.Contains(ev.Id));
            _state.Reverted.Clear();
            FlushStateLocked();
        }
    }

    public void FlushState()
    {
        lock (_lock)
        {
            FlushStateLocked();
        }
    }

    /// <summary>Ámbito anti-reentrancia: mientras esté activo, el handler ignora los guardados (que son nuestros).</summary>
    public RevertScope BeginRevert() => new(this);

    public sealed class RevertScope : IDisposable
    {
        private readonly Plugin _plugin;

        public RevertScope(Plugin plugin)
        {
            _plugin = plugin;
            Interlocked.Exchange(ref plugin._reverting, 1);
        }

        public void Dispose() => Interlocked.Exchange(ref _plugin._reverting, 0);
    }

    private void FlushStateLocked()
    {
        Directory.CreateDirectory(StateDir);
        var tmp = StatePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_state));
        File.Move(tmp, StatePath, true);
        _lastFlush = DateTime.UtcNow;
    }

    private void LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var loaded = JsonSerializer.Deserialize<WatchdogState>(File.ReadAllText(StatePath));
                if (loaded is not null)
                {
                    _state = loaded;
                }
            }
        }
        catch
        {
            // Estado corrupto: empezar limpio.
        }
    }

    private void WriteAlertCsv(PendingBurst burst)
    {
        try
        {
            var dir = Path.Combine(_appPaths.DataPath, "backups", "history-watchdog");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "alerta-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
            var lines = new List<string> { "fecha_utc,usuario,item_id,serie,titulo,tipo,razon,accion,playcount_antes,playcount_despues" };
            foreach (var ev in burst.Items)
            {
                var razon = ev.Reason switch
                {
                    3 => "PlaybackFinished",
                    4 => "TogglePlayed",
                    7 => "UpdateUserData",
                    _ => ev.Reason.ToString()
                };
                lines.Add(string.Join(
                    ",",
                    Csv(ev.Utc.ToString("yyyy-MM-dd HH:mm:ss")),
                    Csv(burst.UserName ?? burst.UserId.ToString()),
                    Csv(ev.ItemId.ToString()),
                    Csv(ev.SeriesName ?? string.Empty),
                    Csv(ev.ItemName ?? string.Empty),
                    Csv(ev.ItemType ?? string.Empty),
                    Csv(razon),
                    Csv("alerta (pendiente de confirmación)"),
                    (-1).ToString(),
                    (-1).ToString()));
            }

            File.WriteAllLines(path, lines, new UTF8Encoding(true));
            _logger.LogInformation("[Watchdog] audit detalle de alerta: {Csv}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Watchdog] no se pudo escribir el CSV de la alerta");
        }
    }

    private static string Csv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}
