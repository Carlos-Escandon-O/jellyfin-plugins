using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.HistoryWatchdog;

/// <summary>Configuración del perro guardián (XML en plugins/configurations).</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Activa/desactiva la vigilancia.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Marcas consecutivas (por usuario, dentro de la ventana) para considerar ráfaga.</summary>
    public int Threshold { get; set; } = 25;

    /// <summary>Segundos máximos entre marcas consecutivas para seguirlas contando como ráfaga.</summary>
    public int WindowSeconds { get; set; } = 180;

    /// <summary>Si es true, detecta y escribe el CSV pero NO revierte nada.</summary>
    public bool DryRun { get; set; }

    /// <summary>Al revertir, también borra la posición/progreso guardado.</summary>
    public bool ResetProgress { get; set; } = true;
}
