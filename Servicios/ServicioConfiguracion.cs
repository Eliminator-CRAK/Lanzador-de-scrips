// (Autor: Alex Roman)
// Descripcion: Carga y guarda la configuracion de la aplicacion.

using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanzadorScripts.Modelos;

namespace LanzadorScripts.Servicios;

public sealed class ServicioConfiguracion
{
    private const int LongitudMaximaConfiguracion = 1024 * 1024;
    private const int IntentosLectura = 10;
    private const int EsperaLecturaMilisegundos = 50;

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly byte[] EntropiaConfiguracion = Encoding.UTF8.GetBytes("LanzadorScripts.ConfiguracionLocal.v1");
    private static readonly UTF8Encoding Utf8Estricto = new(false, true);
    private static readonly ConcurrentDictionary<string, object> BloqueosPorRuta =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _rutaConfiguracion;
    private readonly IReadOnlyList<string> _rutasLegadas;
    private readonly bool _prepararDatosUsuario;
    private readonly object _bloqueo;
    private bool _directorioPreparado;

    public ServicioConfiguracion()
        : this(
            RutasAplicacion.RutaConfiguracionUsuario,
            [
                RutasAplicacion.RutaConfiguracionUsuarioLegadaDat,
                RutasAplicacion.RutaConfiguracionUsuarioLegadaJson,
                RutasAplicacion.RutaConfiguracionLegada
            ],
            prepararDatosUsuario: true)
    {
    }

    internal ServicioConfiguracion(string rutaConfiguracion)
        : this(rutaConfiguracion, [], prepararDatosUsuario: false)
    {
    }

    private ServicioConfiguracion(
        string rutaConfiguracion,
        IReadOnlyList<string> rutasLegadas,
        bool prepararDatosUsuario)
    {
        _rutaConfiguracion = ServicioRutasSeguras.ResolverArchivoAbsoluto(
            rutaConfiguracion,
            "configuracion local",
            ".dat");
        _rutasLegadas = rutasLegadas
            .Select(ruta => ServicioRutasSeguras.ResolverArchivoAbsoluto(
                ruta,
                "configuracion legada",
                ".dat",
                ".json"))
            .Where(ruta => !string.Equals(ruta, _rutaConfiguracion, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _prepararDatosUsuario = prepararDatosUsuario;
        _bloqueo = BloqueosPorRuta.GetOrAdd(_rutaConfiguracion, static _ => new object());
    }

    public ConfiguracionLanzador Cargar()
    {
        lock (_bloqueo)
        {
            return CargarSinBloqueo();
        }
    }

    public void Guardar(ConfiguracionLanzador configuracion)
    {
        ArgumentNullException.ThrowIfNull(configuracion);
        lock (_bloqueo)
        {
            GuardarSinBloqueo(configuracion);
        }
    }

    public void AplicarRutasImportadas(string rutaScripts, string rutaPermisos)
    {
        lock (_bloqueo)
        {
            var configuracion = CargarSinBloqueo();
            configuracion.RutaScripts = rutaScripts;
            configuracion.RutaPermisos = rutaPermisos;
            GuardarSinBloqueo(configuracion);
        }
    }

    private ConfiguracionLanzador CargarSinBloqueo()
    {
        PrepararDirectorioConfiguracion();
        var configuracionPredeterminada = CargarConfiguracionPredeterminada();
        var rutasExistentes = ResolverRutasConfiguracion()
            .Where(candidata => File.Exists(candidata.Ruta))
            .ToList();
        if (rutasExistentes.Count == 0)
        {
            GuardarSinBloqueo(configuracionPredeterminada);
            return configuracionPredeterminada;
        }

        var configuraciones = rutasExistentes
            .Select(candidata => LeerConfiguracionSegura(candidata, configuracionPredeterminada))
            .Where(resultado => resultado.Configuracion is not null)
            .OrderByDescending(resultado => resultado.Candidata.Prioridad)
            .ThenByDescending(resultado => resultado.UltimaEscrituraUtc)
            .ToList();

        if (configuraciones.Count == 0)
        {
            throw new InvalidDataException(
                "La configuracion local existe, pero no se pudo descifrar o validar. No se ha reemplazado para evitar perder las rutas guardadas.");
        }

        var seleccionada = configuraciones[0];
        var configuracion = seleccionada.Configuracion!;
        var contenidoAntesDeMigrar = JsonSerializer.Serialize(configuracion, OpcionesJson);
        MigrarRutasPredeterminadasAnteriores(configuracion, configuracionPredeterminada);
        MigrarRutaLogsLegada(configuracion);
        var contenidoMigrado = JsonSerializer.Serialize(configuracion, OpcionesJson);
        if (!seleccionada.Candidata.EsPrincipal
            || !string.Equals(contenidoAntesDeMigrar, contenidoMigrado, StringComparison.Ordinal))
        {
            GuardarSinBloqueo(configuracion);
        }

        return configuracion;
    }

    private void GuardarSinBloqueo(ConfiguracionLanzador configuracion)
    {
        configuracion.Normalizar(CargarConfiguracionPredeterminada());
        configuracion.VersionConfiguracion = ConfiguracionLanzador.VersionActual;
        PrepararDirectorioConfiguracion();
        Directory.CreateDirectory(configuracion.RutaLogs);

        var json = JsonSerializer.Serialize(configuracion, OpcionesJson);
        var claro = Encoding.UTF8.GetBytes(json);
        var protegido = ProtectedData.Protect(claro, EntropiaConfiguracion, DataProtectionScope.CurrentUser);
        try
        {
            GuardarBytesAtomico(_rutaConfiguracion, protegido);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(claro);
            CryptographicOperations.ZeroMemory(protegido);
        }
    }

    private void PrepararDirectorioConfiguracion()
    {
        if (_directorioPreparado)
        {
            return;
        }

        if (_prepararDatosUsuario)
        {
            ServicioDirectoriosAplicacion.PrepararDatosUsuario();
            _directorioPreparado = true;
            return;
        }

        var carpeta = Path.GetDirectoryName(_rutaConfiguracion)
            ?? throw new InvalidOperationException("No se pudo resolver la carpeta de configuracion.");
        Directory.CreateDirectory(carpeta);
        _directorioPreparado = true;
    }

    private IEnumerable<RutaConfiguracionCandidata> ResolverRutasConfiguracion()
    {
        yield return new RutaConfiguracionCandidata(_rutaConfiguracion, Cifrada: true, EsPrincipal: true, Prioridad: 2);
        yield return new RutaConfiguracionCandidata(_rutaConfiguracion + ".bak", Cifrada: true, EsPrincipal: false, Prioridad: 1);

        foreach (var ruta in _rutasLegadas)
        {
            yield return new RutaConfiguracionCandidata(
                ruta,
                Cifrada: ruta.EndsWith(".dat", StringComparison.OrdinalIgnoreCase),
                EsPrincipal: false,
                Prioridad: 0);
        }
    }

    private static ResultadoLecturaConfiguracion LeerConfiguracionSegura(
        RutaConfiguracionCandidata candidata,
        ConfiguracionLanzador configuracionPredeterminada)
    {
        try
        {
            return new ResultadoLecturaConfiguracion(
                candidata,
                LeerConfiguracion(candidata, configuracionPredeterminada),
                File.GetLastWriteTimeUtc(candidata.Ruta));
        }
        catch (Exception ex) when (ex is CryptographicException
            or JsonException
            or DecoderFallbackException
            or InvalidDataException)
        {
            return new ResultadoLecturaConfiguracion(candidata, null, DateTime.MinValue);
        }
    }

    private static ConfiguracionLanzador LeerConfiguracion(
        RutaConfiguracionCandidata candidata,
        ConfiguracionLanzador configuracionPredeterminada)
    {
        var json = candidata.Cifrada
            ? LeerConfiguracionCifrada(candidata.Ruta)
            : LeerTextoConReintentos(candidata.Ruta);

        var configuracion = JsonSerializer.Deserialize<ConfiguracionLanzador>(json, OpcionesJson) ?? configuracionPredeterminada;
        configuracion.Normalizar(configuracionPredeterminada);
        return configuracion;
    }

    private static string LeerConfiguracionCifrada(string ruta)
    {
        // Lee configuraciones nuevas de maquina y migra las antiguas de usuario.
        var datos = LeerBytesConReintentos(ruta);
        byte[]? claro = null;
        try
        {
            try
            {
                claro = ProtectedData.Unprotect(datos, EntropiaConfiguracion, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                claro = ProtectedData.Unprotect(datos, EntropiaConfiguracion, DataProtectionScope.LocalMachine);
            }

            return Utf8Estricto.GetString(claro);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(datos);
            if (claro is not null)
            {
                CryptographicOperations.ZeroMemory(claro);
            }
        }
    }

    private static string LeerTextoConReintentos(string ruta)
    {
        var datos = LeerBytesConReintentos(ruta);
        try
        {
            return Utf8Estricto.GetString(datos);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(datos);
        }
    }

    private static byte[] LeerBytesConReintentos(string ruta)
    {
        if (ruta.Contains(".."))
        {
            throw new InvalidOperationException("La ruta de configuracion contiene segmentos no permitidos.");
        }

        if (ruta.Contains('/'))
        {
            throw new InvalidOperationException("La ruta de configuracion contiene separadores no permitidos.");
        }

        var rutaSegura = ServicioRutasSeguras.ResolverArchivoAbsoluto(
            ruta,
            "configuracion local",
            ".dat",
            ".bak",
            ".json");
        if (rutaSegura.Contains(".."))
        {
            throw new InvalidOperationException("La ruta normalizada de configuracion contiene segmentos no permitidos.");
        }

        if (rutaSegura.Contains('/'))
        {
            throw new InvalidOperationException("La ruta normalizada de configuracion contiene separadores no permitidos.");
        }

        for (var intento = 1; ; intento++)
        {
            try
            {
                using var flujo = new FileStream(
                    rutaSegura,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    4096,
                    FileOptions.SequentialScan);
                if (flujo.Length <= 0 || flujo.Length > LongitudMaximaConfiguracion)
                {
                    throw new InvalidDataException("La configuracion local tiene un tamano no valido.");
                }

                var datos = new byte[checked((int)flujo.Length)];
                flujo.ReadExactly(datos);
                return datos;
            }
            catch (IOException ex) when (intento < IntentosLectura && EsBloqueoTransitorio(ex))
            {
                // Espera a que termine una operacion local breve sobre el archivo.
                Thread.Sleep(EsperaLecturaMilisegundos);
            }
        }
    }

    private static bool EsBloqueoTransitorio(IOException excepcion)
    {
        var codigoWin32 = excepcion.HResult & 0xFFFF;
        return codigoWin32 is 32 or 33;
    }

    private static void GuardarBytesAtomico(string ruta, ReadOnlySpan<byte> contenido)
    {
        if (ruta.Contains("..", StringComparison.Ordinal) || ruta.Contains('/'))
        {
            throw new InvalidOperationException("La ruta de configuracion contiene segmentos no permitidos.");
        }

        var rutaSegura = ServicioRutasSeguras.ResolverArchivoAbsoluto(
            ruta,
            "configuracion local",
            ".dat");
        if (rutaSegura.Contains("..", StringComparison.Ordinal) || rutaSegura.Contains('/'))
        {
            throw new InvalidOperationException("La ruta normalizada de configuracion contiene segmentos no permitidos.");
        }

        var carpeta = Path.GetDirectoryName(rutaSegura)
            ?? throw new InvalidOperationException("No se pudo resolver la carpeta de configuracion.");
        Directory.CreateDirectory(carpeta);

        var temporal = Path.Combine(carpeta, $".{Path.GetFileName(rutaSegura)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var flujo = new FileStream(
                temporal,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                flujo.Write(contenido);
                flujo.Flush(flushToDisk: true);
            }

            if (File.Exists(rutaSegura))
            {
                File.Replace(temporal, rutaSegura, rutaSegura + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporal, rutaSegura);
            }
        }
        finally
        {
            try
            {
                File.Delete(temporal);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static ConfiguracionLanzador CargarConfiguracionPredeterminada()
    {
        // Carga los valores base embebidos en el ejecutable publicado.
        var ensamblado = Assembly.GetExecutingAssembly();
        var recurso = ensamblado.GetManifestResourceNames()
            .FirstOrDefault(nombre => nombre.EndsWith("ConfiguracionPredeterminada.json", StringComparison.OrdinalIgnoreCase));
        if (recurso is null)
        {
            return new ConfiguracionLanzador();
        }

        try
        {
            using var flujo = ensamblado.GetManifestResourceStream(recurso);
            if (flujo is null)
            {
                return new ConfiguracionLanzador();
            }

            var configuracion = JsonSerializer.Deserialize<ConfiguracionLanzador>(flujo, OpcionesJson) ?? new ConfiguracionLanzador();
            configuracion.Normalizar(new ConfiguracionLanzador());
            return configuracion;
        }
        catch
        {
            return new ConfiguracionLanzador();
        }
    }

    internal static void MigrarRutasPredeterminadasAnteriores(
        ConfiguracionLanzador configuracion,
        ConfiguracionLanzador configuracionPredeterminada)
    {
        // Corrige instalaciones que guardaron rutas predeterminadas anteriores.
        if (configuracion.VersionConfiguracion is null
            || configuracion.VersionConfiguracion < ConfiguracionLanzador.VersionActual)
        {
            configuracion.RutaScripts = configuracionPredeterminada.RutaScripts;
            configuracion.RutaPermisos = configuracionPredeterminada.RutaPermisos;
            configuracion.VersionConfiguracion = ConfiguracionLanzador.VersionActual;
            return;
        }

        string[] rutasScriptsAnteriores =
        [
            @"\\MAD002MICROPRU\REPO",
            @"\\MAD002MICROPRU\C$\REPO",
            @"\\MAD002MICROPRU.mad.ae.aena.es\C$\REPO"
        ];

        string[] rutasPermisosAnteriores =
        [
            @"\\MAD002MICROPRU\REPO\PERMISOS",
            @"\\MAD002MICROPRU\C$\REPO\PERMISOS",
            @"\\MAD002MICROPRU.mad.ae.aena.es\C$\REPO\PERMISOS"
        ];

        if (rutasScriptsAnteriores.Any(ruta => string.Equals(configuracion.RutaScripts, ruta, StringComparison.OrdinalIgnoreCase)))
        {
            configuracion.RutaScripts = configuracionPredeterminada.RutaScripts;
        }

        if (rutasPermisosAnteriores.Any(ruta => string.Equals(configuracion.RutaPermisos, ruta, StringComparison.OrdinalIgnoreCase)))
        {
            configuracion.RutaPermisos = configuracionPredeterminada.RutaPermisos;
        }
    }

    internal static void MigrarRutaLogsLegada(ConfiguracionLanzador configuracion)
    {
        // Mueve el log predeterminado fuera del perfil de Windows.
        try
        {
            var ruta = Path.GetFullPath(configuracion.RutaLogs);
            var raizLegada = Path.GetFullPath(RutasAplicacion.RaizLocalAppDataLegada)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (ruta.StartsWith(raizLegada, StringComparison.OrdinalIgnoreCase))
            {
                configuracion.RutaLogs = RutasAplicacion.RutaLogsUsuario;
            }
        }
        catch
        {
            configuracion.RutaLogs = RutasAplicacion.RutaLogsUsuario;
        }
    }

    private sealed record RutaConfiguracionCandidata(
        string Ruta,
        bool Cifrada,
        bool EsPrincipal,
        int Prioridad);

    private sealed record ResultadoLecturaConfiguracion(
        RutaConfiguracionCandidata Candidata,
        ConfiguracionLanzador? Configuracion,
        DateTime UltimaEscrituraUtc);
}
