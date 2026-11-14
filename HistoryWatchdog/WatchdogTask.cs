using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HistoryWatchdog;

/// <summary>
/// Tarea programada: revisa el buffer de marcas y revierte las ráfagas detectadas.
/// Programada 2 veces al día + una pasada al arrancar el servidor.
/// </summary>
public class WatchdogTask : IScheduledTask
{
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationPaths _appPaths;
    private readonly ILogger<WatchdogTask> _logger;

    public WatchdogTask(
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IServerApplicationPaths appPaths,
        ILogger<WatchdogTask> logger)
    {
        _userDataManager = userDataManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _appPaths = appPaths;
        _logger = logger;
    }

    public string Name => "Perro guardián del historial";

    public string Key => "HistoryWatchdog";

    public string Description => "Revisa las marcas 'visto' acumuladas desde la última ejecución y revierte las ráfagas detectadas (muchos items marcados en pocos segundos), con auditoría en CSV.";

    public string Category => "History Watchdog";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(12).Ticks },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger }
    };

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            Run(cancellationToken);
        }
        finally
        {
            progress.Report(100);
        }

        return Task.CompletedTask;
    }

    private void Run(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            _logger.LogWarning("[Watchdog] plugin no inicializado");
            return;
        }

        var cfg = plugin.Configuration;
        if (!cfg.Enabled)
        {
            _logger.LogInformation("[Watchdog] desactivado en la configuración (Enabled=false)");
            return;
        }

        var (events, reverted) = plugin.Snapshot();
        var pending = events.Where(ev => !reverted.Contains(ev.Id)).ToList();
        _logger.LogInformation("[Watchdog] buffer: {Count} marcas ({Pending} pendientes de análisis)", events.Count, pending.Count);

        if (pending.Count == 0)
        {
            plugin.Prune(DateTime.UtcNow.AddHours(-24));
            return;
        }

        var clusters = DetectClusters(pending, cfg.Threshold, cfg.WindowSeconds);
        var affected = clusters.SelectMany(c => c).ToList();

        if (affected.Count == 0)
        {
            _logger.LogInformation(
                "[Watchdog] sin ráfagas (umbral {Threshold} en {Window}s). Cadena más larga de un usuario: {Max}",
                cfg.Threshold,
                cfg.WindowSeconds,
                LongestChain(pending, cfg.WindowSeconds));
            plugin.Prune(DateTime.UtcNow.AddHours(-24));
            return;
        }

        var nUsers = affected.Select(ev => ev.UserId).Distinct().Count();
        _logger.LogWarning(
            "[Watchdog] RÁFAGA detectada: {Count} marcas de {Users} usuario(s) (umbral {Threshold} en {Window}s). Modo: {Mode}",
            affected.Count,
            nUsers,
            cfg.Threshold,
            cfg.WindowSeconds,
            cfg.DryRun ? "SIMULACIÓN (DryRun=true, no se revierte)" : "REVERSIÓN REAL");

        var csvRows = new List<string> { "fecha_utc,usuario,item_id,serie,titulo,tipo,razon,accion,playcount_antes,playcount_despues" };

        using (plugin.BeginRevert())
        {
            foreach (var ev in affected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var (accion, antes, despues) = RevertOne(ev, cfg, cancellationToken);
                    csvRows.Add(BuildRow(ev, accion, antes, despues));
                    plugin.MarkReverted(new[] { ev.Id });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Watchdog] fallo al procesar '{Item}'", ev.ItemName);
                    csvRows.Add(BuildRow(ev, "error: " + ex.GetType().Name, -1, -1));
                }
            }
        }

        var csvPath = WriteCsv(csvRows, cfg.DryRun);
        _logger.LogInformation("[Watchdog] fin: {Count} items procesados. Auditoría: {Csv}", affected.Count, csvPath);
        plugin.Prune(DateTime.UtcNow.AddHours(-24));
    }

    private (string Accion, int Antes, int Despues) RevertOne(MarkEvent ev, PluginConfiguration cfg, CancellationToken cancellationToken)
    {
        var user = _userManager.GetUserById(ev.UserId);
        if (user is null)
        {
            return ("skip: usuario inexistente", -1, -1);
        }

        var item = _libraryManager.GetItemById(ev.ItemId);
        if (item is null)
        {
            return ("skip: item inexistente", -1, -1);
        }

        var ud = _userDataManager.GetUserData(user, item);
        if (ud is null)
        {
            return ("skip: sin datos de usuario", -1, -1);
        }

        if (!ud.Played && ud.PlayCount <= 1)
        {
            return ("skip: ya sin marca", ud.PlayCount, ud.PlayCount);
        }

        var antes = ud.PlayCount;
        string accion;
        if (ud.PlayCount <= 1)
        {
            // Estaba sin ver y quedó marcado una vez: deshacer por completo.
            ud.Played = false;
            ud.PlayCount = 0;
            ud.LastPlayedDate = null;
            if (cfg.ResetProgress)
            {
                ud.PlaybackPositionTicks = 0;
            }

            accion = cfg.DryRun ? "reset (dry)" : "reset";
        }
        else
        {
            // Ya tenía visionados previos: quitar solo esta última.
            ud.PlayCount -= 1;
            ud.Played = ud.PlayCount > 0;
            accion = cfg.DryRun ? "decrement (dry)" : "decrement";
        }

        if (!cfg.DryRun)
        {
            _userDataManager.SaveUserData(user, item, ud, UserDataSaveReason.UpdateUserData, cancellationToken);
        }

        _logger.LogInformation(
            "[Watchdog] {Accion}: '{Name}' ({Series}) playcount {Before} -> {After}",
            accion,
            ev.ItemName,
            ev.SeriesName ?? "-",
            antes,
            ud.PlayCount);

        return (accion, antes, ud.PlayCount);
    }

    /// <summary>Encuentra cadenas de marcas: >= threshold marcas con huecos <= windowSeconds entre consecutivas.</summary>
    private static List<List<MarkEvent>> DetectClusters(List<MarkEvent> events, int threshold, int windowSeconds)
    {
        var result = new List<List<MarkEvent>>();
        foreach (var group in events.GroupBy(ev => ev.UserId))
        {
            var list = group.OrderBy(ev => ev.Utc).ToList();
            var i = 0;
            while (i < list.Count)
            {
                var j = i;
                while (j + 1 < list.Count && (list[j + 1].Utc - list[j].Utc).TotalSeconds <= windowSeconds)
                {
                    j++;
                }

                var count = j - i + 1;
                if (count >= threshold)
                {
                    result.Add(list.GetRange(i, count));
                }

                i = j + 1;
            }
        }

        return result;
    }

    private static int LongestChain(List<MarkEvent> events, int windowSeconds)
    {
        var best = 0;
        foreach (var group in events.GroupBy(ev => ev.UserId))
        {
            var list = group.OrderBy(ev => ev.Utc).ToList();
            var chain = list.Count > 0 ? 1 : 0;
            var localBest = chain;
            for (var i = 1; i < list.Count; i++)
            {
                if ((list[i].Utc - list[i - 1].Utc).TotalSeconds <= windowSeconds)
                {
                    chain++;
                }
                else
                {
                    chain = 1;
                }

                if (chain > localBest)
                {
                    localBest = chain;
                }
            }

            if (localBest > best)
            {
                best = localBest;
            }
        }

        return best;
    }

    private string BuildRow(MarkEvent ev, string accion, int antes, int despues)
    {
        var usuario = _userManager.GetUserById(ev.UserId)?.Username ?? ev.UserId.ToString();
        var razon = ev.Reason switch
        {
            3 => "PlaybackFinished",
            4 => "TogglePlayed",
            7 => "UpdateUserData",
            _ => ev.Reason.ToString()
        };

        return string.Join(
            ",",
            Csv(ev.Utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            Csv(usuario),
            Csv(ev.ItemId.ToString()),
            Csv(ev.SeriesName ?? string.Empty),
            Csv(ev.ItemName ?? string.Empty),
            Csv(ev.ItemType ?? string.Empty),
            Csv(razon),
            Csv(accion),
            antes.ToString(CultureInfo.InvariantCulture),
            despues.ToString(CultureInfo.InvariantCulture));
    }

    private static string Csv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }

    private string WriteCsv(List<string> rows, bool dry)
    {
        var dir = Path.Combine(_appPaths.DataPath, "backups", "history-watchdog");
        Directory.CreateDirectory(dir);
        var prefix = dry ? "simulacion-" : "revert-";
        var path = Path.Combine(dir, prefix + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv");
        File.WriteAllLines(path, rows, new UTF8Encoding(true));
        return path;
    }
}
