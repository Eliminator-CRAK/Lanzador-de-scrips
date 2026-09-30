// (Autor: Alex Roman)
// Descripcion: Contratos tipados del cliente WPF y sus operaciones locales.

namespace LanzadorScripts.Servicios;

public sealed record SesionClienteNativa(UsuarioCliente Usuario, string AvisoConexion, string RutaScripts, bool ModoDesarrolloFirmas);
public sealed record ElementoScriptNativo(string Id, string Nombre, string Tipo, bool EstaBloqueado, string MotivoBloqueo, bool EsCarpeta, string Carpeta);
public sealed record UsuarioPermisoNativo(string Id, string NombreUsuario, string Rol, int MaxScriptsSimultaneos, IReadOnlyList<string> CarpetasPermitidas);
public sealed record PermisosClienteNativo(IReadOnlyList<UsuarioPermisoNativo> Usuarios, IReadOnlyList<string> ScriptsAdmin, IReadOnlyList<string> ScriptsElevadosPermitidos, bool PermitirExecutionPolicyBypass);
public sealed record CatalogoClienteNativo(bool Valido, string Mensaje, IReadOnlyList<EstadoCatalogoScriptCliente> Scripts);
public sealed record ResultadoOperacionNativa<T>(bool Exito, T? Datos, string Mensaje, int Codigo = 200);

public interface IClienteNativo : IDisposable
{
    // Expone operaciones sin transportar peticiones HTTP ni contenido HTML.
    Task<SesionClienteNativa> ObtenerSesionAsync(CancellationToken cancelacion = default);
    Task<IReadOnlyList<ElementoScriptNativo>> ListarScriptsAsync(string carpeta, string buscar, CancellationToken cancelacion = default);
    Task<PermisosClienteNativo> ObtenerPermisosAsync(CancellationToken cancelacion = default);
    Task GuardarPermisosAsync(PermisosClienteNativo permisos, CancellationToken cancelacion = default);
    Task<IReadOnlyList<string>> ObtenerCarpetasAsync(CancellationToken cancelacion = default);
    Task<CatalogoClienteNativo> ObtenerCatalogoAsync(CancellationToken cancelacion = default);
    Task PublicarCatalogoAsync(IReadOnlyList<string> seleccionados, CancellationToken cancelacion = default);
    Task CambiarModoDesarrolloAsync(bool activo, CancellationToken cancelacion = default);
    Task<ResultadoExecutionPolicy> AplicarExecutionPolicyAsync(CancellationToken cancelacion = default);
    Task<DiagnosticoEjecucionScript> DiagnosticarAsync(string scriptId, CancellationToken cancelacion = default);
    Task<ResultadoOperacionNativa<Guid>> IniciarAsync(string scriptId, CancellationToken cancelacion = default);
    Task EnviarEntradaAsync(Guid ejecucionId, string texto);
    Task CancelarAsync(Guid ejecucionId);
    IAsyncEnumerable<EventoCliente> ObservarAsync(Guid ejecucionId, CancellationToken cancelacion = default);
    IReadOnlyList<EjecucionActivaResumen> ObtenerEjecucionesActivas();
}
