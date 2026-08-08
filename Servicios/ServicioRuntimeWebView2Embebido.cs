// (Autor: Alex Roman)
// Descripcion: Extrae WebView2 Fixed Runtime embebido a una ruta escribible.

using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace LanzadorScripts.Servicios;

public sealed class ServicioRuntimeWebView2Embebido
{
    internal const string NombreRecursoZip = "Recursos.WebView2Runtime.zip";
    internal const string VersionRuntimeFijada = "150.0.4078.48";
    internal const string HashZipRuntimeFijado = "80C46993E2D5922EFDF6463ACDA737BA0525993D4D7757D377C38F50D8BB417B";
    internal const string HashEjecutableRuntimeFijado = "30428A9075E5706B5E4A77E324B4331326566CDA027F49A8922089733C728859";
    internal const string HashContenidoRuntimeFijado = "3345CEC7106D6A8EB3A5770DFF97DF36CB0750DF005331B54AB551CDF11E3DFB";

    private const int TamanoBuffer = 1048576;
    private const int MaximoVersionesConservadas = 1;
    private const string NombreEjecutableWebView2 = "msedgewebview2.exe";
    private const string NombreArchivoHash = ".lanzador-webview2.sha256";
    private const string PrefijoCarpetaRuntime = "runtime-";

    private readonly Func<Stream?> _abrirRecurso;
    private readonly IReadOnlyList<string> _raicesExtraccion;
    private readonly string? _hashZipEsperado;
    private readonly string? _hashContenidoEsperado;
    private readonly string? _hashEjecutableEsperado;
    private readonly string? _versionEsperada;

    public ServicioRuntimeWebView2Embebido()
        : this(
            AbrirRecursoEnsamblado,
            [RutasAplicacion.RutaRuntimesWebView2],
            HashZipRuntimeFijado,
            HashContenidoRuntimeFijado,
            HashEjecutableRuntimeFijado,
            VersionRuntimeFijada)
    {
    }

    internal ServicioRuntimeWebView2Embebido(Func<Stream?> abrirRecurso, IReadOnlyList<string> raicesExtraccion)
        : this(abrirRecurso, raicesExtraccion, null, null, null, null)
    {
    }

    internal ServicioRuntimeWebView2Embebido(
        Func<Stream?> abrirRecurso,
        IReadOnlyList<string> raicesExtraccion,
        string? hashZipEsperado,
        string? hashContenidoEsperado,
        string? hashEjecutableEsperado,
        string? versionEsperada)
    {
        _abrirRecurso = abrirRecurso;
        _raicesExtraccion = raicesExtraccion;
        _hashZipEsperado = hashZipEsperado;
        _hashContenidoEsperado = hashContenidoEsperado;
        _hashEjecutableEsperado = hashEjecutableEsperado;
        _versionEsperada = versionEsperada;
    }

    public ResultadoRuntimeWebView2Embebido Preparar()
    {
        return PrepararEnRaices(_raicesExtraccion);
    }

    private ResultadoRuntimeWebView2Embebido PrepararEnRaices(IEnumerable<string> raices)
    {
        using (var recurso = _abrirRecurso())
        {
            if (recurso is null)
            {
                return ResultadoRuntimeWebView2Embebido.NoDisponible("No se encontro WebView2 Fixed Runtime embebido.");
            }
        }

        var errores = new List<string>();
        foreach (var raizCandidata in raices.Where(raiz => !string.IsNullOrWhiteSpace(raiz)))
        {
            var resultado = PrepararEnRaiz(raizCandidata);
            if (resultado.Exito)
            {
                return resultado;
            }

            errores.Add(resultado.Mensaje);
        }

        var detalle = errores.Count == 0
            ? "No hay una carpeta segura para extraer WebView2 Fixed Runtime."
            : string.Join(" ", errores.Distinct(StringComparer.Ordinal));
        return ResultadoRuntimeWebView2Embebido.Error(detalle);
    }

    private ResultadoRuntimeWebView2Embebido PrepararEnRaiz(string raizCandidata)
    {
        string raiz;
        try
        {
            raiz = Path.GetFullPath(raizCandidata);
            ServicioDirectoriosAplicacion.PrepararDirectorioRuntime(raiz);
            ProbarEscrituraDirectorio(raiz);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ResultadoRuntimeWebView2Embebido.Error($"No se puede usar {raizCandidata} para WebView2: {ex.Message}");
        }

        FileStream bloqueoRaiz;
        try
        {
            bloqueoRaiz = AdquirirBloqueoRaiz(raiz);
        }
        catch (IOException ex)
        {
            return ResultadoRuntimeWebView2Embebido.Error($"No se pudo bloquear la extraccion de WebView2 en {raiz}: {ex.Message}");
        }

        using var bloqueo = bloqueoRaiz;

        using var recurso = _abrirRecurso();
        if (recurso is null)
        {
            return ResultadoRuntimeWebView2Embebido.NoDisponible("No se encontro WebView2 Fixed Runtime embebido.");
        }

        string hash;
        if (!string.IsNullOrWhiteSpace(_hashZipEsperado))
        {
            hash = _hashZipEsperado;
            var carpetaExistente = Path.Combine(raiz, PrefijoCarpetaRuntime + hash[..16]);
            if (!IntentarAplicarPermisosRuntime(carpetaExistente, out var errorPermisosExistente))
            {
                return ResultadoRuntimeWebView2Embebido.Error(errorPermisosExistente);
            }

            if (ValidarRuntimeExtraido(carpetaExistente, hash, out var rutaRuntimeExistente))
            {
                LimpiarVersionesAntiguas(raiz, carpetaExistente);
                return ResultadoRuntimeWebView2Embebido.Correcto(rutaRuntimeExistente, hash, extraidoAhora: false);
            }
        }
        else
        {
            hash = string.Empty;
        }

        var zipTemporal = Path.Combine(raiz, $".webview2-{Guid.NewGuid():N}.zip");
        try
        {
            var hashCalculado = CopiarRecursoYCalcularHash(recurso, zipTemporal);
            if (!string.IsNullOrWhiteSpace(_hashZipEsperado)
                && !string.Equals(hashCalculado, _hashZipEsperado, StringComparison.OrdinalIgnoreCase))
            {
                return ResultadoRuntimeWebView2Embebido.Error("El ZIP embebido de WebView2 no coincide con la version fijada.");
            }

            hash = hashCalculado;
            var carpetaDestino = Path.Combine(raiz, PrefijoCarpetaRuntime + hash[..16]);
            if (!IntentarAplicarPermisosRuntime(carpetaDestino, out var errorPermisosDestino))
            {
                return ResultadoRuntimeWebView2Embebido.Error(errorPermisosDestino);
            }

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
                ReemplazarCarpeta(raiz, carpetaTemporal, carpetaDestino);
                if (!ValidarRuntimeExtraido(carpetaDestino, hash, out var rutaRuntimeExtraido))
                {
                    return ResultadoRuntimeWebView2Embebido.Error("El runtime WebView2 extraido no supero la validacion.");
                }

                if (!IntentarAplicarPermisosRuntime(carpetaDestino, out var errorPermisosFinales))
                {
                    return ResultadoRuntimeWebView2Embebido.Error(errorPermisosFinales);
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
                EliminarDirectorioSiExiste(raiz, carpetaTemporal);
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or System.Security.SecurityException
            or InvalidOperationException)
        {
            return ResultadoRuntimeWebView2Embebido.Error($"No se pudo preparar WebView2 en {raiz}: {ex.Message}");
        }
        finally
        {
            EliminarArchivoSiExiste(zipTemporal);
        }
    }

    private static Stream? AbrirRecursoEnsamblado()
    {
        // La instalacion MSI usa directamente el runtime desplegado en Program Files.
        if (!RutasAplicacion.Distribucion.EsPortable)
        {
            return null;
        }

        return Assembly.GetExecutingAssembly().GetManifestResourceStream(NombreRecursoZip);
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

    private bool ValidarRuntimeExtraido(string carpeta, string? hashEsperado, out string rutaRuntime)
    {
        rutaRuntime = string.Empty;
        try
        {
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

            if (!string.IsNullOrWhiteSpace(_hashEjecutableEsperado))
            {
                using var flujoEjecutable = File.OpenRead(ejecutable);
                var hashEjecutable = Convert.ToHexString(SHA256.HashData(flujoEjecutable));
                if (!string.Equals(hashEjecutable, _hashEjecutableEsperado, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(_versionEsperada))
            {
                var version = FileVersionInfo.GetVersionInfo(ejecutable);
                if (!string.Equals(version.FileVersion, _versionEsperada, StringComparison.Ordinal)
                    || !string.Equals(version.ProductVersion, _versionEsperada, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(_hashContenidoEsperado)
                && !string.Equals(
                    CalcularHashContenidoRuntime(carpeta),
                    _hashContenidoEsperado,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            rutaRuntime = Path.GetDirectoryName(ejecutable) ?? carpeta;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string CalcularHashContenidoRuntime(string carpeta)
    {
        // Calcula la huella de rutas, tamanos y contenido de toda la extraccion.
        var raiz = Path.GetFullPath(carpeta).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rutaMarcador = Path.GetFullPath(Path.Combine(raiz, NombreArchivoHash));
        var archivos = Directory
            .EnumerateFiles(raiz, "*", SearchOption.AllDirectories)
            .Where(ruta => !string.Equals(Path.GetFullPath(ruta), rutaMarcador, StringComparison.OrdinalIgnoreCase))
            .Select(ruta => new
            {
                Ruta = ruta,
                Relativa = Path.GetRelativePath(raiz, ruta).Replace('\\', '/')
            })
            .OrderBy(archivo => archivo.Relativa, StringComparer.Ordinal)
            .ToList();

        using var integridad = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var archivo in archivos)
        {
            AgregarLineaHash(integridad, archivo.Relativa);
            AgregarLineaHash(
                integridad,
                new FileInfo(archivo.Ruta).Length.ToString(CultureInfo.InvariantCulture));
            using var flujo = File.OpenRead(archivo.Ruta);
            AgregarLineaHash(integridad, Convert.ToHexString(SHA256.HashData(flujo)));
        }

        return Convert.ToHexString(integridad.GetHashAndReset());
    }

    private static void AgregarLineaHash(IncrementalHash integridad, string valor)
    {
        // Separa cada valor para producir una huella determinista.
        integridad.AppendData(Encoding.UTF8.GetBytes(valor + "\n"));
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

    private static void ReemplazarCarpeta(string raiz, string origen, string destino)
    {
        if (Directory.Exists(destino))
        {
            ServicioDirectoriosAplicacion.EliminarArbolSinAtravesarReanalisis(
                raiz,
                destino);
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
                if (!RuntimeEstaEnUso(carpeta.FullName))
                {
                    EliminarDirectorioSiExiste(raiz, carpeta.FullName);
                }
            }
        }
        catch
        {
        }
    }

    private static bool RuntimeEstaEnUso(string carpeta)
    {
        // Evita retirar un runtime usado por otra sesion de Windows.
        var raiz = Path.GetFullPath(carpeta).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        foreach (var proceso in Process.GetProcesses())
        {
            using (proceso)
            {
                try
                {
                    var ejecutable = proceso.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(ejecutable))
                    {
                        continue;
                    }

                    var ruta = Path.GetFullPath(ejecutable);
                    if (ruta.Equals(raiz, StringComparison.OrdinalIgnoreCase)
                        || ruta.StartsWith(
                            raiz + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or NotSupportedException)
                {
                    if (proceso.ProcessName.Equals(
                        "msedgewebview2",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool IntentarAplicarPermisosRuntime(string carpeta, out string error)
    {
        try
        {
            ServicioDirectoriosAplicacion.PrepararDirectorioRuntime(carpeta);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or InvalidOperationException)
        {
            error = $"No se pudieron aplicar los permisos de WebView2 en {carpeta}: {ex.Message}";
            return false;
        }
    }

    private static FileStream AdquirirBloqueoRaiz(string raiz)
    {
        // Evita que dos procesos sustituyan la misma extraccion.
        var rutaBloqueo = Path.Combine(raiz, ".lanzador-webview2.lock");
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                return new FileStream(rutaBloqueo, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < limite)
            {
                Thread.Sleep(100);
            }
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

    private static void EliminarDirectorioSiExiste(string raiz, string ruta)
    {
        try
        {
            if (Directory.Exists(ruta))
            {
                ServicioDirectoriosAplicacion.EliminarArbolSinAtravesarReanalisis(
                    raiz,
                    ruta);
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
