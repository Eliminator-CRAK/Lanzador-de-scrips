// (Autor: Alex Roman)
// Descripcion: Operaciones locales tipadas reutilizando la autorizacion y auditoria del motor.

using System.IO;
using System.Text.Json.Nodes;

namespace LanzadorScripts.Servicios;

public sealed partial class ServidorLocalWeb
{
    private readonly SemaphoreSlim _bloqueoInicioNativo = new(1, 1);

    internal static ServidorLocalWeb CrearMotorNativo() => new(0);

    internal static ServidorLocalWeb CrearMotorNativoParaPruebas(Modelos.ConfiguracionLanzador configuracion, ServicioArtefactosFirmados? artefactos = null) =>
        artefactos is null ? new(0, configuracion) : new(0, configuracion, new ServicioTokenMaestro(), artefactos);

    private async Task PrepararOperacionNativaAsync(CancellationToken cancelacion)
    {
        // Cada operacion confirma la configuracion central antes de consultar permisos.
        if (!_usarArtefactosLocales)
            await _servicioConfiguracion.ActualizarDesdeServidorAsync(cancelacion).ConfigureAwait(false);
        cancelacion.ThrowIfCancellationRequested();
    }

    internal async Task<SesionClienteNativa> ObtenerSesionNativaAsync(CancellationToken cancelacion)
    {
        await PrepararOperacionNativaAsync(cancelacion);
        var permisos = await ObtenerDiagnosticoAjustesAsync();
        var configuracion = CargarConfiguracion();
        var inaccesible = await RutaScriptsInaccesibleAsync(configuracion.RutaScripts);
        return new SesionClienteNativa(ObtenerUsuarioActual(permisos),
            CrearAvisoConexion(permisos.ModoOffline, inaccesible), configuracion.RutaScripts, _modoDesarrolloFirmas);
    }

    internal async Task<IReadOnlyList<ElementoScriptNativo>> ListarScriptsNativosAsync(string carpeta, string buscar, CancellationToken cancelacion)
    {
        if (buscar.Length > 200) throw new ArgumentException("La busqueda no puede superar 200 caracteres.");
        await PrepararOperacionNativaAsync(cancelacion);
        if (NormalizarCarpetaSolicitada(carpeta) is null) throw new ArgumentException("La carpeta no es valida.");
        return ObtenerScriptsParaCliente(carpeta, buscar)
            .Select(s => new ElementoScriptNativo(s.Id, s.Nombre, s.Tipo, s.EstaBloqueado, s.MotivoBloqueo, s.EsCarpeta, s.Carpeta)).ToArray();
    }

    private async Task<DiagnosticoPermisos> ExigirAdministradorNativoAsync(CancellationToken cancelacion)
    {
        await PrepararOperacionNativaAsync(cancelacion);
        // Las operaciones administrativas no reutilizan permisos de una consulta anterior.
        var permisos = ObtenerDiagnosticoPermisos();
        var usuario = ObtenerUsuarioActual(permisos);
        if (!permisos.EstaDisponible || !usuario.EstaAutorizado || usuario.Rol != "admin")
            throw new UnauthorizedAccessException("Solo administradores autorizados pueden realizar esta operacion.");
        return permisos;
    }

    internal async Task<PermisosClienteNativo> ObtenerPermisosNativosAsync(CancellationToken cancelacion)
    {
        var diagnostico = await ExigirAdministradorNativoAsync(cancelacion);
        var politica = ServicioSeguridadScripts.LeerPolitica(diagnostico.Permisos);
        var usuarios = (diagnostico.Permisos["usuarios"] as JsonArray ?? [])
            .OfType<JsonObject>().Select(u => new UsuarioPermisoNativo(
                LeerTexto(u, "id", Guid.NewGuid().ToString("N")), LeerTexto(u, "nombreUsuario", ""),
                LeerTexto(u, "rol", "nominal"), LeerEntero(u, "maxScriptsSimultaneos", 5),
                LeerArrayTexto(u["carpetasPermitidas"] as JsonArray))).ToArray();
        return new PermisosClienteNativo(usuarios, LeerArrayTexto(diagnostico.Permisos["scriptsAdmin"] as JsonArray),
            politica.ScriptsElevadosPermitidos.ToArray(), politica.PermitirExecutionPolicyBypass);
    }

    internal async Task GuardarPermisosNativosAsync(PermisosClienteNativo permisos, CancellationToken cancelacion)
    {
        await ExigirAdministradorNativoAsync(cancelacion);
        ArgumentNullException.ThrowIfNull(permisos);
        if (permisos.Usuarios.Any(u => string.IsNullOrWhiteSpace(u.NombreUsuario) || u.NombreUsuario.Length > 256
                || u.MaxScriptsSimultaneos is < 1 or > 50 || u.Rol is not ("admin" or "nominal")))
            throw new ArgumentException("Revise los nombres, roles y limites de los usuarios.");
        if (permisos.Usuarios.Select(u => u.NombreUsuario.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != permisos.Usuarios.Count)
            throw new ArgumentException("No puede haber usuarios repetidos.");
        var usuarios = new JsonArray();
        foreach (var usuario in permisos.Usuarios)
        {
            usuarios.Add(new JsonObject
            {
                ["id"] = usuario.Id, ["nombreUsuario"] = usuario.NombreUsuario.Trim(), ["rol"] = usuario.Rol,
                ["maxScriptsSimultaneos"] = usuario.MaxScriptsSimultaneos,
                ["carpetasPermitidas"] = CrearArrayNativo(usuario.CarpetasPermitidas)
            });
        }
        var resultado = GuardarPermisos(new JsonObject
        {
            ["usuarios"] = usuarios, ["scriptsAdmin"] = CrearArrayNativo(permisos.ScriptsAdmin),
            ["seguridadScripts"] = new JsonObject
            {
                ["scriptsElevadosPermitidos"] = CrearArrayNativo(permisos.ScriptsElevadosPermitidos),
                ["permitirExecutionPolicyBypass"] = permisos.PermitirExecutionPolicyBypass
            }
        });
        if (!resultado.PermisosGuardados) throw new InvalidOperationException(resultado.AvisoConexion);
        InvalidarDiagnosticoAjustes();
    }

    private static JsonArray CrearArrayNativo(IEnumerable<string> valores) => new(valores.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    internal async Task<IReadOnlyList<string>> ObtenerCarpetasNativasAsync(CancellationToken cancelacion)
    {
        await ExigirAdministradorNativoAsync(cancelacion);
        return ObtenerSubcarpetasScripts().Select(c => c.Id).ToArray();
    }

    internal async Task<CatalogoClienteNativo> ObtenerCatalogoNativoAsync(CancellationToken cancelacion)
    {
        var permisos = await ExigirAdministradorNativoAsync(cancelacion);
        var catalogo = ObtenerDiagnosticoCatalogo(permisos);
        return new CatalogoClienteNativo(catalogo.EstaDisponible, catalogo.Mensaje,
            _servicioCatalogoScripts.ObtenerEstados(ObtenerScriptsInternos(), catalogo.Catalogo));
    }

    internal async Task PublicarCatalogoNativoAsync(IReadOnlyList<string> seleccionados, CancellationToken cancelacion)
    {
        var permisos = await ExigirAdministradorNativoAsync(cancelacion);
        var scripts = _servicioValidacionScripts.DescubrirScripts(CargarConfiguracion().RutaScripts);
        CatalogoScripts catalogo;
        if (_usarArtefactosLocales)
            _servicioConjuntoArtefactos.GuardarCatalogoPreservandoConjunto(ObtenerRutaPermisosCompleta(CargarConfiguracion()), scripts, seleccionados, out catalogo);
        else
        {
            catalogo = _servicioCatalogoScripts.Crear(scripts, seleccionados, permisos.ConjuntoId);
            _servicioDatosCentralizados.GuardarCatalogo(catalogo);
        }
        await _servicioAuditoria.RegistrarEventoSeguridadAsync("seguridad.catalogo.publicado", ObtenerUsuarioActual(permisos).NombreUsuario,
            null, "publicado", $"Catalogo publicado con {catalogo.Scripts.Count} scripts.");
    }

    internal async Task CambiarModoDesarrolloNativoAsync(bool activo, CancellationToken cancelacion)
    {
        var permisos = await ExigirAdministradorNativoAsync(cancelacion);
        await _servicioAuditoria.RegistrarEventoSeguridadAsync("seguridad.modo_desarrollo_firmas", ObtenerUsuarioActual(permisos).NombreUsuario,
            null, activo ? "activado" : "desactivado", "Modo desarrollo cambiado para la sesion local.");
        _modoDesarrolloFirmas = activo;
    }

    internal async Task<DiagnosticoEjecucionScript> DiagnosticarNativoAsync(string scriptId, CancellationToken cancelacion)
    {
        await PrepararOperacionNativaAsync(cancelacion);
        var validacion = _servicioValidacionScripts.ValidarScriptParaEjecucion(CargarConfiguracion().RutaScripts, scriptId);
        if (!validacion.EsValido) throw new ArgumentException(validacion.Mensaje);
        var permisos = ObtenerDiagnosticoPermisos();
        var usuario = ObtenerUsuarioActual(permisos);
        if (ScriptBloqueado(scriptId, usuario, permisos)) throw new UnauthorizedAccessException("Acceso denegado para este script.");
        var catalogo = ObtenerDiagnosticoCatalogo(permisos);
        return _servicioSeguridadScripts.Diagnosticar(validacion.Script!, permisos.Permisos, catalogo.Catalogo, catalogo.Mensaje, _modoDesarrolloFirmas);
    }

    internal async Task<ResultadoExecutionPolicy> AplicarExecutionPolicyNativaAsync(CancellationToken cancelacion)
    {
        var permisos = await ExigirAdministradorNativoAsync(cancelacion);
        var resultado = await new ServicioExecutionPolicy().AplicarUnrestrictedAsync();
        await _servicioAuditoria.RegistrarEventoSeguridadAsync("seguridad.execution_policy", ObtenerUsuarioActual(permisos).NombreUsuario,
            null, resultado.Exito ? "correcto" : "error", "Cambio local de ExecutionPolicy solicitado por administrador.");
        return resultado;
    }

    internal async Task<ResultadoOperacionNativa<Guid>> IniciarEjecucionNativaAsync(string scriptId, CancellationToken cancelacion)
    {
        // Serializa la admision para respetar el limite aunque se pulse dos veces.
        await _bloqueoInicioNativo.WaitAsync(cancelacion);
        try
        {
            await PrepararOperacionNativaAsync(cancelacion);
            var configuracion = CargarConfiguracion();
            var validacion = _servicioValidacionScripts.ValidarScriptParaEjecucion(configuracion.RutaScripts, scriptId);
            var permisos = ObtenerDiagnosticoPermisos();
            var usuario = ObtenerUsuarioActual(permisos);
            if (!validacion.EsValido)
                return await DenegarInicioNativoAsync("ejecucion.validacion", usuario.NombreUsuario, scriptId, validacion.Mensaje, 400);
            var script = validacion.Script!;
            if (PermisosInaccesiblesSinDesbloqueo(permisos) || !usuario.EstaAutorizado || ScriptBloqueado(script.Id, usuario, permisos))
                return await DenegarInicioNativoAsync("ejecucion.permisos", usuario.NombreUsuario, script.Id,
                    usuario.EstaAutorizado ? "Acceso denegado para este script." : usuario.MotivoBloqueo, 403);
            var catalogo = ObtenerDiagnosticoCatalogo(permisos);
            var seguridad = _servicioSeguridadScripts.Diagnosticar(script, permisos.Permisos, catalogo.Catalogo, catalogo.Mensaje, _modoDesarrolloFirmas);
            if (!seguridad.Permitido)
                return await DenegarInicioNativoAsync("ejecucion.seguridad", usuario.NombreUsuario, script.Id, seguridad.MotivoBloqueo, 403);
            if (_gestorEjecuciones.RecuentoActivas >= usuario.MaxScriptsSimultaneos)
                return await DenegarInicioNativoAsync("ejecucion.limite", usuario.NombreUsuario, script.Id, "Limite de ejecuciones simultaneas alcanzado.", 429);
            if (seguridad.ExecutionPolicyBypassPermitido)
                await _servicioAuditoria.RegistrarEventoSeguridadAsync("ejecucion.execution_policy_bypass", usuario.NombreUsuario, script.Id, "permitido", "Bypass permitido por politica administrativa.");
            if (_modoDesarrolloFirmas)
                await _servicioAuditoria.RegistrarEventoSeguridadAsync("ejecucion.modo_desarrollo_firmas", usuario.NombreUsuario, script.Id, "permitido", "Validacion omitida por modo desarrollo temporal.");
            cancelacion.ThrowIfCancellationRequested();
            var inicio = await _gestorEjecuciones.IniciarAsync(script, configuracion.RutaLogs, usuario,
                seguridad.ExecutionPolicyBypassPermitido, permisos.Permisos,
                catalogo.Catalogo ?? new CatalogoScripts(1, DateTimeOffset.UtcNow, permisos.ConjuntoId, []), _modoDesarrolloFirmas, seguridad.Sha256);
            return new ResultadoOperacionNativa<Guid>(inicio.Exito, inicio.EjecucionId ?? Guid.Empty, inicio.Mensaje, inicio.Exito ? 200 : 503);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            return new ResultadoOperacionNativa<Guid>(false, default, ServicioRedaccionSecretos.Sanitizar(ex.Message), 503);
        }
        finally { _bloqueoInicioNativo.Release(); }
    }

    private async Task<ResultadoOperacionNativa<Guid>> DenegarInicioNativoAsync(string tipo, string usuario, string script, string motivo, int codigo)
    {
        await _servicioAuditoria.RegistrarDenegacionAsync(tipo, usuario, script, motivo);
        return new ResultadoOperacionNativa<Guid>(false, default, motivo, codigo);
    }

    internal Task EnviarEntradaNativaAsync(Guid id, string texto) => _gestorEjecuciones.EnviarEntradaAsync(id, texto);
    internal Task CancelarEjecucionNativaAsync(Guid id) => _gestorEjecuciones.CancelarAsync(id);
    internal IAsyncEnumerable<EventoCliente> ObservarEjecucionNativaAsync(Guid id, CancellationToken cancelacion) => _gestorEjecuciones.ObservarEventosAsync(id, cancelacion);
}
