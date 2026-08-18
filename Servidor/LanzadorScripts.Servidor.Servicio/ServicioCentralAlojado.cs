// (Autor: Alex Roman)
// Descripcion: Inicia y detiene los componentes del servicio central.

using System.Security.Cryptography;
using LanzadorScripts.Servidor.Core;
using Microsoft.Extensions.Hosting;

namespace LanzadorScripts.Servidor.Servicio;

public sealed class ServicioCentralAlojado : IHostedService, IAsyncDisposable
{
    public const string NombreServicio = "LanzadorScriptsServidor";
    public const string NombreVisible = "LanzadorScripts Servidor";

    private readonly RutasServidor _rutas;
    private readonly AlmacenConfiguracionServidor _almacenConfiguracion;
    private readonly RegistroServidor _registro;
    private RepositorioServidor? _repositorio;
    private ServidorTcpSeguro? _servidor;
    private ServidorAdministracionLocal? _administracionLocal;

    public ServicioCentralAlojado(
        RutasServidor rutas,
        AlmacenConfiguracionServidor almacenConfiguracion,
        RegistroServidor registro)
    {
        _rutas = rutas;
        _almacenConfiguracion = almacenConfiguracion;
        _registro = registro;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var configuracion = _almacenConfiguracion.CargarOCrear();
            var clave = new AlmacenClaveServidor(_rutas).ObtenerOCrear();
            try
            {
                _repositorio = new RepositorioServidor(_rutas, configuracion, clave);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clave);
            }

            var administradorInicial = new AlmacenAdministradorInicialServidor(_rutas);
            _repositorio.Inicializar(administradorInicial.Leer());
            administradorInicial.Eliminar();
            PrepararDatosIniciales(configuracion);
            var integridad = _repositorio.ComprobarIntegridad();
            if (!integridad.Integra)
            {
                throw new InvalidDataException(integridad.Mensaje);
            }

            var procesador = new ProcesadorSolicitudesServidor(_repositorio);
            _servidor = new ServidorTcpSeguro(configuracion, procesador, _registro.Escribir);
            _servidor.Iniciar();
            _administracionLocal = new ServidorAdministracionLocal(
                procesador,
                _registro.Escribir);
            _administracionLocal.Iniciar();
            _registro.Escribir("servicio.iniciado", "Base central preparada.");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _registro.Escribir("servicio.inicio.error", ex.GetType().Name);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_administracionLocal is not null)
        {
            await _administracionLocal.DisposeAsync();
            _administracionLocal = null;
        }

        if (_servidor is not null)
        {
            await _servidor.DisposeAsync();
            _servidor = null;
        }

        _repositorio?.Dispose();
        _repositorio = null;
        _registro.Escribir("servicio.detenido", string.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
    }

    private void PrepararDatosIniciales(ConfiguracionServidor configuracion)
    {
        var limite = DateTimeOffset.UtcNow.AddDays(-configuracion.DiasRetencionAuditoria);
        var eliminadas = _repositorio!.PurgarAuditoriaAnteriorA(limite);
        if (eliminadas > 0)
        {
            _registro.Escribir("auditoria.retencion", $"Eventos eliminados: {eliminadas}.");
        }

        var catalogo = _repositorio.ObtenerCatalogo();
        if (catalogo.Catalogo["scripts"] is not System.Text.Json.Nodes.JsonArray scripts
            || scripts.Count > 0)
        {
            return;
        }

        if (!Directory.Exists(configuracion.RutaScripts))
        {
            _registro.Escribir(
                "catalogo.inicial.pendiente",
                "La carpeta configurada no esta disponible.");
            return;
        }

        var generado = new GeneradorCatalogoServidor().Generar(
            configuracion.RutaScripts,
            catalogo.ConjuntoId);
        var guardado = _repositorio.GuardarCatalogo(generado);
        var total = guardado.Catalogo["scripts"] is System.Text.Json.Nodes.JsonArray entradas
            ? entradas.Count
            : 0;
        _registro.Escribir("catalogo.inicial.generado", $"Scripts incluidos: {total}.");
    }
}
