// (Autor: Alex Roman)
// Descripcion: Atiende la consola administrativa mediante un canal local limitado a administradores.

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using LanzadorScripts.Protocolo;

namespace LanzadorScripts.Servidor.Core;

public sealed class ServidorAdministracionLocal : IAsyncDisposable
{
    private const int MaximoInstancias = 8;
    private readonly ProcesadorSolicitudesServidor _procesador;
    private readonly Action<string, string>? _registrar;
    private readonly CancellationTokenSource _cancelacion = new();
    private readonly ConcurrentDictionary<int, Task> _clientes = new();
    private Task? _tareaEscucha;
    private int _secuenciaCliente;

    public ServidorAdministracionLocal(
        ProcesadorSolicitudesServidor procesador,
        Action<string, string>? registrar = null)
    {
        _procesador = procesador;
        _registrar = registrar;
    }

    public void Iniciar()
    {
        if (_tareaEscucha is not null)
        {
            throw new InvalidOperationException("El canal administrativo local ya esta iniciado.");
        }

        _tareaEscucha = EscucharAsync(_cancelacion.Token);
        _registrar?.Invoke("administracion.local.iniciada", CanalAdministracionLocal.Nombre);
    }

    public async ValueTask DisposeAsync()
    {
        _cancelacion.Cancel();
        if (_tareaEscucha is not null)
        {
            try
            {
                await _tareaEscucha.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                _registrar?.Invoke("administracion.local.cierre.aviso", ex.GetType().Name);
            }
        }

        try
        {
            await Task.WhenAll(_clientes.Values).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            _registrar?.Invoke("administracion.local.clientes.aviso", ex.GetType().Name);
        }

        _cancelacion.Dispose();
    }

    private async Task EscucharAsync(CancellationToken cancelacion)
    {
        while (!cancelacion.IsCancellationRequested)
        {
            NamedPipeServerStream? canal = null;
            try
            {
                canal = CrearCanal();
                await canal.WaitForConnectionAsync(cancelacion);
                var id = Interlocked.Increment(ref _secuenciaCliente);
                var tarea = AtenderAsync(canal, cancelacion);
                canal = null;
                _clientes[id] = tarea;
                _ = tarea.ContinueWith(
                    tareaCompletada => _clientes.TryRemove(id, out var _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (cancelacion.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _registrar?.Invoke("administracion.local.escucha.error", ex.GetType().Name);
                await Task.Delay(500, cancelacion);
            }
            finally
            {
                canal?.Dispose();
            }
        }
    }

    private async Task AtenderAsync(NamedPipeServerStream canal, CancellationToken cancelacionServidor)
    {
        await using (canal)
        using (var limite = CancellationTokenSource.CreateLinkedTokenSource(cancelacionServidor))
        {
            limite.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var solicitud = await TransporteProtocolo.LeerAsync<SolicitudServidor>(
                    canal,
                    limite.Token);
                // Windows expone la identidad del cliente despues de recibir su primera escritura.
                var cuenta = IdentidadClienteCanalLocal.ObtenerCuenta(canal);
                var respuesta = _procesador.Procesar(cuenta, solicitud);
                await TransporteProtocolo.EscribirAsync(canal, respuesta, limite.Token);
            }
            catch (OperationCanceledException) when (limite.IsCancellationRequested)
            {
                _registrar?.Invoke("administracion.local.tiempo_agotado", string.Empty);
            }
            catch (Exception ex) when (ex is IOException
                or InvalidDataException
                or JsonException
                or UnauthorizedAccessException)
            {
                _registrar?.Invoke("administracion.local.rechazada", ex.GetType().Name);
            }
        }
    }

    private static NamedPipeServerStream CrearCanal()
    {
        // Permite el canal solamente a SYSTEM y administradores locales elevados.
        var sistema = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administradores = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var seguridad = new PipeSecurity();
        seguridad.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        seguridad.SetOwner(sistema);
        seguridad.AddAccessRule(new PipeAccessRule(
            sistema,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        seguridad.AddAccessRule(new PipeAccessRule(
            administradores,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            CanalAdministracionLocal.Nombre,
            PipeDirection.InOut,
            MaximoInstancias,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            0,
            0,
            seguridad,
            HandleInheritability.None,
            0);
    }
}
