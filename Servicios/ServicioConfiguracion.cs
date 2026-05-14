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

        if (!File.Exists(RutasAplicacion.RutaConfiguracion))
        {
            var creada = new ConfiguracionLanzador();
            Guardar(creada);
            return creada;
        }

        try
        {
            var json = File.ReadAllText(RutasAplicacion.RutaConfiguracion);
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
        File.WriteAllText(RutasAplicacion.RutaConfiguracion, json);
    }

}
