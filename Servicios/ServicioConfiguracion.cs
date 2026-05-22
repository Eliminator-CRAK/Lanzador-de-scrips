// (Autor: Alex Roman)
// Descripcion: Carga y guarda la configuracion de la aplicacion.

using System.IO;
using System.Text.Json;
using LanzadorScripts.Modelos;

namespace LanzadorScripts.Servicios;

public sealed class ServicioConfiguracion
{
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = true
    };

    public ConfiguracionLanzador Cargar()
    {
        Directory.CreateDirectory(RutasAplicacion.RaizProgramData);

        var rutaConfiguracion = ResolverRutaConfiguracionExistente();
        if (rutaConfiguracion is null)
        {
            var creada = new ConfiguracionLanzador();
            Guardar(creada);
            return creada;
        }

        try
        {
            var json = File.ReadAllText(rutaConfiguracion);
            var configuracion = JsonSerializer.Deserialize<ConfiguracionLanzador>(json) ?? new ConfiguracionLanzador();
            configuracion.Normalizar();
            Guardar(configuracion);
            return configuracion;
        }
        catch
        {
            var respaldo = new ConfiguracionLanzador();
            Guardar(respaldo);
            return respaldo;
        }
    }

    public void Guardar(ConfiguracionLanzador configuracion)
    {
        configuracion.Normalizar();
        Directory.CreateDirectory(RutasAplicacion.RaizProgramData);
        Directory.CreateDirectory(configuracion.RutaLogs);

        var json = JsonSerializer.Serialize(configuracion, OpcionesJson);
        var rutaDestino = ResolverRutaConfiguracionParaGuardar();
        File.WriteAllText(rutaDestino, json);
    }

    private static string? ResolverRutaConfiguracionExistente()
    {
        if (File.Exists(RutasAplicacion.RutaConfiguracionLocal))
        {
            return RutasAplicacion.RutaConfiguracionLocal;
        }

        if (File.Exists(RutasAplicacion.RutaConfiguracionProgramData))
        {
            return RutasAplicacion.RutaConfiguracionProgramData;
        }

        return null;
    }

    private static string ResolverRutaConfiguracionParaGuardar()
    {
        try
        {
            var carpeta = Path.GetDirectoryName(RutasAplicacion.RutaConfiguracionLocal);
            if (!string.IsNullOrWhiteSpace(carpeta))
            {
                Directory.CreateDirectory(carpeta);
            }

            File.WriteAllText(RutasAplicacion.RutaConfiguracionLocal, "{}");
            File.Delete(RutasAplicacion.RutaConfiguracionLocal);
            return RutasAplicacion.RutaConfiguracionLocal;
        }
        catch
        {
            return RutasAplicacion.RutaConfiguracionProgramData;
        }
    }

}
