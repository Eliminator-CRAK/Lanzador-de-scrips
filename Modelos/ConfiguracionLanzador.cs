// (Autor: Alex Roman)
// Descripcion: Configuracion persistente de la aplicacion.

using LanzadorScripts.Servicios;

namespace LanzadorScripts.Modelos;

public sealed class ConfiguracionLanzador
{
    public const int VersionActual = 3;

    public int? VersionConfiguracion { get; set; }

    public string RutaScripts { get; set; } = @"\\MAD002MICROPRU.mad.ae.aena.es\R$\SCRIPS";

    public string RutaPermisos { get; set; } = RutasArtefactosProtegidos.CarpetaPredeterminada;

    public string ServidorCentral { get; set; } = "MAD002MICROPRU.mad.ae.aena.es";

    public int PuertoServidorCentral { get; set; } = 47831;

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

        ServidorCentral = NormalizarServidorCentral(
            ServidorCentral,
            valoresDefecto.ServidorCentral);
        PuertoServidorCentral = PuertoServidorCentral is >= 1024 and <= 65535
            ? PuertoServidorCentral
            : valoresDefecto.PuertoServidorCentral;

        if (string.IsNullOrWhiteSpace(RutaLogs))
        {
            RutaLogs = RutasAplicacion.RutaLogsUsuario;
        }

        MaximoEjecucionesParalelas = Math.Clamp(MaximoEjecucionesParalelas, 1, 50);
    }

    private static string NormalizarServidorCentral(string? servidor, string predeterminado)
    {
        var valor = servidor?.Trim().TrimEnd('.') ?? string.Empty;
        if (valor.Length is <= 0 or > 253
            || valor.Contains('\\', StringComparison.Ordinal)
            || valor.Contains('/', StringComparison.Ordinal)
            || valor.Contains(':', StringComparison.Ordinal))
        {
            return predeterminado;
        }

        var segmentos = valor.Split('.');
        return segmentos.Any(segmento => segmento.Length is <= 0 or > 63
            || segmento[0] == '-'
            || segmento[^1] == '-'
            || segmento.Any(caracter => !char.IsLetterOrDigit(caracter) && caracter != '-'))
            ? predeterminado
            : valor;
    }
}
