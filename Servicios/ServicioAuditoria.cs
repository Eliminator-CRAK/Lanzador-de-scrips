// (Autor: Alex Roman)
// Descripcion: Registra auditoria local de acciones criticas.

using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace LanzadorScripts.Servicios;

public sealed class ServicioAuditoria
{
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SemaphoreSlim _bloqueo = new(1, 1);

    public Task RegistrarInicioEjecucionAsync(Guid ejecucionId, ScriptInterno script, UsuarioCliente usuario)
    {
        return RegistrarAsync(new EventoAuditoria(
            "ejecucion.inicio",
            "permitido",
            usuario.NombreUsuario,
            script.Id,
            script.Nombre,
            ejecucionId,
            null,
            null,
            null));
    }

    public Task RegistrarFinEjecucionAsync(Guid ejecucionId, ScriptInterno script, UsuarioCliente usuario, string resultado, int? codigoSalida, string? detalle)
    {
        return RegistrarAsync(new EventoAuditoria(
            "ejecucion.fin",
            resultado,
            usuario.NombreUsuario,
            script.Id,
            script.Nombre,
            ejecucionId,
            codigoSalida,
            null,
            detalle));
    }

    public Task RegistrarDenegacionAsync(string accion, string usuario, string? scriptId, string motivo)
    {
        return RegistrarAsync(new EventoAuditoria(
            accion,
            "denegado",
            usuario,
            scriptId,
            null,
            null,
            null,
            motivo,
            null));
    }

    public Task RegistrarErrorInternoAsync(string accion, string detalle)
    {
        return RegistrarAsync(new EventoAuditoria(
            accion,
            "error",
            WindowsIdentity.GetCurrent().Name,
            null,
            null,
            null,
            null,
            "Error interno",
            detalle));
    }

    private async Task RegistrarAsync(EventoAuditoria evento)
    {
        try
        {
            Directory.CreateDirectory(RutasAplicacion.RutaAuditoria);
            var ruta = Path.Combine(RutasAplicacion.RutaAuditoria, $"{DateTime.UtcNow:yyyyMMdd}.jsonl");
            var json = JsonSerializer.Serialize(evento, OpcionesJson);

            await _bloqueo.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(ruta, json + Environment.NewLine, Encoding.UTF8);
            }
            finally
            {
                _bloqueo.Release();
            }
        }
        catch
        {
        }
    }

    private sealed record EventoAuditoria(
        string Accion,
        string Resultado,
        string UsuarioWindows,
        string? ScriptId,
        string? ScriptNombre,
        Guid? EjecucionId,
        int? CodigoSalida,
        string? Motivo,
        string? Detalle)
    {
        public DateTimeOffset FechaUtc { get; init; } = DateTimeOffset.UtcNow;

        public string Equipo { get; init; } = Environment.MachineName;
    }
}
