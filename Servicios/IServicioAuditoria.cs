// (Autor: Alex Roman)
// Descripcion: Contrato de auditoria usado por el backend y las ejecuciones.

namespace LanzadorScripts.Servicios;

public interface IServicioAuditoria : IDisposable
{
    string UltimoError { get; }

    string RutaAuditoria { get; }

    int TotalPendientes { get; }

    bool Disponible { get; }

    Task<ResultadoRegistroAuditoria> RegistrarInicioEjecucionAsync(
        Guid ejecucionId,
        ScriptInterno script,
        UsuarioCliente usuario,
        string sha256);

    Task<ResultadoRegistroAuditoria> RegistrarFinEjecucionAsync(
        Guid ejecucionId,
        ScriptInterno script,
        UsuarioCliente usuario,
        string sha256,
        string resultado,
        int? codigoSalida,
        string? detalle);

    Task<ResultadoRegistroAuditoria> RegistrarDenegacionAsync(
        string accion,
        string usuario,
        string? scriptId,
        string motivo);

    Task<ResultadoRegistroAuditoria> RegistrarErrorInternoAsync(string accion, string detalle);

    Task<ResultadoRegistroAuditoria> RegistrarEventoSeguridadAsync(
        string accion,
        string usuario,
        string? scriptId,
        string resultado,
        string detalle);

    Task<bool> VaciarPendientesAsync(TimeSpan tiempoMaximo);

    ResultadoDisponibilidadAuditoria ComprobarDisponibilidad();

    void Cerrar(TimeSpan tiempoMaximo);
}
