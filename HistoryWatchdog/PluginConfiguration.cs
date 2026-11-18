using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.HistoryWatchdog;

/// <summary>
/// Configuración del perro guardián (XML en plugins/configurations).
/// Ahora: umbral 5; ninguna ráfaga se deshace sola; decides tú desde el Panel.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Activa/desactiva la vigilancia.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Marcas DISTINTAS (por usuario, dentro de la ventana) que disparan el aviso de ráfaga.</summary>
    public int Threshold { get; set; } = 5;

    /// <summary>Segundos máximos entre marcas consecutivas para seguir contándolas como ráfaga.</summary>
    public int WindowSeconds { get; set; } = 180;

    /// <summary>Si es true, la opción "deshacer la ráfaga" solo audita (no revierte). El aviso y "sí, estoy seguro" no se ven afectados.</summary>
    public bool DryRun { get; set; }

    /// <summary>Al deshacer una ráfaga, también borra la posición/progreso guardado.</summary>
    public bool ResetProgress { get; set; } = true;

    /// <summary>Ráfagas pendientes de confirmación. Las gestiona la página del plugin en el Panel; no se tocan hasta que decidas.</summary>
    public List<PendingBurst> PendingBursts { get; set; } = new();
}
