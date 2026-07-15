// (Autor: Alex Roman)
// Descripcion: Carga y guarda la configuracion de la aplicacion.

using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanzadorScripts.Modelos;

namespace LanzadorScripts.Servicios;

public sealed class ServicioConfiguracion
{
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly byte[] EntropiaConfiguracion = Encoding.UTF8.GetBytes("LanzadorScripts.ConfiguracionLocal.v1");

    public ConfiguracionLanzador Cargar()
    {
        ServicioDirectoriosAplicacion.PrepararDatosUsuario();

        var configuracionPredeterminada = CargarConfiguracionPredeterminada();
        var configuraciones = ResolverRutasConfiguracionExistentes()
            .Select(ruta => LeerConfiguracionSegura(ruta, configuracionPredeterminada))
            .Where(resultado => resultado.Configuracion is not null)
            .OrderByDescending(resultado => resultado.UltimaEscrituraUtc)
            .ToList();

        if (configuraciones.Count == 0)
        {
            var creada = configuracionPredeterminada;
            Guardar(creada);
            return creada;
        }

        var configuracion = configuraciones[0].Configuracion!;
        MigrarRutasPredeterminadasAnteriores(configuracion, configuracionPredeterminada);
        MigrarRutaLogsLegada(configuracion);
        Guardar(configuracion);
        return configuracion;
    }

    public void Guardar(ConfiguracionLanzador configuracion)
    {
        configuracion.Normalizar(CargarConfiguracionPredeterminada());
        configuracion.VersionConfiguracion = ConfiguracionLanzador.VersionActual;
        ServicioDirectoriosAplicacion.PrepararDatosUsuario();
        Directory.CreateDirectory(configuracion.RutaLogs);

        var json = JsonSerializer.Serialize(configuracion, OpcionesJson);
        var protegido = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), EntropiaConfiguracion, DataProtectionScope.CurrentUser);
        var rutaDestino = RutasAplicacion.RutaConfiguracionUsuario;
        var carpeta = Path.GetDirectoryName(rutaDestino);
        if (!string.IsNullOrWhiteSpace(carpeta))
        {
            Directory.CreateDirectory(carpeta);
        }

        File.WriteAllBytes(rutaDestino, protegido);
    }

    public void AplicarRutasImportadas(string rutaScripts, string rutaPermisos)
    {
        var configuracion = Cargar();
        configuracion.RutaScripts = rutaScripts;
        configuracion.RutaPermisos = rutaPermisos;
        Guardar(configuracion);
    }

    private static IEnumerable<string> ResolverRutasConfiguracionExistentes()
    {
        if (File.Exists(RutasAplicacion.RutaConfiguracionUsuario))
        {
            yield return RutasAplicacion.RutaConfiguracionUsuario;
        }

        if (File.Exists(RutasAplicacion.RutaConfiguracionUsuarioLegadaDat))
        {
            yield return RutasAplicacion.RutaConfiguracionUsuarioLegadaDat;
        }

        if (File.Exists(RutasAplicacion.RutaConfiguracionUsuarioLegadaJson))
        {
            yield return RutasAplicacion.RutaConfiguracionUsuarioLegadaJson;
        }

        if (File.Exists(RutasAplicacion.RutaConfiguracionLegada))
        {
            yield return RutasAplicacion.RutaConfiguracionLegada;
        }
    }

    private static ResultadoLecturaConfiguracion LeerConfiguracionSegura(string ruta, ConfiguracionLanzador configuracionPredeterminada)
    {
        try
        {
            return new ResultadoLecturaConfiguracion(
                LeerConfiguracion(ruta, configuracionPredeterminada),
                File.GetLastWriteTimeUtc(ruta));
        }
        catch
        {
            return new ResultadoLecturaConfiguracion(null, DateTime.MinValue);
        }
    }

    private static ConfiguracionLanzador LeerConfiguracion(string ruta, ConfiguracionLanzador configuracionPredeterminada)
    {
        var json = ruta.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
            ? LeerConfiguracionCifrada(ruta)
            : File.ReadAllText(ruta, Encoding.UTF8);

        var configuracion = JsonSerializer.Deserialize<ConfiguracionLanzador>(json, OpcionesJson) ?? configuracionPredeterminada;
        configuracion.Normalizar(configuracionPredeterminada);
        return configuracion;
    }

    private static string LeerConfiguracionCifrada(string ruta)
    {
        // Lee configuraciones nuevas de maquina y migra las antiguas de usuario.
        var datos = File.ReadAllBytes(ruta);
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(datos, EntropiaConfiguracion, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(datos, EntropiaConfiguracion, DataProtectionScope.LocalMachine));
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

    private sealed record ResultadoLecturaConfiguracion(ConfiguracionLanzador? Configuracion, DateTime UltimaEscrituraUtc);
}
