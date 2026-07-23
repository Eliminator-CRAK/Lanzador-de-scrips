// (Autor: Alex Roman)
// Descripcion: Centraliza la validacion de rutas antes de acceder a archivos.

using System.IO;

namespace LanzadorScripts.Servicios;

public static class ServicioRutasSeguras
{
    public static string ResolverArchivoAbsoluto(
        string rutaArchivo,
        string descripcion,
        params string[] extensionesPermitidas)
    {
        if (string.IsNullOrWhiteSpace(rutaArchivo))
        {
            throw new InvalidOperationException($"La ruta de {descripcion} no puede estar vacia.");
        }

        var expandida = Environment.ExpandEnvironmentVariables(rutaArchivo.Trim());
        ValidarRutaControlada(expandida, descripcion);
        if (!Path.IsPathFullyQualified(expandida))
        {
            throw new InvalidOperationException($"La ruta de {descripcion} debe ser absoluta.");
        }

        var rutaCompleta = Path.GetFullPath(expandida);
        ValidarNombreArchivo(Path.GetFileName(rutaCompleta), descripcion);
        ValidarExtension(rutaCompleta, descripcion, extensionesPermitidas);
        return rutaCompleta;
    }

    public static string ResolverCarpetaAbsoluta(string rutaCarpeta, string descripcion)
    {
        if (string.IsNullOrWhiteSpace(rutaCarpeta))
        {
            throw new InvalidOperationException($"La ruta de {descripcion} no puede estar vacia.");
        }

        var expandida = Environment.ExpandEnvironmentVariables(rutaCarpeta.Trim());
        ValidarRutaControlada(expandida, descripcion);
        if (!Path.IsPathFullyQualified(expandida))
        {
            throw new InvalidOperationException($"La ruta de {descripcion} debe ser absoluta.");
        }

        var carpeta = Path.GetFullPath(expandida);
        var raiz = Path.GetPathRoot(carpeta);
        return string.Equals(carpeta, raiz, StringComparison.OrdinalIgnoreCase)
            ? carpeta
            : carpeta.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string ResolverArchivoEnCarpeta(
        string carpetaBase,
        string nombreArchivo,
        string descripcion,
        params string[] extensionesPermitidas)
    {
        var carpeta = ResolverCarpetaAbsoluta(carpetaBase, $"carpeta base de {descripcion}");
        ValidarNombreArchivo(nombreArchivo, descripcion);
        ValidarExtension(nombreArchivo, descripcion, extensionesPermitidas);

        var rutaCompleta = Path.GetFullPath(Path.Combine(carpeta, nombreArchivo));
        if (!EstaDentroDeCarpeta(carpeta, rutaCompleta))
        {
            throw new InvalidOperationException($"La ruta de {descripcion} queda fuera de la carpeta autorizada.");
        }

        return rutaCompleta;
    }

    public static bool EsArchivoAbsolutoValido(
        string rutaArchivo,
        string descripcion,
        params string[] extensionesPermitidas)
    {
        try
        {
            _ = ResolverArchivoAbsoluto(rutaArchivo, descripcion, extensionesPermitidas);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool ContieneSegmentosNavegacion(string ruta)
    {
        if (string.IsNullOrWhiteSpace(ruta) || ruta.Contains('\0'))
        {
            return true;
        }

        var segmentos = ruta.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return segmentos.Any(segmento => segmento is "." or "..");
    }

    public static bool EstaDentroDeCarpeta(string carpetaBase, string rutaArchivo)
    {
        var carpeta = Path.GetFullPath(carpetaBase)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var ruta = Path.GetFullPath(rutaArchivo);
        return string.Equals(carpeta, ruta, StringComparison.OrdinalIgnoreCase)
            || ruta.StartsWith(carpeta + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidarRutaControlada(string ruta, string descripcion)
    {
        // Bloquea navegacion de directorios y separadores no usados por rutas Windows operativas.
        if (ContieneSegmentosNavegacion(ruta) || ruta.Contains('/'))
        {
            throw new InvalidOperationException($"La ruta de {descripcion} contiene segmentos no permitidos.");
        }
    }

    private static void ValidarNombreArchivo(string nombreArchivo, string descripcion)
    {
        if (string.IsNullOrWhiteSpace(nombreArchivo)
            || nombreArchivo != Path.GetFileName(nombreArchivo)
            || ContieneSegmentosNavegacion(nombreArchivo)
            || nombreArchivo.Contains('/')
            || nombreArchivo.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException($"El nombre de {descripcion} no es seguro.");
        }
    }

    private static void ValidarExtension(
        string ruta,
        string descripcion,
        IReadOnlyCollection<string> extensionesPermitidas)
    {
        if (extensionesPermitidas.Count == 0)
        {
            return;
        }

        var extension = Path.GetExtension(ruta);
        if (!extensionesPermitidas.Any(permitida => string.Equals(extension, permitida, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"La extension de {descripcion} no esta permitida.");
        }
    }
}
