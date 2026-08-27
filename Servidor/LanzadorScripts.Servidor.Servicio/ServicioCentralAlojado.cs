// (Autor: Alex Roman)
// Descripcion: Inicia y detiene los componentes del servicio central.

using System.Security.Cryptography;
using LanzadorScripts.Servidor.Core;
using Microsoft.Extensions.Hosting;

namespace LanzadorScripts.Servidor.Servicio;

public sealed class ServicioCentralAlojado : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan EsperaReintentoSpnInicial = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EsperaReintentoSpnMaxima = TimeSpan.FromMinutes(5);

    public const string NombreServicio = "LanzadorScriptsServidor";
    public const string NombreVisible = "LanzadorScripts Servidor";

    private readonly RutasServidor _rutas;
    private readonly AlmacenConfiguracionServidor _almacenConfiguracion;
    private readonly RegistroServidor _registro;
    private readonly IRegistroSpnServidor _registroSpn;
    private RepositorioServidor? _repositorio;
    private ServidorTcpSeguro? _servidor;
    private ServidorAdministracionLocal? _administracionLocal;
    private CancellationTokenSource? _cancelacionSpn;
    private Task? _tareaSpn;
    private volatile EstadoAutenticacionServidor _estadoAutenticacion =
        EstadoAutenticacionServidor.Pendiente;
    private int _cierreIniciado;

    public ServicioCentralAlojado(
        RutasServidor rutas,
        AlmacenConfiguracionServidor almacenConfiguracion,
        RegistroServidor registro,
        IRegistroSpnServidor registroSpn)
    {
        _rutas = rutas;
        _almacenConfiguracion = almacenConfiguracion;
        _registro = registro;
        _registroSpn = registroSpn;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Volatile.Write(ref _cierreIniciado, 0);
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

            var procesador = new ProcesadorSolicitudesServidor(
                _repositorio,
                () => _estadoAutenticacion);
            _servidor = new ServidorTcpSeguro(configuracion, procesador, _registro.Escribir);
            _servidor.Iniciar();
            _administracionLocal = new ServidorAdministracionLocal(
                procesador,
                _registro.Escribir);
            _administracionLocal.Iniciar();
            _cancelacionSpn = new CancellationTokenSource();
            _tareaSpn = Task.Run(
                () => MantenerRegistroSpnAsync(_cancelacionSpn.Token),
                CancellationToken.None);
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
        if (Interlocked.Exchange(ref _cierreIniciado, 1) != 0)
        {
            return;
        }

        var registroSpnDetenido = await DetenerReintentosSpnAsync();
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

        if (registroSpnDetenido && _estadoAutenticacion.Preparada)
        {
            try
            {
                var resultado = await Task.Run(_registroSpn.Eliminar, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                _registro.Escribir(
                    resultado.Exito ? "servidor.spn.eliminado" : "servidor.spn.eliminacion.error",
                    resultado.Mensaje);
                _estadoAutenticacion = EstadoAutenticacionServidor.Pendiente;
            }
            catch (TimeoutException)
            {
                _registro.Escribir(
                    "servidor.spn.eliminacion.error",
                    "La eliminacion del SPN supero el tiempo permitido.");
            }
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

    private async Task MantenerRegistroSpnAsync(CancellationToken cancelacion)
    {
        var espera = EsperaReintentoSpnInicial;
        while (!cancelacion.IsCancellationRequested)
        {
            var resultado = _registroSpn.Registrar();
            _estadoAutenticacion = new EstadoAutenticacionServidor(
                resultado.Exito,
                resultado.SpnPrincipal,
                resultado.Mensaje);
            _registro.Escribir(
                resultado.Exito ? "servidor.spn.registrado" : "servidor.spn.registro.error",
                resultado.Mensaje);
            if (resultado.Exito)
            {
                return;
            }

            await Task.Delay(espera, cancelacion);
            espera = TimeSpan.FromTicks(Math.Min(
                espera.Ticks * 2,
                EsperaReintentoSpnMaxima.Ticks));
        }
    }

    private async Task<bool> DetenerReintentosSpnAsync()
    {
        if (_cancelacionSpn is null || _tareaSpn is null)
        {
            return true;
        }

        _cancelacionSpn.Cancel();
        try
        {
            await _tareaSpn.WaitAsync(TimeSpan.FromSeconds(5));
            return true;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (TimeoutException)
        {
            _registro.Escribir("servidor.spn.cierre.aviso", "El registro SPN no finalizo a tiempo.");
            return false;
        }
        catch (Exception ex)
        {
            _registro.Escribir("servidor.spn.cierre.aviso", ex.GetType().Name);
            return true;
        }
        finally
        {
            _cancelacionSpn.Dispose();
            _cancelacionSpn = null;
            _tareaSpn = null;
        }
    }
}
