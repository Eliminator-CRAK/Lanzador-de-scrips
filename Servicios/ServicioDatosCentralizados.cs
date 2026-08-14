// (Autor: Alex Roman)
// Descripcion: Lee y actualiza permisos, catalogo y auditoria en el servidor central.

using System.Text.Json;
using System.Text.Json.Nodes;
using LanzadorScripts.Modelos;
using LanzadorScripts.Protocolo;

namespace LanzadorScripts.Servicios;

public sealed class ServicioDatosCentralizados
{
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false
    };

    private readonly Func<ConfiguracionLanzador> _obtenerConfiguracion;

    public ServicioDatosCentralizados(Func<ConfiguracionLanzador> obtenerConfiguracion)
    {
        _obtenerConfiguracion = obtenerConfiguracion
            ?? throw new ArgumentNullException(nameof(obtenerConfiguracion));
    }

    public bool IntentarObtenerPermisos(
        out JsonObject permisos,
        out string conjuntoId,
        out string error)
    {
        var respuesta = CrearCliente().Enviar<object, PermisosServidorCentral>(
            OperacionesServidor.ObtenerPermisos,
            new { });
        if (!respuesta.Exito || respuesta.Datos is null)
        {
            permisos = new JsonObject();
            conjuntoId = string.Empty;
            error = respuesta.Mensaje;
            return false;
        }

        permisos = respuesta.Datos.Permisos.DeepClone().AsObject();
        conjuntoId = respuesta.Datos.ConjuntoId;
        error = string.Empty;
        return true;
    }

    public void GuardarPermisos(JsonObject permisos)
    {
        ArgumentNullException.ThrowIfNull(permisos);
        var respuesta = CrearCliente().Enviar<JsonObject, PermisosServidorCentral>(
            OperacionesServidor.GuardarPermisos,
            permisos);
        if (!respuesta.Exito)
        {
            throw new InvalidOperationException(respuesta.Mensaje);
        }
    }

    public bool IntentarObtenerCatalogo(out CatalogoScripts? catalogo, out string error)
    {
        var respuesta = CrearCliente().Enviar<object, CatalogoServidorCentral>(
            OperacionesServidor.ObtenerCatalogo,
            new { });
        if (!respuesta.Exito || respuesta.Datos is null)
        {
            catalogo = null;
            error = respuesta.Mensaje;
            return false;
        }

        try
        {
            catalogo = respuesta.Datos.Catalogo.Deserialize<CatalogoScripts>(OpcionesJson);
            if (catalogo is null)
            {
                error = "El servidor central devolvio un catalogo vacio.";
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            catalogo = null;
            error = "El servidor central devolvio un catalogo no valido.";
            return false;
        }
    }

    public void GuardarCatalogo(CatalogoScripts catalogo)
    {
        ArgumentNullException.ThrowIfNull(catalogo);
        var nodo = JsonSerializer.SerializeToNode(catalogo, OpcionesJson) as JsonObject
            ?? throw new InvalidOperationException("No se pudo preparar el catalogo para el servidor central.");
        var respuesta = CrearCliente().Enviar<JsonObject, CatalogoServidorCentral>(
            OperacionesServidor.GuardarCatalogo,
            nodo);
        if (!respuesta.Exito)
        {
            throw new InvalidOperationException(respuesta.Mensaje);
        }
    }

    public RespuestaTipada<PaginaAuditoriaServidorCentral> ConsultarAuditoria(
        FiltroAuditoriaServidorCentral filtro)
    {
        return CrearCliente().Enviar<FiltroAuditoriaServidorCentral, PaginaAuditoriaServidorCentral>(
            OperacionesServidor.ConsultarAuditoria,
            filtro);
    }

    public RespuestaTipada<EstadoServidorCentral> ObtenerEstado()
    {
        return CrearCliente(TimeSpan.FromSeconds(3)).Enviar<object, EstadoServidorCentral>(
            OperacionesServidor.Salud,
            new { });
    }

    public string ObtenerRutaSanitizada()
    {
        var configuracion = _obtenerConfiguracion();
        return $"{configuracion.ServidorCentral}:{configuracion.PuertoServidorCentral}/LanzadorScripts.db";
    }

    private ClienteServidorCentral CrearCliente(TimeSpan? tiempo = null)
    {
        var configuracion = _obtenerConfiguracion();
        return new ClienteServidorCentral(
            configuracion.ServidorCentral,
            configuracion.PuertoServidorCentral,
            tiempo);
    }
}
