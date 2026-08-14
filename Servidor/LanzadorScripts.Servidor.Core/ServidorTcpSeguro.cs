// (Autor: Alex Roman)
// Descripcion: Atiende conexiones cifradas y autenticadas de clientes Windows.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Principal;
using LanzadorScripts.Protocolo;

namespace LanzadorScripts.Servidor.Core;

public sealed class ServidorTcpSeguro : IAsyncDisposable
{
    private static readonly TimeSpan TiempoAutenticacion = TimeSpan.FromSeconds(10);
    private readonly ConfiguracionServidor _configuracion;
    private readonly ProcesadorSolicitudesServidor _procesador;
    private readonly Action<string, string>? _registrar;
    private readonly CancellationTokenSource _cancelacion = new();
    private readonly ConcurrentDictionary<int, Task> _clientes = new();
    private readonly SemaphoreSlim _limiteClientes;
    private TcpListener? _escuchador;
    private Task? _tareaEscucha;
    private int _secuenciaCliente;

    public ServidorTcpSeguro(
        ConfiguracionServidor configuracion,
        ProcesadorSolicitudesServidor procesador,
        Action<string, string>? registrar = null)
    {
        _configuracion = configuracion;
        _procesador = procesador;
        _registrar = registrar;
        _limiteClientes = new SemaphoreSlim(configuracion.MaximoConexiones);
    }

    public bool Iniciado => _escuchador is not null;

    public void Iniciar()
    {
        if (_escuchador is not null)
        {
            throw new InvalidOperationException("El servidor ya esta iniciado.");
        }

        _escuchador = new TcpListener(IPAddress.Any, _configuracion.Puerto);
        _escuchador.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        _escuchador.Start(_configuracion.MaximoConexiones);
        _tareaEscucha = EscucharAsync(_cancelacion.Token);
        _registrar?.Invoke("servidor.iniciado", $"Puerto {_configuracion.Puerto}.");
    }

    public async ValueTask DisposeAsync()
    {
        _cancelacion.Cancel();
        _escuchador?.Stop();
        if (_tareaEscucha is not null)
        {
            try
            {
                await _tareaEscucha.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or SocketException)
            {
                _registrar?.Invoke("servidor.cierre.aviso", ex.GetType().Name);
            }
        }

        var tareas = _clientes.Values.ToArray();
        try
        {
            await Task.WhenAll(tareas).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            _registrar?.Invoke("servidor.clientes.cierre.aviso", ex.GetType().Name);
        }

        _limiteClientes.Dispose();
        _cancelacion.Dispose();
        _escuchador = null;
    }

    private async Task EscucharAsync(CancellationToken cancelacion)
    {
        while (!cancelacion.IsCancellationRequested)
        {
            TcpClient? cliente = null;
            var reservaAdquirida = false;
            try
            {
                await _limiteClientes.WaitAsync(cancelacion);
                reservaAdquirida = true;
                cliente = await _escuchador!.AcceptTcpClientAsync(cancelacion);
                var id = Interlocked.Increment(ref _secuenciaCliente);
                var tarea = AtenderClienteAsync(cliente, cancelacion);
                cliente = null;
                _clientes[id] = tarea;
                _ = tarea.ContinueWith(
                    tareaCompletada =>
                    {
                        _clientes.TryRemove(id, out var _);
                        _limiteClientes.Release();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                reservaAdquirida = false;
            }
            catch (OperationCanceledException) when (cancelacion.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancelacion.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _registrar?.Invoke("servidor.escucha.error", ex.GetType().Name);
                await Task.Delay(500, cancelacion);
            }
            finally
            {
                cliente?.Dispose();
                if (reservaAdquirida)
                {
                    _limiteClientes.Release();
                }
            }
        }
    }

    private async Task AtenderClienteAsync(TcpClient cliente, CancellationToken cancelacionServidor)
    {
        using (cliente)
        using (var limite = CancellationTokenSource.CreateLinkedTokenSource(cancelacionServidor))
        {
            limite.CancelAfter(TiempoAutenticacion);
            cliente.NoDelay = true;
            try
            {
                await using var seguro = new NegotiateStream(
                    cliente.GetStream(),
                    leaveInnerStreamOpen: false);
                await seguro.AuthenticateAsServerAsync(
                    CredentialCache.DefaultNetworkCredentials,
                    ProtectionLevel.EncryptAndSign,
                    TokenImpersonationLevel.Identification)
                    .WaitAsync(limite.Token);
                if (!seguro.IsAuthenticated || !seguro.IsEncrypted || !seguro.IsSigned)
                {
                    throw new AuthenticationException("El canal no cumple la proteccion requerida.");
                }

                var identidad = seguro.RemoteIdentity?.Name ?? string.Empty;
                var solicitud = await TransporteProtocolo.LeerAsync<SolicitudServidor>(
                    seguro,
                    limite.Token);
                limite.CancelAfter(TimeSpan.FromSeconds(30));
                var respuesta = _procesador.Procesar(identidad, solicitud);
                await TransporteProtocolo.EscribirAsync(seguro, respuesta, limite.Token);
            }
            catch (OperationCanceledException) when (limite.IsCancellationRequested)
            {
                _registrar?.Invoke("servidor.cliente.tiempo_agotado", string.Empty);
            }
            catch (Exception ex) when (ex is AuthenticationException
                or IOException
                or InvalidDataException
                or SocketException)
            {
                _registrar?.Invoke("servidor.cliente.rechazado", ex.GetType().Name);
            }
        }
    }
}
