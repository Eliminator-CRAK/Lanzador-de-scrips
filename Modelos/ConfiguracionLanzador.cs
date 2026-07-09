// (Autor: Alex Roman)
// Descripcion: Configuracion persistente de la aplicacion.

using LanzadorScripts.Servicios;

namespace LanzadorScripts.Modelos;

public sealed class ConfiguracionLanzador
{
    public const int VersionActual = 2;

    public int? VersionConfiguracion { get; set; }

    public string RutaScripts { get; set; } = @"\\MAD002MICROPRU.mad.ae.aena.es\R$\SCRIPS";

    public string RutaPermisos { get; set; } = RutasArtefactosProtegidos.CarpetaPredeterminada;

    public string RutaLogs { get; set; } = RutasAplicacion.RutaLogsUsuario;

    public int MaximoEjecucionesParalelas { get; set; } = 5;

    public void Normalizar(ConfiguracionLanzador? valoresDefecto = null)
    {
        valoresDefecto ??= new ConfiguracionLanzador();

        if (string.IsNullOrWhiteSpace(RutaScripts))
        {
            RutaScripts = valoresDefecto.RutaScripts;
        }

        RutaPermisos = RutasArtefactosProtegidos.NormalizarCarpetaConfigurada(
            RutaPermisos,
            valoresDefecto.RutaPermisos);
        if (RutasArtefactosProtegidos.EsCarpetaDeLaAplicacion(RutaPermisos))
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
