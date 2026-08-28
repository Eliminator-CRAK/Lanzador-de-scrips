// (Autor: Alex Roman)
// Descripcion: Identifica la distribucion instalada o portable y valida su raiz local.

using System.IO;

namespace LanzadorScripts.Servicios;

public enum TipoDistribucion
{
    Instalada,
    Portable
}

public sealed record ContextoDistribucion(
    TipoDistribucion Tipo,
    string? RaizPortable,
    string? RaizEjecucionPortable)
{
    internal const string VariableVariante = "LANZADOR_VARIANTE";
    internal const string VariableRaizPortable = "LANZADOR_PORTABLE_ROOT";
    internal const string VariableRaizSesionesPortable = "LANZADOR_PORTABLE_SESSIONS_ROOT";
    internal const string VariableRaizEjecucionPortable = "LANZADOR_PORTABLE_EXECUTION_ROOT";
    internal const string VariableRaizSesionesEjecucionPortable = "LANZADOR_PORTABLE_EXECUTION_SESSIONS_ROOT";
    internal const string NombreVariantePortable = "portable";
    internal const string NombreEjecutableInternoPortable = "LanzadorScripts.Runtime.exe";

    public bool EsPortable => Tipo == TipoDistribucion.Portable;

    public static ContextoDistribucion ObtenerActual()
    {
        return Resolver(
            Environment.GetEnvironmentVariable(VariableVariante),
            Environment.GetEnvironmentVariable(VariableRaizPortable),
            Environment.GetEnvironmentVariable(VariableRaizSesionesPortable)
                ?? Path.Combine(Path.GetTempPath(), "LanzadorScripts", "Portable"),
            Environment.GetEnvironmentVariable(VariableRaizEjecucionPortable),
            Environment.GetEnvironmentVariable(VariableRaizSesionesEjecucionPortable));
    }

    internal static ContextoDistribucion Resolver(
        string? variante,
        string? raizPortable,
        string raizSesionesPortable,
        string? raizEjecucionPortable = null,
        string? raizSesionesEjecucionPortable = null)
    {
        var valorVariante = variante?.Trim();
        if (string.IsNullOrWhiteSpace(valorVariante)
            || valorVariante.Equals("instalada", StringComparison.OrdinalIgnoreCase)
            || valorVariante.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            return new ContextoDistribucion(TipoDistribucion.Instalada, null, null);
        }

        if (!valorVariante.Equals(NombreVariantePortable, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("La variante de LanzadorScripts no es valida.");
        }

        var raiz = ValidarRaizPortable(raizPortable, raizSesionesPortable);
        var raizEjecucion = ValidarRaizPortable(
            raizEjecucionPortable ?? raizPortable,
            raizSesionesEjecucionPortable ?? raizSesionesPortable);
        return new ContextoDistribucion(
            TipoDistribucion.Portable,
            raiz,
            raizEjecucion);
    }

    internal static string ValidarRaizPortable(string? raizPortable, string raizSesionesPortable)
    {
        if (string.IsNullOrWhiteSpace(raizPortable)
            || string.IsNullOrWhiteSpace(raizSesionesPortable)
            || raizPortable.Contains('/')
            || raizSesionesPortable.Contains('/')
            || !Path.IsPathFullyQualified(raizPortable)
            || !Path.IsPathFullyQualified(raizSesionesPortable)
            || ServicioRutasSeguras.ContieneSegmentosNavegacion(raizSesionesPortable)
            || ServicioRutasSeguras.ContieneSegmentosNavegacion(raizPortable))
        {
            throw new InvalidOperationException("La raiz de la sesion portable no es valida.");
        }

        var raizSesiones = Path.GetFullPath(raizSesionesPortable);
        var raiz = Path.GetFullPath(raizPortable);
        var nombre = Path.GetFileName(raiz);
        if (!ServicioRutasSeguras.EstaDentroDeCarpeta(raizSesiones, raiz)
            || !nombre.StartsWith("Sesion-", StringComparison.Ordinal)
            || !Guid.TryParseExact(nombre["Sesion-".Length..], "N", out _))
        {
            throw new InvalidOperationException("La raiz portable queda fuera de su contenedor autorizado.");
        }

        return raiz;
    }

    internal void ValidarEjecutablePortable(string? rutaProceso)
    {
        // Impide activar el modo portable sobre el ejecutable instalado mediante variables manipuladas.
        if (!EsPortable)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(rutaProceso)
            || rutaProceso.Contains('/')
            || !Path.IsPathFullyQualified(rutaProceso)
            || ServicioRutasSeguras.ContieneSegmentosNavegacion(rutaProceso))
        {
            throw new InvalidOperationException("El ejecutable de la sesion portable no es valido.");
        }

        var ejecutableEsperado = Path.GetFullPath(Path.Combine(
            RaizEjecucionPortable!,
            "Aplicacion",
            NombreEjecutableInternoPortable));
        var ejecutableActual = Path.GetFullPath(rutaProceso);
        if (!string.Equals(
                ejecutableActual,
                ejecutableEsperado,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "El modo portable solo puede iniciarse desde su lanzador firmado.");
        }
    }
}
