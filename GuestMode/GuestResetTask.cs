using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.GuestMode;

/// <summary>
/// Tarea programada: borra y recrea la cuenta de invitado (historial limpio) y le copia
/// el Inicio del usuario indicado. Se ejecuta 2 veces al día si el modo huésped está
/// activado, y también a mano desde Panel → Programado.
/// Si el invitado está usando Jellyfin en ese momento (reproduciendo algo —pausa incluida—
/// o con actividad dentro de la ventana configurada), el reinicio se salta: nunca se corta
/// su sesión a media película.
/// </summary>
public class GuestResetTask : IScheduledTask
{
    /// <summary>Clave que usa el cliente web para sus ajustes de Inicio ('usersettings' → MD5 unicode).</summary>
    private static readonly Guid UsersettingsItemId = Md5Guid("usersettings");

    private readonly IUserManager _userManager;
    private readonly IDisplayPreferencesManager _displayPreferencesManager;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<GuestResetTask> _logger;

    public GuestResetTask(
        IUserManager userManager,
        IDisplayPreferencesManager displayPreferencesManager,
        ISessionManager sessionManager,
        ILogger<GuestResetTask> logger)
    {
        _userManager = userManager;
        _displayPreferencesManager = displayPreferencesManager;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public string Name => "Modo huésped: reiniciar cuenta de invitado";

    public string Key => "GuestReset";

    public string Description => "Borra y recrea la cuenta configurada como invitado, deja su historial completamente limpio y le copia el Inicio del usuario indicado en HomeCopyFrom. Si el invitado está activo (reproduciendo o con actividad reciente), el reinicio se aplaza. Los demás usuarios no se tocan. Solo actúa si GuestModeEnabled=true.";

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

        // Bloqueo anti-corte: si el invitado tiene una sesión activa (reproduciendo algo
        // —pausa incluida— o con actividad dentro de la ventana configurada), no se toca
        // la cuenta; el reinicio se aplaza a la siguiente pasada.
        if (existing is not null && IsGuestActive(existing.Id, cfg.ActiveWindowMinutes))
        {
            _logger.LogInformation(
                "[GuestMode] invitado '{Name}' activo ahora mismo (reproduciendo o con actividad en los últimos {Minutes} min); reinicio aplazado.",
                name,
                Math.Max(0, cfg.ActiveWindowMinutes));
            progress.Report(100);
            return;
        }

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

        if (!string.IsNullOrWhiteSpace(cfg.HomeCopyFrom))
        {
            CopyHomeFrom(cfg.HomeCopyFrom.Trim(), fresh.Id, name);
        }

        progress.Report(100);
    }

    /// <summary>
    /// True si el invitado está usando Jellyfin ahora mismo: reproduce algo
    /// (NowPlayingItem, pausa incluida) o tiene una sesión con actividad dentro
    /// de la ventana configurada (ActiveWindowMinutes; 0 = solo si reproduce).
    /// LastActivityDate es UTC, igual que DateTime.UtcNow.
    /// </summary>
    private bool IsGuestActive(Guid guestId, int windowMinutes)
    {
        var window = TimeSpan.FromMinutes(Math.Max(0, windowMinutes));
        var now = DateTime.UtcNow;

        foreach (var session in _sessionManager.Sessions.Where(s => s.UserId == guestId))
        {
            if (session.NowPlayingItem is not null)
            {
                return true;
            }

            if (session.LastActivityDate != default && now - session.LastActivityDate <= window)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Copia el Inicio (secciones, orden y ajustes de pantalla) del usuario indicado al invitado.</summary>
    private void CopyHomeFrom(string fromUserName, Guid guestId, string guestName)
    {
        try
        {
            var source = _userManager.GetUserByName(fromUserName);
            if (source is null)
            {
                _logger.LogWarning("[GuestMode] no existe el usuario '{From}' para copiar su Inicio; el invitado tendrá el layout por defecto.", fromUserName);
                return;
            }

            var sourcePrefs = _displayPreferencesManager.GetDisplayPreferences(source.Id, UsersettingsItemId, "emby");

            // GetDisplayPreferences crea la fila si no existe, así que se muta esa
            // entidad (mismo flujo que usa el propio controlador al guardar).
            var target = _displayPreferencesManager.GetDisplayPreferences(guestId, UsersettingsItemId, "emby");
            target.ShowSidebar = sourcePrefs.ShowSidebar;
            target.ShowBackdrop = sourcePrefs.ShowBackdrop;
            target.ScrollDirection = sourcePrefs.ScrollDirection;
            target.IndexBy = sourcePrefs.IndexBy;
            target.SkipForwardLength = sourcePrefs.SkipForwardLength;
            target.SkipBackwardLength = sourcePrefs.SkipBackwardLength;
            target.ChromecastVersion = sourcePrefs.ChromecastVersion;
            target.EnableNextVideoInfoOverlay = sourcePrefs.EnableNextVideoInfoOverlay;
            target.DashboardTheme = sourcePrefs.DashboardTheme;
            target.TvHome = sourcePrefs.TvHome;

            target.HomeSections.Clear();
            foreach (var section in sourcePrefs.HomeSections.OrderBy(s => s.Order).ToList())
            {
                target.HomeSections.Add(new HomeSection { Order = section.Order, Type = section.Type });
            }

            _displayPreferencesManager.UpdateDisplayPreferences(target);

            var customPrefs = _displayPreferencesManager.ListCustomItemDisplayPreferences(source.Id, UsersettingsItemId, "emby");
            _displayPreferencesManager.SetCustomItemDisplayPreferences(guestId, UsersettingsItemId, "emby", customPrefs);

            _logger.LogInformation(
                "[GuestMode] Inicio copiado a '{Guest}': {Count} secciones desde '{From}'.",
                guestName,
                target.HomeSections.Count,
                fromUserName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GuestMode] fallo copiando el Inicio; '{Guest}' tendrá el layout por defecto.", guestName);
        }
    }

    /// <summary>Idéntico a Jellyfin's StringExtensions.GetMD5 (MD5 sobre bytes UTF-16LE).</summary>
    private static Guid Md5Guid(string value)
    {
        using var md5 = MD5.Create();
        return new Guid(md5.ComputeHash(Encoding.Unicode.GetBytes(value)));
    }
}
