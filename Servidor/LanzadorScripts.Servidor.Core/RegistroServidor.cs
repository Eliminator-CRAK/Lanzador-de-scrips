// (Autor: Alex Roman)
// Descripcion: Registra eventos operativos locales del servicio central.

using System.Text.Json;

namespace LanzadorScripts.Servidor.Core;

public sealed class RegistroServidor
{
    private readonly RutasServidor _rutas;
    private readonly object _bloqueo = new();

    public RegistroServidor(RutasServidor rutas)
    {
        _rutas = rutas;
    }

    public void Escribir(string evento, string detalle)
    {
        try
        {
            _rutas.PrepararDirectorios();
            var ruta = Path.Combine(_rutas.RutaLogs, $"servidor-{DateTime.UtcNow:yyyyMMdd}.jsonl");
            var linea = JsonSerializer.Serialize(new
            {
                evento,
                detalle,
                fechaUtc = DateTimeOffset.UtcNow,
                equipo = Environment.MachineName
            });
            lock (_bloqueo)
            {
                using var flujo = new FileStream(
                    ruta,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    4096,
                    FileOptions.WriteThrough);
                using var escritor = new StreamWriter(flujo);
                escritor.WriteLine(linea);
            }
        }
        catch
        {
            // El registro local no debe detener el servicio.
        }
    }
}
