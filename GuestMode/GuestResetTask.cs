using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.GuestMode;

/// <summary>
/// Tarea programada: borra y recrea la cuenta de invitado (historial limpio).
/// Se ejecuta 2 veces al día si el modo huésped está activado, y también a mano
/// desde Panel → Programado → "Modo huésped: reiniciar cuenta de invitado".
/// </summary>
public class GuestResetTask : IScheduledTask
{
    private readonly IUserManager _userManager;
    private readonly ILogger<GuestResetTask> _logger;

    public GuestResetTask(IUserManager userManager, ILogger<GuestResetTask> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public string Name => "Modo huésped: reiniciar cuenta de invitado";

    public string Key => "GuestReset";

    public string Description => "Borra y recrea la cuenta configurada como invitado, dejando su historial completamente limpio. Los demás usuarios no se tocan. Solo actúa si GuestModeEnabled=true en la configuración del plugin Guest Mode.";

    public string Category => "Guest Mode";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(12).Ticks }
    };

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.GuestModeEnabled)
        {
            _logger.LogInformation(
                "[GuestMode] desactivado (GuestModeEnabled=false); no se toca nada. Para activarlo editar /var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.GuestMode.xml");
            progress.Report(100);
            return;
        }

        var name = string.IsNullOrWhiteSpace(cfg.GuestUserName) ? "Invitado" : cfg.GuestUserName.Trim();

        var existing = _userManager.GetUserByName(name);
        if (existing is not null)
        {
            _logger.LogInformation("[GuestMode] eliminando cuenta '{Name}' ({Id}) con todo su historial…", name, existing.Id);
            await _userManager.DeleteUserAsync(existing.Id).ConfigureAwait(false);
        }
        else
        {
            _logger.LogInformation("[GuestMode] la cuenta '{Name}' no existía; se creará nueva.", name);
        }

        var fresh = await _userManager.CreateUserAsync(name).ConfigureAwait(false);
        _logger.LogInformation("[GuestMode] cuenta '{Name}' lista ({Id}) — perfil limpio, sin marcas ni favoritos.", name, fresh.Id);

        progress.Report(100);
    }
}
