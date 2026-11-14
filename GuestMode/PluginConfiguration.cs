using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.GuestMode;

/// <summary>Configuración del modo huésped (XML en plugins/configurations).</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Si es true, la cuenta de invitado se reinicia automáticamente 2 veces al día (y se puede ejecutar a mano desde Programado).</summary>
    public bool GuestModeEnabled { get; set; }

    /// <summary>Nombre de la cuenta que se usa como invitado.</summary>
    public string GuestUserName { get; set; } = "Invitado";
}
