// (Autor: Alex Roman)
// Descripcion: Define los mensajes intercambiados con el servidor central.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace LanzadorScripts.Protocolo;

public static class OperacionesServidor
{
    public const string Salud = "salud";
    public const string ObtenerPermisos = "permisos.obtener";
    public const string GuardarPermisos = "permisos.guardar";
    public const string ObtenerCatalogo = "catalogo.obtener";
    public const string GuardarCatalogo = "catalogo.guardar";
    public const string RegistrarAuditoria = "auditoria.registrar";
    public const string ConsultarAuditoria = "auditoria.consultar";
    public const string ListarUsuarios = "usuarios.listar";
    public const string GuardarUsuario = "usuarios.guardar";
    public const string EliminarUsuario = "usuarios.eliminar";
    public const string CrearCopiaSeguridad = "mantenimiento.copia";
    public const string ComprobarIntegridad = "mantenimiento.integridad";
}

public sealed record SolicitudServidor(
    int Version,
    Guid SolicitudId,
    string Operacion,
    JsonElement Datos);

public sealed record RespuestaServidor(
    int Version,
    Guid SolicitudId,
    bool Exito,
    string Codigo,
    string Mensaje,
    JsonElement Datos);

public sealed record EstadoServidorCentral(
    string Version,
    string Equipo,
    bool BaseInicializada,
    bool BaseIntegra,
    int Puerto,
    int TotalUsuarios,
    long TotalAuditorias,
    DateTimeOffset? UltimaAuditoriaUtc,
    string Mensaje,
    bool AutenticacionRemotaPreparada = false,
    string SpnServidor = "",
    string MensajeAutenticacion = "");

public sealed record PermisosServidorCentral(
    string ConjuntoId,
    long Revision,
    JsonObject Permisos);

public sealed record CatalogoServidorCentral(
    string ConjuntoId,
    long Revision,
    JsonObject Catalogo);

public sealed record UsuarioServidorCentral(
    string Id,
    string NombreUsuario,
    string Rol,
    int MaxScriptsSimultaneos,
    IReadOnlyList<string> CarpetasPermitidas,
    bool Activo);

public sealed record GuardarUsuarioServidorCentral(
    string? Id,
    string NombreUsuario,
    string Rol,
    int MaxScriptsSimultaneos,
    IReadOnlyList<string> CarpetasPermitidas,
    bool Activo);

public sealed record EliminarUsuarioServidorCentral(string Id);

public sealed record EventoAuditoriaServidorCentral(
    string EventoId,
    string Accion,
    string Resultado,
    string UsuarioWindows,
    string UsuarioSid,
    string Equipo,
    string? ScriptId,
    string? ScriptNombre,
    string? ScriptSha256,
    string? EjecucionId,
    int? CodigoSalida,
    string Motivo,
    string Detalle,
    DateTimeOffset FechaUtc,
    DateTimeOffset FechaLocal);

public sealed record FiltroAuditoriaServidorCentral(
    string? Usuario,
    DateTimeOffset? DesdeUtc,
    DateTimeOffset? HastaUtc,
    string? Resultado,
    string? Script,
    int Limite = 500,
    int Desplazamiento = 0);

public sealed record PaginaAuditoriaServidorCentral(
    IReadOnlyList<string> Usuarios,
    IReadOnlyList<EventoAuditoriaServidorCentral> Eventos,
    long Total);

public sealed record ResultadoCopiaServidorCentral(
    string NombreArchivo,
    DateTimeOffset FechaUtc,
    long Longitud);

public sealed record ResultadoIntegridadServidorCentral(bool Integra, string Mensaje);

public sealed record RespuestaTipada<T>(bool Exito, string Codigo, string Mensaje, T? Datos)
{
    public static RespuestaTipada<T> Correcta(T datos, string mensaje = "")
    {
        return new RespuestaTipada<T>(true, "ok", mensaje, datos);
    }

    public static RespuestaTipada<T> Error(string codigo, string mensaje)
    {
        return new RespuestaTipada<T>(false, codigo, mensaje, default);
    }
}
