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
/// Tarea programada: aplica las decisiones que tomaste en Panel → Plugins → History Watchdog.
/// NUNCA revierte nada por su cuenta: "sí, estoy seguro" deja todo como está (sin importar
/// cuántos episodios); "revertir" deshace esa ráfaga concreta.
/// También disponible a mano como botón ▶ en Panel → Programado.
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

    public string Description => "Aplica tus decisiones sobre los avisos de ráfaga (dejar marcado o deshacer). No deshace nada por su cuenta: todo queda como tú decidas.";

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
            _logger.LogWarning("[Watchdog] el plugin no está inicializado");
            return;
        }

        var cfg = plugin.Configuration;
        if (!cfg.Enabled)
        {
            _logger.LogInformation("[Watchdog] desactivado en la configuración (Enabled=false)");
            return;
        }

        cfg.PendingBursts ??= new List<PendingBurst>();
        var toApply = cfg.PendingBursts.Where(b => !string.IsNullOrEmpty(b.Decision)).ToList();

        _logger.LogInformation(
            "[Watchdog] pendientes de decisión: {Total} (a aplicar: {ToApply})",
            cfg.PendingBursts.Count(b => b.Decision == string.Empty),
            toApply.Count);

        if (toApply.Count == 0)
        {
            LogPending();
            plugin.Prune(DateTime.UtcNow.AddHours(-24));
            return;
        }

        var csvRows = new List<string> { "fecha_utc,usuario,item_id,serie,titulo,tipo,razon,accion,playcount_antes,playcount_despues" };

        using (plugin.BeginRevert())
        {
            foreach (var burst in toApply)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (burst.Decision == "keep")
                    {
                        _logger.LogInformation(
                            "[Watchdog] confirmado por {User}: se dejan {Count} marcas ({Summary}). No se toca nada.",
                            burst.UserName, burst.Items.Count, burst.SeriesSummary);
                        foreach (var ev in burst.Items)
                        {
                            csvRows.Add(BuildRow(ev, burst.UserName, "confirmado (se queda marcado)", -1, -1));
                        }
                    }
                    else if (burst.Decision == "revert")
                    {
                        var n = 0;
                        foreach (var ev in burst.Items)
                        {
                            var (accion, antes, despues) = RevertOne(ev, cfg, cancellationToken);
                            csvRows.Add(BuildRow(ev, burst.UserName, accion, antes, despues));
                            if (!accion.StartsWith("skip", StringComparison.OrdinalIgnoreCase) && !accion.Contains("(dry)"))
                            {
                                n++;
                            }

                            plugin.MarkReverted(new[] { ev.Id });
                        }

                        _logger.LogInformation(
                            "[Watchdog] revertida la ráfaga de {User}: {Count} marcas ({Summary}).",
                            burst.UserName, n, burst.SeriesSummary);
                    }
                    else
                    {
                        _logger.LogWarning("[Watchdog] decisión desconocida '{Decision}' en la ráfaga {Id}; se deja como está.", burst.Decision, burst.Id);
                        continue;
                    }

                    cfg.PendingBursts.Remove(burst);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Watchdog] fallo procesando la ráfaga {Id}", burst.Id);
                    csvRows.Add($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss},{Csv(burst.UserName ?? string.Empty)},,,,,,error:{ex.GetType().Name},-1,-1");
                }
            }
        }

        var path = WriteCsv(csvRows, "decision");
        _logger.LogInformation("[Watchdog] fin del procesamiento. Auditoría: {Csv}", path);
        plugin.SaveConfiguration();
        LogPending();
        plugin.Prune(DateTime.UtcNow.AddHours(-24));
    }

    private void LogPending()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        var cfg = plugin.Configuration;
        cfg.PendingBursts ??= new List<PendingBurst>();
        var open = cfg.PendingBursts.Count(b => b.Decision == string.Empty);
        if (open > 0)
        {
            _logger.LogWarning(
                "[Watchdog] hay {Count} ráfaga(s) sin confirmar. Abre Panel → Plugins → History Watchdog para decidir.",
                open);
        }
        else
        {
            _logger.LogInformation("[Watchdog] sin ráfagas pendientes de decisión.");
        }
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

    private string BuildRow(MarkEvent ev, string? userName, string accion, int antes, int despues)
    {
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
            Csv(userName ?? ev.UserId.ToString()),
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

    private string WriteCsv(List<string> rows, string tag)
    {
        var dir = Path.Combine(_appPaths.DataPath, "backups", "history-watchdog");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{tag}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.csv");
        File.WriteAllLines(path, rows, new UTF8Encoding(true));
        return path;
    }
}
