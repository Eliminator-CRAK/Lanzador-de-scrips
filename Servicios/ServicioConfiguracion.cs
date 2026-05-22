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
        Directory.CreateDirectory(RutasAplicacion.RaizAppData);
        Directory.CreateDirectory(RutasAplicacion.RaizLocalAppData);
        Directory.CreateDirectory(RutasAplicacion.RaizProgramData);

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
        Guardar(configuracion);
        return configuracion;
    }

    public void Guardar(ConfiguracionLanzador configuracion)
    {
        configuracion.Normalizar(CargarConfiguracionPredeterminada());
        Directory.CreateDirectory(RutasAplicacion.RaizAppData);
        Directory.CreateDirectory(RutasAplicacion.RaizLocalAppData);
        Directory.CreateDirectory(RutasAplicacion.RaizProgramData);
        Directory.CreateDirectory(configuracion.RutaLogs);

        var json = JsonSerializer.Serialize(configuracion, OpcionesJson);
        var protegido = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), EntropiaConfiguracion, DataProtectionScope.LocalMachine);
        foreach (var rutaDestino in ResolverRutasConfiguracionParaGuardar())
        {
            try
            {
                var carpeta = Path.GetDirectoryName(rutaDestino);
                if (!string.IsNullOrWhiteSpace(carpeta))
                {
                    Directory.CreateDirectory(carpeta);
                }

                File.WriteAllBytes(rutaDestino, protegido);
            }
            catch
            {
            }
        }
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

        if (File.Exists(RutasAplicacion.RutaConfiguracionUsuarioLegadaJson))
        {
            yield return RutasAplicacion.RutaConfiguracionUsuarioLegadaJson;
        }

        if (File.Exists(RutasAplicacion.RutaConfiguracionProgramData))
        {
            yield return RutasAplicacion.RutaConfiguracionProgramData;
        }

        if (File.Exists(RutasAplicacion.RutaConfiguracionProgramDataLegadaJson))
        {
            yield return RutasAplicacion.RutaConfiguracionProgramDataLegadaJson;
        }

        if (File.Exists(RutasAplicacion.RutaConfiguracionLegada))
        {
            yield return RutasAplicacion.RutaConfiguracionLegada;
        }
    }

    private static IEnumerable<string> ResolverRutasConfiguracionParaGuardar()
    {
        yield return RutasAplicacion.RutaConfiguracionUsuario;
        yield return RutasAplicacion.RutaConfiguracionProgramData;
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
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(datos, EntropiaConfiguracion, DataProtectionScope.LocalMachine));
        }
        catch
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(datos, EntropiaConfiguracion, DataProtectionScope.CurrentUser));
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

    private sealed record ResultadoLecturaConfiguracion(ConfiguracionLanzador? Configuracion, DateTime UltimaEscrituraUtc);
}
