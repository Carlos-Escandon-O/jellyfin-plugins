using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.HistoryWatchdog;

/// <summary>
/// Perro guardián del historial: escucha cada guardado de UserData, acumula las marcas
/// "visto" en un log persistente (state.json) y la tarea programada revierte las ráfagas.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    private readonly IServerApplicationPaths _appPaths;
    private readonly object _lock = new();
    private WatchdogState _state = new();
    private DateTime _lastFlush = DateTime.MinValue;
    private int _reverting;

    public Plugin(IServerApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, IUserDataManager userDataManager)
        : base(applicationPaths, xmlSerializer)
    {
        _appPaths = applicationPaths;
        Instance = this;
        LoadState();
        userDataManager.UserDataSaved += OnUserDataSaved;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "History Watchdog";

    public override string Description => "Perro guardián del historial: detecta ráfagas de marcas 'visto' (muchos items en pocos segundos, p.ej. al marcar una serie entera por error) y las revierte automáticamente con auditoría CSV.";

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

            lock (_lock)
            {
                _state.Events.Add(ev);
                if ((DateTime.UtcNow - _lastFlush).TotalSeconds >= 2)
                {
                    FlushStateLocked();
                }
            }
        }
        catch
        {
            // Nunca propagar excepciones al pipeline de guardado del servidor.
        }
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
}
