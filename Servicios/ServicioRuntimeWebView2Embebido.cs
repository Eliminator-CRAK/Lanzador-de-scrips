// (Autor: Alex Roman)
// Descripcion: Extrae WebView2 Fixed Runtime embebido a una ruta escribible.

using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace LanzadorScripts.Servicios;

public sealed class ServicioRuntimeWebView2Embebido
{
    internal const string NombreRecursoZip = "Recursos.WebView2Runtime.zip";

    private const int TamanoBuffer = 1048576;
    private const int MaximoVersionesConservadas = 2;
    private const string NombreEjecutableWebView2 = "msedgewebview2.exe";
    private const string NombreArchivoHash = ".lanzador-webview2.sha256";
    private const string PrefijoCarpetaRuntime = "runtime-";

    private readonly Func<Stream?> _abrirRecurso;
    private readonly IReadOnlyList<string> _raicesExtraccion;

    public ServicioRuntimeWebView2Embebido()
        : this(AbrirRecursoEnsamblado, [RutasAplicacion.RutaRuntimesWebView2, RutasAplicacion.RutaRuntimesWebView2Temporal])
    {
    }

    internal ServicioRuntimeWebView2Embebido(Func<Stream?> abrirRecurso, IReadOnlyList<string> raicesExtraccion)
    {
        _abrirRecurso = abrirRecurso;
        _raicesExtraccion = raicesExtraccion;
    }

    public ResultadoRuntimeWebView2Embebido Preparar()
    {
        using var recurso = _abrirRecurso();
        if (recurso is null)
        {
            return ResultadoRuntimeWebView2Embebido.NoDisponible("No se encontro WebView2 Fixed Runtime embebido.");
        }

        var raiz = ResolverRaizEscribible();
        if (string.IsNullOrWhiteSpace(raiz))
        {
            return ResultadoRuntimeWebView2Embebido.Error("No hay una carpeta escribible para extraer WebView2 Fixed Runtime.");
        }

        var zipTemporal = Path.Combine(raiz, $".webview2-{Guid.NewGuid():N}.zip");
        try
        {
            var hash = CopiarRecursoYCalcularHash(recurso, zipTemporal);
            var carpetaDestino = Path.Combine(raiz, PrefijoCarpetaRuntime + hash[..16]);
            if (ValidarRuntimeExtraido(carpetaDestino, hash, out var rutaRuntimeExistente))
            {
                LimpiarVersionesAntiguas(raiz, carpetaDestino);
                return ResultadoRuntimeWebView2Embebido.Correcto(rutaRuntimeExistente, hash, extraidoAhora: false);
            }

            var carpetaTemporal = Path.Combine(raiz, $".extraccion-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(carpetaTemporal);
                ZipFile.ExtractToDirectory(zipTemporal, carpetaTemporal);
                if (!ValidarRuntimeExtraido(carpetaTemporal, null, out _))
                {
                    return ResultadoRuntimeWebView2Embebido.Error("El runtime WebView2 embebido no contiene msedgewebview2.exe.");
                }

                File.WriteAllText(Path.Combine(carpetaTemporal, NombreArchivoHash), hash, Encoding.ASCII);
                ReemplazarCarpeta(carpetaTemporal, carpetaDestino);
                if (!ValidarRuntimeExtraido(carpetaDestino, hash, out var rutaRuntimeExtraido))
                {
                    return ResultadoRuntimeWebView2Embebido.Error("El runtime WebView2 extraido no supero la validacion.");
                }

                LimpiarVersionesAntiguas(raiz, carpetaDestino);
                return ResultadoRuntimeWebView2Embebido.Correcto(rutaRuntimeExtraido, hash, extraidoAhora: true);
            }
            catch (InvalidDataException)
            {
                return ResultadoRuntimeWebView2Embebido.Error("El ZIP embebido de WebView2 esta corrupto.");
            }
            finally
            {
                EliminarDirectorioSiExiste(carpetaTemporal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return ResultadoRuntimeWebView2Embebido.Error($"No se pudo preparar WebView2 Fixed Runtime embebido: {ex.Message}");
        }
        finally
        {
            EliminarArchivoSiExiste(zipTemporal);
        }
    }

    private static Stream? AbrirRecursoEnsamblado()
    {
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(NombreRecursoZip);
    }

    private string? ResolverRaizEscribible()
    {
        foreach (var raiz in _raicesExtraccion.Where(raiz => !string.IsNullOrWhiteSpace(raiz)))
        {
            try
            {
                var ruta = Path.GetFullPath(raiz);
                Directory.CreateDirectory(ruta);
                ProbarEscrituraDirectorio(ruta);
                return ruta;
            }
            catch
            {
            }
        }

        return null;
    }

    private static string CopiarRecursoYCalcularHash(Stream origen, string destino)
    {
        using var sha256 = SHA256.Create();
        using var archivo = new FileStream(destino, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[TamanoBuffer];
        int leidos;
        while ((leidos = origen.Read(buffer, 0, buffer.Length)) > 0)
        {
            archivo.Write(buffer, 0, leidos);
            sha256.TransformBlock(buffer, 0, leidos, null, 0);
        }

        sha256.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha256.Hash!);
    }

    private static bool ValidarRuntimeExtraido(string carpeta, string? hashEsperado, out string rutaRuntime)
    {
        rutaRuntime = string.Empty;
        if (!Directory.Exists(carpeta))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(hashEsperado))
        {
            var rutaHash = Path.Combine(carpeta, NombreArchivoHash);
            if (!File.Exists(rutaHash)
                || !string.Equals(File.ReadAllText(rutaHash, Encoding.ASCII).Trim(), hashEsperado, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var ejecutable = BuscarEjecutableWebView2(carpeta);
        if (string.IsNullOrWhiteSpace(ejecutable))
        {
            return false;
        }

        rutaRuntime = Path.GetDirectoryName(ejecutable) ?? carpeta;
        return true;
    }

    private static string? BuscarEjecutableWebView2(string carpeta)
    {
        try
        {
            return Directory
                .EnumerateFiles(carpeta, NombreEjecutableWebView2, SearchOption.AllDirectories)
                .OrderBy(ruta => ruta.Length)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static void ReemplazarCarpeta(string origen, string destino)
    {
        if (Directory.Exists(destino))
        {
            Directory.Delete(destino, recursive: true);
        }

        Directory.Move(origen, destino);
    }

    private static void LimpiarVersionesAntiguas(string raiz, string carpetaActual)
    {
        try
        {
            var antiguas = Directory
                .EnumerateDirectories(raiz, PrefijoCarpetaRuntime + "*", SearchOption.TopDirectoryOnly)
                .Where(carpeta => !string.Equals(carpeta, carpetaActual, StringComparison.OrdinalIgnoreCase))
                .Select(carpeta => new DirectoryInfo(carpeta))
                .OrderByDescending(carpeta => carpeta.LastWriteTimeUtc)
                .Skip(Math.Max(0, MaximoVersionesConservadas - 1));

            foreach (var carpeta in antiguas)
            {
                EliminarDirectorioSiExiste(carpeta.FullName);
            }
        }
        catch
        {
        }
    }

    private static void ProbarEscrituraDirectorio(string ruta)
    {
        var prueba = Path.Combine(ruta, $".lanzador_write_{Guid.NewGuid():N}.tmp");
        using (var flujo = new FileStream(prueba, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
        {
            flujo.WriteByte(1);
        }

        EliminarArchivoSiExiste(prueba);
    }

    private static void EliminarArchivoSiExiste(string ruta)
    {
        try
        {
            if (File.Exists(ruta))
            {
                File.Delete(ruta);
            }
        }
        catch
        {
        }
    }

    private static void EliminarDirectorioSiExiste(string ruta)
    {
        try
        {
            if (Directory.Exists(ruta))
            {
                Directory.Delete(ruta, recursive: true);
            }
        }
        catch
        {
        }
    }
}

public sealed record ResultadoRuntimeWebView2Embebido(
    bool Exito,
    bool RecursoEncontrado,
    string Mensaje,
    string? RutaRuntime,
    string? Hash,
    bool ExtraidoAhora)
{
    public static ResultadoRuntimeWebView2Embebido Correcto(string rutaRuntime, string hash, bool extraidoAhora)
    {
        return new ResultadoRuntimeWebView2Embebido(true, true, string.Empty, rutaRuntime, hash, extraidoAhora);
    }

    public static ResultadoRuntimeWebView2Embebido NoDisponible(string mensaje)
    {
        return new ResultadoRuntimeWebView2Embebido(false, false, mensaje, null, null, false);
    }

    public static ResultadoRuntimeWebView2Embebido Error(string mensaje)
    {
        return new ResultadoRuntimeWebView2Embebido(false, true, mensaje, null, null, false);
    }
}
