// (Autor: Alex Roman)
// Descripcion: Abre la interfaz vacia sin conexiones, permisos ni ejecuciones.

using System.Security.Principal;

namespace LanzadorScripts.Servicios;

internal sealed class ClienteNativoSinConexion : IClienteNativo
{
    private static UnauthorizedAccessException Denegada() => new("Operacion no disponible.");

    public Task<SesionClienteNativa> ObtenerSesionAsync(CancellationToken cancelacion = default)
    {
        cancelacion.ThrowIfCancellationRequested();
        using var identidad = WindowsIdentity.GetCurrent();
        return Task.FromResult(new SesionClienteNativa(new UsuarioCliente(identidad.Name, "nominal", 1, false), "", "", false));
    }

    public Task<IReadOnlyList<ElementoScriptNativo>> ListarScriptsAsync(string carpeta, string buscar, CancellationToken cancelacion = default)
    {
        cancelacion.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ElementoScriptNativo>>([]);
    }

    // Ninguna operacion de este cliente puede alcanzar el motor real ni guardar configuracion.
    public Task<PermisosClienteNativo> ObtenerPermisosAsync(CancellationToken cancelacion = default) => Task.FromException<PermisosClienteNativo>(Denegada());
    public Task GuardarPermisosAsync(PermisosClienteNativo permisos, CancellationToken cancelacion = default) => Task.FromException(Denegada());
    public Task<IReadOnlyList<string>> ObtenerCarpetasAsync(CancellationToken cancelacion = default) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<CatalogoClienteNativo> ObtenerCatalogoAsync(CancellationToken cancelacion = default) => Task.FromException<CatalogoClienteNativo>(Denegada());
    public Task PublicarCatalogoAsync(IReadOnlyList<string> seleccionados, CancellationToken cancelacion = default) => Task.FromException(Denegada());
    public Task CambiarModoDesarrolloAsync(bool activo, CancellationToken cancelacion = default) => Task.FromException(Denegada());
    public Task<ResultadoExecutionPolicy> AplicarExecutionPolicyAsync(CancellationToken cancelacion = default) => Task.FromException<ResultadoExecutionPolicy>(Denegada());
    public Task<DiagnosticoEjecucionScript> DiagnosticarAsync(string scriptId, CancellationToken cancelacion = default) => Task.FromException<DiagnosticoEjecucionScript>(Denegada());
    public Task<ResultadoOperacionNativa<Guid>> IniciarAsync(string scriptId, CancellationToken cancelacion = default) => Task.FromException<ResultadoOperacionNativa<Guid>>(Denegada());
    public Task EnviarEntradaAsync(Guid ejecucionId, string texto) => Task.FromException(Denegada());
    public Task CancelarAsync(Guid ejecucionId) => Task.FromException(Denegada());
    public IAsyncEnumerable<EventoCliente> ObservarAsync(Guid ejecucionId, CancellationToken cancelacion = default) => throw Denegada();
    public IReadOnlyList<EjecucionActivaResumen> ObtenerEjecucionesActivas() => [];
    public void Dispose() { }
}
