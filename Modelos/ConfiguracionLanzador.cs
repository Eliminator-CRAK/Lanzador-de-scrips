// (Autor: Alex Roman)
// Descripcion: Configuracion persistente de la aplicacion.

using LanzadorScripts.Servicios;

namespace LanzadorScripts.Modelos;

public sealed class ConfiguracionLanzador
{
    public string RutaScripts { get; set; } = @"\\MAD002MICROPRU\REPO";

    public string RutaPermisos { get; set; } = @"PERMISOS\permissions.json";

    public string RutaLogs { get; set; } = RutasAplicacion.RutaLogsUsuario;

    public int MaximoEjecucionesParalelas { get; set; } = 5;

    public void Normalizar(ConfiguracionLanzador? valoresDefecto = null)
    {
        valoresDefecto ??= new ConfiguracionLanzador();

        if (string.IsNullOrWhiteSpace(RutaScripts))
        {
            RutaScripts = valoresDefecto.RutaScripts;
        }

        if (string.IsNullOrWhiteSpace(RutaPermisos))
        {
            RutaPermisos = valoresDefecto.RutaPermisos;
        }

        if (string.IsNullOrWhiteSpace(RutaLogs))
        {
            RutaLogs = RutasAplicacion.RutaLogsUsuario;
        }

        MaximoEjecucionesParalelas = Math.Clamp(MaximoEjecucionesParalelas, 1, 50);
    }
}
