// (Autor: Alex Roman)
// Descripcion: Conecta los controles WPF con el motor autorizado sin iniciar un servidor local.

namespace LanzadorScripts.Servicios;

public sealed class ServicioClienteNativo : IClienteNativo
{
    private readonly ServidorLocalWeb _motor;

    public ServicioClienteNativo() : this(ServidorLocalWeb.CrearMotorNativo()) { }

    internal ServicioClienteNativo(ServidorLocalWeb motor) => _motor = motor;

    // Las operaciones remotas se ejecutan fuera del hilo visual.
    public Task<SesionClienteNativa> ObtenerSesionAsync(CancellationToken cancelacion = default) =>
        Task.Run(() => _motor.ObtenerSesionNativaAsync(cancelacion), cancelacion);
    public Task<IReadOnlyList<ElementoScriptNativo>> ListarScriptsAsync(string carpeta, string buscar, CancellationToken cancelacion = default) =>
        Task.Run(() => _motor.ListarScriptsNativosAsync(carpeta, buscar, cancelacion), cancelacion);
    public Task<PermisosClienteNativo> ObtenerPermisosAsync(CancellationToken cancelacion = default) =>
        Task.Run(() => _motor.ObtenerPermisosNativosAsync(cancelacion), cancelacion);
    public Task GuardarPermisosAsync(PermisosClienteNativo permisos, CancellationToken cancelacion = default) =>
        Task.Run(() => _motor.GuardarPermisosNativosAsync(permisos, cancelacion), cancelacion);
    public Task<IReadOnlyList<string>> ObtenerCarpetasAsync(CancellationToken cancelacion = default) =>
        Task.Run(() => _motor.ObtenerCarpetasNativasAsync(cancelacion), cancelacion);
    public Task<CatalogoClienteNativo> ObtenerCatalogoAsync(CancellationToken cancelacion = default) =>
        Task.Run(() => _motor.ObtenerCatalogoNativoAsync(cancelacion), cancelacion);
    public Task PublicarCatalogoAsync(IReadOnlyList<string> seleccionados, CancellationToken cancelacion = default) =>
        Task.Run(() => _motor.PublicarCatalogoNativoAsync(seleccionados, cancelacion), cancelacion);
    public Task CambiarModoDesarrolloAsync(bool activo, CancellationToken cancelacion = default) =>
        Task.Run(() => _motor.CambiarModoDesarrolloNativoAsync(activo, cancelacion), cancelacion);
    public Task<ResultadoExecutionPolicy> AplicarExecutionPolicyAsync(CancellationToken cancelacion = default) =>
        Task.Run(() => _motor.AplicarExecutionPolicyNativaAsync(cancelacion), cancelacion);
    public Task<DiagnosticoEjecucionScript> DiagnosticarAsync(string scriptId, CancellationToken cancelacion = default) =>
        Task.Run(() => _motor.DiagnosticarNativoAsync(scriptId, cancelacion), cancelacion);
    public Task<ResultadoOperacionNativa<Guid>> IniciarAsync(string scriptId, CancellationToken cancelacion = default) =>
        Task.Run(() => _motor.IniciarEjecucionNativaAsync(scriptId, cancelacion), cancelacion);
    public Task EnviarEntradaAsync(Guid ejecucionId, string texto) => _motor.EnviarEntradaNativaAsync(ejecucionId, texto);
    public Task CancelarAsync(Guid ejecucionId) => _motor.CancelarEjecucionNativaAsync(ejecucionId);
    public IAsyncEnumerable<EventoCliente> ObservarAsync(Guid ejecucionId, CancellationToken cancelacion = default) => _motor.ObservarEjecucionNativaAsync(ejecucionId, cancelacion);
    public IReadOnlyList<EjecucionActivaResumen> ObtenerEjecucionesActivas() => _motor.ObtenerEjecucionesActivas();
    public void Dispose() => _motor.Dispose();
}
