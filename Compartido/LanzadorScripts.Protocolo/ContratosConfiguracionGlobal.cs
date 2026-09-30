// (Autor: Alex Roman)
// Descripcion: Define la configuracion compartida y valida sus limites antes de aplicarla.

namespace LanzadorScripts.Protocolo;

public sealed record ConfiguracionGlobalServidorCentral(
    long Revision, string RutaScripts, int MaximoEjecucionesParalelas)
{
    public static ConfiguracionGlobalServidorCentral Predeterminada => new(
        1, @"\\MAD002MICROPRU.mad.ae.aena.es\R$\SCRIPS", 5);

    public void Validar()
    {
        if (Revision < 1 || MaximoEjecucionesParalelas is < 1 or > 20
            || string.IsNullOrWhiteSpace(RutaScripts) || RutaScripts.Length > 1024
            || !RutaScripts.StartsWith(@"\\", StringComparison.Ordinal)
            || RutaScripts.StartsWith(@"\\?\", StringComparison.Ordinal)
            || RutaScripts.Any(c => char.IsControl(c) || "/:*?\"<>|".Contains(c)))
        {
            throw new InvalidDataException("Indique una ruta UNC de scripts y entre 1 y 20 ejecuciones paralelas.");
        }

        var segmentos = RutaScripts[2..].TrimEnd('\\').Split('\\');
        if (segmentos.Length < 2 || segmentos.Any(s => s.Length == 0 || s is "." or ".."
                || s.EndsWith('.') || s.EndsWith(' ')))
        {
            throw new InvalidDataException("La ruta UNC no puede contener segmentos vacios, relativos o ambiguos.");
        }
    }
}

public sealed record GuardarConfiguracionGlobalServidorCentral(
    long RevisionEsperada, string RutaScripts, int MaximoEjecucionesParalelas);
