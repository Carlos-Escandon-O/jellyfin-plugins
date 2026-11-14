using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.GuestMode;

/// <summary>
/// Modo huésped: mantiene una cuenta desechable que se reinicia (borrada y recreada)
/// sin tocar el historial de los demás usuarios.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    public Plugin(IServerApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Guest Mode";

    public override string Description => "Modo huésped: cuenta desechable que se reinicia sola (borrada y recreada, 2 veces al día o a mano desde Programado) para que el visitante vea lo que quiera sin ensuciar el historial de nadie.";
}
