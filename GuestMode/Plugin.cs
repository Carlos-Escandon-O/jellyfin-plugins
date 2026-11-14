using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.GuestMode;

/// <summary>
/// Modo huésped: cuenta desechable que se reinicia (borrada y recreada) sin tocar
/// el historial de los demás, y con historial congelado en vivo: cada guardado de
/// UserData del invitado (vistos, progreso, favoritos, valoraciones) se deshace.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<Plugin> _logger;
    private readonly ConcurrentQueue<(Guid UserId, Guid ItemId)> _pendingFreezes = new();
    private readonly Timer _freezeTimer;
    private readonly object _sync = new();
    private (Guid Id, DateTime At)? _guestCache;

    public Plugin(
        IServerApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _logger = logger;
        _freezeTimer = new Timer(ProcessPendingFreezes, null, Timeout.Infinite, Timeout.Infinite);
        userDataManager.UserDataSaved += OnUserDataSaved;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Guest Mode";

    public override string Description => "Modo huésped: cuenta desechable que se reinicia sola (2 veces al día o a mano) con el Inicio copiado del usuario que elijas, y con el historial congelado en vivo: nada de lo que el invitado vea queda guardado.";

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            var cfg = Configuration;
            if (!cfg.FreezeHistory)
            {
                return;
            }

            if (e.Item is null)
            {
                return;
            }

            var guestId = ResolveGuestId();
            if (guestId is null || guestId.Value != e.UserId)
            {
                return;
            }

            // Los propios reinicios del congelador llegan aquí también; el chequeo
            // de "dirty" en FreezeOne evita cualquier bucle.
            _pendingFreezes.Enqueue((e.UserId, e.Item.Id));
            _freezeTimer.Change(2000, Timeout.Infinite); // debounce: procesar 2 s después
        }
        catch
        {
            // Nunca propagar excepciones al pipeline de guardado del servidor.
        }
    }

    private Guid? ResolveGuestId()
    {
        var name = Configuration.GuestUserName;
        lock (_sync)
        {
            if (_guestCache is { } cached && (DateTime.UtcNow - cached.At).TotalMinutes < 2)
            {
                return cached.Id;
            }
        }

        var user = _userManager.GetUserByName(name);
        (Guid Id, DateTime At)? fresh = user is null ? null : (user.Id, DateTime.UtcNow);
        lock (_sync)
        {
            _guestCache = fresh;
        }

        return fresh?.Id;
    }

    private void ProcessPendingFreezes(object? state)
    {
        while (_pendingFreezes.TryDequeue(out var job))
        {
            try
            {
                FreezeOne(job.UserId, job.ItemId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[GuestMode] fallo congelando {ItemId}", job.ItemId);
            }
        }

        if (!_pendingFreezes.IsEmpty)
        {
            _freezeTimer.Change(1000, Timeout.Infinite);
        }
    }

    private void FreezeOne(Guid userId, Guid itemId)
    {
        var user = _userManager.GetUserById(userId);
        var item = _libraryManager.GetItemById(itemId);
        if (user is null || item is null)
        {
            return;
        }

        var ud = _userDataManager.GetUserData(user, item);
        if (ud is null)
        {
            return;
        }

        var dirty = ud.Played
                    || ud.PlayCount != 0
                    || ud.LastPlayedDate is not null
                    || ud.PlaybackPositionTicks != 0
                    || ud.IsFavorite
                    || ud.Rating is not null
                    || ud.Likes is not null;

        if (!dirty)
        {
            return; // Ya está limpio (p.ej. el evento de nuestro propio reinicio).
        }

        ud.Played = false;
        ud.PlayCount = 0;
        ud.LastPlayedDate = null;
        ud.PlaybackPositionTicks = 0;
        ud.IsFavorite = false;
        ud.Rating = null;
        ud.Likes = null;

        _userDataManager.SaveUserData(user, item, ud, UserDataSaveReason.UpdateUserData, CancellationToken.None);
        _logger.LogDebug("[GuestMode] congelado: '{Item}' vuelve a estar limpio para el invitado.", item.Name);
    }
}
