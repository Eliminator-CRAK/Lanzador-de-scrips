// (Autor: Alex Roman)
// Descripcion: Registra diagnostico tecnico del arranque.

using System.IO;
using System.Text;
using System.Text.Json;

namespace LanzadorScripts.Servicios;

public sealed class ServicioLogInicio
{
    private static readonly SemaphoreSlim Bloqueo = new(1, 1);

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Task RegistrarAsync(string evento, string mensaje, IReadOnlyDictionary<string, string?>? datos = null)
    {
        return RegistrarInternoAsync(evento, mensaje, datos);
    }

    public Task RegistrarExcepcionAsync(string evento, string fase, string rutaPerfil, Exception excepcion, IReadOnlyDictionary<string, string?>? datos = null)
    {
        var detalle = new Dictionary<string, string?>
        {
            ["fase"] = fase,
            ["rutaPerfil"] = rutaPerfil,
            ["tipoExcepcion"] = excepcion.GetType().FullName,
            ["hresult"] = $"0x{excepcion.HResult:X8}",
            ["mensajeExcepcion"] = excepcion.Message
        };

        if (datos is not null)
        {
            foreach (var dato in datos)
            {
                detalle[dato.Key] = dato.Value;
            }
        }

        return RegistrarInternoAsync(evento, "Error durante el arranque.", detalle);
    }

    private static async Task RegistrarInternoAsync(string evento, string mensaje, IReadOnlyDictionary<string, string?>? datos)
    {
        try
        {
            Directory.CreateDirectory(RutasAplicacion.RutaLogsUsuario);
            var ruta = Path.Combine(RutasAplicacion.RutaLogsUsuario, $"arranque-{DateTime.UtcNow:yyyyMMdd}.jsonl");
            var entrada = new EntradaLogInicio(
                ServicioRedaccionSecretos.Sanitizar(evento),
                ServicioRedaccionSecretos.Sanitizar(mensaje),
                ServicioRedaccionSecretos.Sanitizar(datos));
            var json = JsonSerializer.Serialize(entrada, OpcionesJson);

            await Bloqueo.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(ruta, json + Environment.NewLine, Encoding.UTF8);
            }
            finally
            {
                Bloqueo.Release();
            }
        }
        catch
        {
        }
    }

    private sealed record EntradaLogInicio(string Evento, string Mensaje, IReadOnlyDictionary<string, string?>? Datos)
    {
        public DateTimeOffset FechaUtc { get; init; } = DateTimeOffset.UtcNow;

        public string Equipo { get; init; } = Environment.MachineName;

        public string UsuarioWindows { get; init; } = Environment.UserName;
    }
}
