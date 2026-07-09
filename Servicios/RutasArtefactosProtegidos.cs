// (Autor: Alex Roman)
// Descripcion: Resuelve las rutas fijas de permisos y catalogo dentro de una carpeta.

using System.IO;

namespace LanzadorScripts.Servicios;

public static class RutasArtefactosProtegidos
{
    public const string NombrePermisos = "permisos.json";
    public const string NombreCatalogo = "catalogo-scripts.json";
    public const string CarpetaPredeterminada = @"\\MAD002MICROPRU.mad.ae.aena.es\R$\PERMISOS";

    public static RutasArtefactos Resolver(string rutaCarpetaPermisos)
    {
        if (string.IsNullOrWhiteSpace(rutaCarpetaPermisos))
        {
            throw new InvalidOperationException("La ruta de la carpeta de permisos no esta configurada.");
        }

        var expandida = Environment.ExpandEnvironmentVariables(rutaCarpetaPermisos.Trim());
        if (EsRutaDeArchivo(expandida))
        {
            throw new InvalidOperationException("La ruta de permisos debe indicar una carpeta, no un archivo JSON.");
        }

        if (!Path.IsPathFullyQualified(expandida))
        {
            throw new InvalidOperationException("La ruta de la carpeta de permisos debe ser absoluta.");
        }

        var carpeta = NormalizarRutaCompleta(expandida);
        return new RutasArtefactos(
            carpeta,
            Path.Combine(carpeta, NombrePermisos),
            Path.Combine(carpeta, NombreCatalogo));
    }

    public static RutasArtefactos DesdeRutaPermisos(string rutaPermisos)
    {
        var rutaCompleta = Path.GetFullPath(rutaPermisos);
        if (!string.Equals(Path.GetFileName(rutaCompleta), NombrePermisos, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"El archivo de permisos debe llamarse {NombrePermisos}.");
        }

        var carpeta = Path.GetDirectoryName(rutaCompleta)
            ?? throw new InvalidOperationException("No se pudo resolver la carpeta de permisos.");
        return Resolver(carpeta);
    }

    public static string NormalizarCarpetaConfigurada(string? ruta, string rutaPredeterminada)
    {
        var valor = ruta?.Trim();
        if (string.IsNullOrWhiteSpace(valor))
        {
            return rutaPredeterminada;
        }

        if (!EsRutaDeArchivo(valor))
        {
            return valor.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        var carpeta = Path.GetDirectoryName(valor);
        return string.IsNullOrWhiteSpace(carpeta)
            ? rutaPredeterminada
            : carpeta.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool EsRutaDeArchivo(string ruta)
    {
        var nombre = Path.GetFileName(ruta.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.Equals(nombre, NombrePermisos, StringComparison.OrdinalIgnoreCase)
            || string.Equals(nombre, NombreCatalogo, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(nombre), ".json", StringComparison.OrdinalIgnoreCase);
    }

    public static bool EsCarpetaDeLaAplicacion(string ruta)
    {
        try
        {
            return string.Equals(
                NormalizarRutaCompleta(ruta),
                NormalizarRutaCompleta(AppContext.BaseDirectory),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizarRutaCompleta(string ruta)
    {
        var completa = Path.GetFullPath(ruta);
        var raiz = Path.GetPathRoot(completa);
        return string.Equals(completa, raiz, StringComparison.OrdinalIgnoreCase)
            ? completa
            : completa.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

public sealed record RutasArtefactos(
    string Carpeta,
    string RutaPermisos,
    string RutaCatalogo);
