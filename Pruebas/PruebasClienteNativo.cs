// (Autor: Alex Roman)
// Descripcion: Verifica autorizacion, entrada interactiva y navegacion del cliente WPF.

using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using LanzadorScripts.ModelosVista;
using LanzadorScripts.Servicios;
using Xunit;

namespace LanzadorScripts.Pruebas;

public sealed class PruebasClienteNativo
{
    [Fact]
    public void MotorNativoNoAbrePuertoNiCargaNavegador()
    {
        using var motor = ServidorLocalWeb.CrearMotorNativo();
        var escuchador = (System.Net.HttpListener)typeof(ServidorLocalWeb).GetField("_escuchador", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(motor)!;
        Assert.False(escuchador.IsListening);
        Assert.DoesNotContain(typeof(ServicioClienteNativo).Assembly.GetReferencedAssemblies(), a => a.Name!.Contains("WebView", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(ServicioClienteNativo).Assembly.GetManifestResourceNames(), n => n.StartsWith("ClienteWeb", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UsuarioNoRegistradoNoEjecutaNiAdministra()
    {
        using var entorno = EntornoPruebas.Crear();
        using var cliente = CrearCliente(entorno);
        var sesion = await cliente.ObtenerSesionAsync();
        Assert.False(sesion.Usuario.EstaAutorizado);
        Assert.Empty(await cliente.ListarScriptsAsync("", ""));
        var resultado = await cliente.IniciarAsync("ok.ps1");
        Assert.False(resultado.Exito);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cliente.ObtenerPermisosAsync());
    }

    [Fact]
    public async Task UsuarioNominalNoPublicaCatalogoNiPermisos()
    {
        using var entorno = EntornoPruebas.Crear();
        Autorizar(entorno, "nominal");
        using var cliente = CrearCliente(entorno);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cliente.ObtenerCatalogoAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cliente.PublicarCatalogoAsync(["ok.ps1"]));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cliente.CambiarModoDesarrolloAsync(true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cliente.AplicarExecutionPolicyAsync());
    }

    [Fact]
    public async Task RevocarAdministradorBloqueaCambiosSinEsperarAlCache()
    {
        using var entorno = EntornoPruebas.Crear();
        Autorizar(entorno);
        using var cliente = CrearCliente(entorno);
        Assert.True((await cliente.ObtenerSesionAsync()).Usuario.Rol == "admin");
        Autorizar(entorno, "nominal");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cliente.CambiarModoDesarrolloAsync(true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cliente.ObtenerPermisosAsync());
    }

    [Fact]
    public async Task BusquedaRecorreSoloCarpetasAutorizadas()
    {
        using var entorno = EntornoPruebas.Crear();
        Autorizar(entorno, "nominal");
        entorno.GuardarCatalogo(["ok.ps1", "sub/ok.cmd"]);
        using var cliente = CrearCliente(entorno);
        var scripts = await cliente.ListarScriptsAsync("", "ok");
        Assert.Contains(scripts, s => s.Id == "ok.ps1");
        Assert.DoesNotContain(scripts, s => s.Id == "sub/ok.cmd");
    }

    [Theory]
    [InlineData("../fuera.ps1")]
    [InlineData("PERMISOS/bloqueado.ps1")]
    [InlineData("bad&name.ps1")]
    public async Task RutaManipuladaSeBloquea(string id)
    {
        using var entorno = EntornoPruebas.Crear();
        Autorizar(entorno);
        using var cliente = CrearCliente(entorno);
        Assert.False((await cliente.IniciarAsync(id)).Exito);
        Assert.Empty(cliente.ObtenerEjecucionesActivas());
    }

    [Fact]
    public async Task ScriptModificadoNoSeEjecuta()
    {
        using var entorno = EntornoPruebas.Crear();
        Autorizar(entorno);
        entorno.GuardarCatalogo(["ok.ps1"]);
        File.AppendAllText(Path.Combine(entorno.Raiz, "ok.ps1"), "\nWrite-Output 'modificado'");
        using var cliente = CrearCliente(entorno);
        var resultado = await cliente.IniciarAsync("ok.ps1");
        Assert.False(resultado.Exito);
        Assert.Contains("modificado", resultado.Mensaje, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EntradaTempranaYLíneaVaciaLleganAlScript()
    {
        using var entorno = EntornoPruebas.Crear();
        Autorizar(entorno);
        File.WriteAllText(Path.Combine(entorno.Raiz, "interactivo.ps1"),
            "# (Autor: Alex Roman)\n# Descripcion: Recibe una respuesta y una pausa de prueba.\n$v=Read-Host 'Respuesta'; Write-Output ('RECIBIDO='+$v); Pause; Write-Output 'PAUSA_OK'");
        entorno.GuardarCatalogo(["interactivo.ps1"]);
        using var cliente = CrearCliente(entorno);
        var inicio = await cliente.IniciarAsync("interactivo.ps1");
        Assert.True(inicio.Exito, inicio.Mensaje);
        await cliente.EnviarEntradaAsync(inicio.Datos, "aena \u00f1 ' & literal");
        await cliente.EnviarEntradaAsync(inicio.Datos, "");
        using var tiempo = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var salida = new StringBuilder();
        await foreach (var evento in cliente.ObservarAsync(inicio.Datos, tiempo.Token)) salida.Append(evento.Mensaje);
        Assert.Contains("RECIBIDO=aena", salida.ToString());
        Assert.Contains("' & literal", salida.ToString());
        Assert.Contains("PAUSA_OK", salida.ToString());
        Assert.Empty(cliente.ObtenerEjecucionesActivas());
        Assert.False(Directory.Exists(Path.Combine(entorno.Raiz, "Logs")) && Directory.GetFiles(Path.Combine(entorno.Raiz, "Logs"), "*.log").Length > 0);
    }

    [Fact]
    public async Task ParametroObligatorioRecibeRespuesta()
    {
        using var entorno = EntornoPruebas.Crear();
        Autorizar(entorno);
        File.WriteAllText(Path.Combine(entorno.Raiz, "parametro.ps1"),
            "# (Autor: Alex Roman)\n# Descripcion: Comprueba un parametro obligatorio.\nparam([Parameter(Mandatory=$true)][string]$Nombre)\nWrite-Output ('PARAMETRO='+$Nombre)");
        entorno.GuardarCatalogo(["parametro.ps1"]);
        using var cliente = CrearCliente(entorno);
        var inicio = await cliente.IniciarAsync("parametro.ps1");
        Assert.True(inicio.Exito, inicio.Mensaje);
        await cliente.EnviarEntradaAsync(inicio.Datos, "prueba");
        using var tiempo = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var salida = new StringBuilder();
        await foreach (var evento in cliente.ObservarAsync(inicio.Datos, tiempo.Token)) salida.Append(evento.Mensaje);
        Assert.Contains("PARAMETRO=prueba", salida.ToString());
    }

    [Fact]
    public void SalidaTruncadaConservaResultadoFinalEnPosicionNueva()
    {
        using var entorno = EntornoPruebas.Crear();
        var tipo = typeof(GestorEjecucionesWeb).GetNestedType("EjecucionWeb", BindingFlags.NonPublic)!;
        var script = new ServicioValidacionScripts().ValidarRutaConocida(
            entorno.Raiz, Path.Combine(entorno.Raiz, "ok.ps1"), "ok.ps1", "ok.ps1", "powershell").Script!;
        var catalogo = new ServicioCatalogoScripts(entorno.Artefactos).Crear([script], [script.Id], entorno.ConjuntoId);
        using var ejecucion = (IDisposable)Activator.CreateInstance(tipo, script, "", new UsuarioCliente("prueba", "admin", 5, true),
            true, new JsonObject(), catalogo, false, new string('A', 64))!;
        var agregar = tipo.GetMethod("AgregarEvento")!;
        for (var indice = 0; indice < 5001; indice++) agregar.Invoke(ejecucion, ["info", "salida", null, false]);
        var total = (int)tipo.GetProperty("TotalEventos")!.GetValue(ejecucion)!;
        agregar.Invoke(ejecucion, ["exito", "RESULTADO_FINAL", null, true]);
        agregar.Invoke(ejecucion, ["error", "AUDITORIA_PENDIENTE", null, true]);
        var nuevos = (IReadOnlyList<EventoCliente>)tipo.GetMethod("ObtenerEventosDesde")!.Invoke(ejecucion, [total])!;
        Assert.Equal(new[] { "RESULTADO_FINAL", "AUDITORIA_PENDIENTE" }, nuevos.Select(e => e.Mensaje));
        Assert.True((int)tipo.GetProperty("TotalEventos")!.GetValue(ejecucion)! <= 5003);
    }

    [Fact]
    public async Task CancelacionTempranaFinalizaSinEsperarAlScript()
    {
        using var entorno = EntornoPruebas.Crear();
        Autorizar(entorno);
        File.WriteAllText(Path.Combine(entorno.Raiz, "cancelar.ps1"),
            "# (Autor: Alex Roman)\n# Descripcion: Verifica la cancelacion durante el arranque.\nStart-Sleep -Seconds 30; Write-Output 'NO_DEBE_TERMINAR'");
        entorno.GuardarCatalogo(["cancelar.ps1"]);
        using var cliente = CrearCliente(entorno);
        for (var intento = 0; intento < 3; intento++)
        {
            var inicio = await cliente.IniciarAsync("cancelar.ps1");
            Assert.True(inicio.Exito, inicio.Mensaje);
            await cliente.CancelarAsync(inicio.Datos);
            using var tiempo = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var salida = new StringBuilder();
            await foreach (var evento in cliente.ObservarAsync(inicio.Datos, tiempo.Token)) salida.Append(evento.Mensaje);
            Assert.Contains("Cancelada por el usuario", salida.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NO_DEBE_TERMINAR", salida.ToString());
            Assert.Empty(cliente.ObtenerEjecucionesActivas());
        }
    }

    [Fact]
    public async Task BrokerCanceladoNoSolicitaElevacion()
    {
        using var entorno = EntornoPruebas.Crear();
        var validacion = new ServicioValidacionScripts().ValidarRutaConocida(
            entorno.Raiz, Path.Combine(entorno.Raiz, "ok.ps1"), "ok.ps1", "ok.ps1", "powershell");
        Assert.True(validacion.EsValido);
        using var cancelacion = new CancellationTokenSource();
        cancelacion.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in new ServicioBrokerElevado().EjecutarAsync(validacion.Script!, true, cancelacion.Token)) { }
        });
    }

    [Fact]
    public async Task BrokerRecibeEntradaAutenticadaSinNuevaEjecucion()
    {
        using var entorno = EntornoPruebas.Crear();
        var ruta = Path.Combine(entorno.Raiz, "broker.ps1");
        File.WriteAllText(ruta, "# (Autor: Alex Roman)\n# Descripcion: Prueba de entrada del broker.\n$v=Read-Host 'Respuesta'; Write-Output ('BROKER='+$v)");
        var nombre = "LanzadorScripts_PruebaBroker_" + Guid.NewGuid().ToString("N");
        var token = Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(nombre, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var broker = Task.Run(() => ServicioBrokerElevado.EjecutarModoBroker(["--broker-elevado", "--pipe", nombre, "--token", token]));
        using var tiempo = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await pipe.WaitForConnectionAsync(tiempo.Token);
        await using var escritor = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var lector = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await escritor.WriteLineAsync(JsonSerializer.Serialize(new ComandoBrokerElevado("ejecutar", token, "broker.ps1", "broker.ps1", "powershell", entorno.Raiz, ruta, true)));
        await escritor.WriteLineAsync(JsonSerializer.Serialize(new ComandoBrokerElevado("entrada", "incorrecto", "", "", "", "", "", false, "NO_AUTORIZADO")));
        await escritor.WriteLineAsync(JsonSerializer.Serialize(new ComandoBrokerElevado("entrada", token, "", "", "", "", "", false, "respuesta valida")));
        var salida = new StringBuilder();
        while (true)
        {
            var linea = await lector.ReadLineAsync(tiempo.Token);
            Assert.NotNull(linea);
            var evento = JsonSerializer.Deserialize<EventoBrokerElevado>(linea)!;
            salida.Append(evento.Mensaje);
            if (evento.Finalizado) break;
        }
        Assert.True(await broker.WaitAsync(tiempo.Token) == 0, salida.ToString());
        Assert.Contains("BROKER=respuesta valida", salida.ToString());
        Assert.DoesNotContain("NO_AUTORIZADO", salida.ToString());
    }

    [Fact]
    public async Task BrokerLimitaTamanoDeMensajes()
    {
        using var lector = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 131073))));
        await Assert.ThrowsAsync<InvalidDataException>(() => ServicioBrokerElevado.LeerLineaLimitadaAsync(lector, CancellationToken.None));
    }

    [Fact]
    public async Task PermisosNativosConservanCarpetasYElevacion()
    {
        using var entorno = EntornoPruebas.Crear();
        Autorizar(entorno);
        entorno.GuardarCatalogo(["ok.ps1"]);
        using var cliente = CrearCliente(entorno);
        var permisos = await cliente.ObtenerPermisosAsync();
        var usuarios = permisos.Usuarios.Select(u => u with { CarpetasPermitidas = new[] { "sub" } }).ToArray();
        await cliente.GuardarPermisosAsync(permisos with { Usuarios = usuarios, ScriptsElevadosPermitidos = new[] { "ok.ps1" }, PermitirExecutionPolicyBypass = true });
        var recuperados = await cliente.ObtenerPermisosAsync();
        Assert.Equal(new[] { "sub" }, recuperados.Usuarios.Single().CarpetasPermitidas);
        Assert.Equal(new[] { "ok.ps1" }, recuperados.ScriptsElevadosPermitidos);
        Assert.True(recuperados.PermitirExecutionPolicyBypass);
    }

    [Fact]
    public async Task UsuariosDuplicadosSeRechazan()
    {
        using var entorno = EntornoPruebas.Crear();
        Autorizar(entorno);
        using var cliente = CrearCliente(entorno);
        var permisos = await cliente.ObtenerPermisosAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => cliente.GuardarPermisosAsync(permisos with { Usuarios = new[] { permisos.Usuarios[0], permisos.Usuarios[0] } }));
    }

    [Fact]
    public async Task NavegarYBuscarNoDestruyeConsolas()
    {
        using var cliente = new ClienteNativoSimulado();
        using var modelo = new ClienteNativoModelo(cliente);
        await modelo.InicializarAsync();
        await modelo.EjecutarAsync(ClienteNativoSimulado.Script);
        var consola = modelo.Consolas.Single();
        await modelo.AbrirCarpetaAsync("sub");
        await modelo.RefrescarAsync();
        Assert.Same(consola, modelo.Consolas.Single());
        Assert.True(consola.Activa);
    }

    [Fact]
    public async Task RespuestaVaciaNoSeDescarta()
    {
        using var cliente = new ClienteNativoSimulado();
        using var modelo = new ClienteNativoModelo(cliente);
        await modelo.EjecutarAsync(ClienteNativoSimulado.Script);
        var consola = modelo.Consolas.Single();
        await modelo.EnviarAsync(consola);
        Assert.Equal(new[] { "" }, cliente.Entradas);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    public async Task EntradaMultilineaSeRechaza(string texto)
    {
        using var cliente = new ClienteNativoSimulado();
        using var modelo = new ClienteNativoModelo(cliente);
        await modelo.EjecutarAsync(ClienteNativoSimulado.Script);
        modelo.Consolas.Single().Entrada = texto;
        await modelo.EnviarAsync(modelo.Consolas.Single());
        Assert.Empty(cliente.Entradas);
    }

    [Fact]
    public async Task EntradaLargaSeRechaza()
    {
        using var cliente = new ClienteNativoSimulado();
        using var modelo = new ClienteNativoModelo(cliente);
        await modelo.EjecutarAsync(ClienteNativoSimulado.Script);
        modelo.Consolas.Single().Entrada = new string('a', 8193);
        await modelo.EnviarAsync(modelo.Consolas.Single());
        Assert.Empty(cliente.Entradas);
    }

    private static ServicioClienteNativo CrearCliente(EntornoPruebas entorno) => new(ServidorLocalWeb.CrearMotorNativoParaPruebas(entorno.CrearConfiguracion(), entorno.Artefactos));

    private static void Autorizar(EntornoPruebas entorno, string rol = "admin")
    {
        entorno.GuardarPermisosProtegidos(new JsonObject
        {
            ["rolUsuarioActual"] = "nominal", ["maxScriptsSimultaneos"] = 5,
            ["usuarios"] = new JsonArray(new JsonObject { ["id"] = "prueba", ["nombreUsuario"] = WindowsIdentity.GetCurrent().Name,
                ["rol"] = rol, ["maxScriptsSimultaneos"] = 5, ["carpetasPermitidas"] = new JsonArray() }),
            ["scriptsAdmin"] = new JsonArray(), ["seguridadScripts"] = new JsonObject { ["scriptsElevadosPermitidos"] = new JsonArray(), ["permitirExecutionPolicyBypass"] = true }
        });
    }
}

internal sealed class ClienteNativoSimulado : IClienteNativo
{
    private readonly Channel<EventoCliente> _eventos = Channel.CreateUnbounded<EventoCliente>();
    public static ElementoScriptNativo Script { get; } = new("prueba.ps1", "Prueba interactiva.ps1", "powershell", false, "", false, "");
    public List<string> Entradas { get; } = [];
    public Task<SesionClienteNativa> ObtenerSesionAsync(CancellationToken cancelacion = default) => Task.FromResult(new SesionClienteNativa(new UsuarioCliente("DOMINIO\\usuario", "admin", 5, true), "", "\\\\servidor\\scripts", false));
    public Task<IReadOnlyList<ElementoScriptNativo>> ListarScriptsAsync(string carpeta, string buscar, CancellationToken cancelacion = default) => Task.FromResult<IReadOnlyList<ElementoScriptNativo>>([Script]);
    public Task<PermisosClienteNativo> ObtenerPermisosAsync(CancellationToken cancelacion = default) => Task.FromResult(new PermisosClienteNativo([new UsuarioPermisoNativo("1", "usuario", "admin", 5, [])], [], [], false));
    public Task GuardarPermisosAsync(PermisosClienteNativo permisos, CancellationToken cancelacion = default) => Task.CompletedTask;
    public Task<IReadOnlyList<string>> ObtenerCarpetasAsync(CancellationToken cancelacion = default) => Task.FromResult<IReadOnlyList<string>>(["sub"]);
    public Task<CatalogoClienteNativo> ObtenerCatalogoAsync(CancellationToken cancelacion = default) => Task.FromResult(new CatalogoClienteNativo(true, "", [new EstadoCatalogoScriptCliente(Script.Id, "powershell", 100, new string('A', 64), "autorizado", true)]));
    public Task PublicarCatalogoAsync(IReadOnlyList<string> seleccionados, CancellationToken cancelacion = default) => Task.CompletedTask;
    public Task CambiarModoDesarrolloAsync(bool activo, CancellationToken cancelacion = default) => Task.CompletedTask;
    public Task<ResultadoExecutionPolicy> AplicarExecutionPolicyAsync(CancellationToken cancelacion = default) => Task.FromResult(new ResultadoExecutionPolicy(true, "Prueba"));
    public Task<DiagnosticoEjecucionScript> DiagnosticarAsync(string scriptId, CancellationToken cancelacion = default) => throw new NotSupportedException();
    public Task<ResultadoOperacionNativa<Guid>> IniciarAsync(string scriptId, CancellationToken cancelacion = default) => Task.FromResult(new ResultadoOperacionNativa<Guid>(true, Guid.NewGuid(), ""));
    public Task EnviarEntradaAsync(Guid ejecucionId, string texto) { Entradas.Add(texto); return Task.CompletedTask; }
    public Task CancelarAsync(Guid ejecucionId) { _eventos.Writer.TryWrite(new EventoCliente("fin", "", null, true)); return Task.CompletedTask; }
    public IAsyncEnumerable<EventoCliente> ObservarAsync(Guid ejecucionId, CancellationToken cancelacion = default) => _eventos.Reader.ReadAllAsync(cancelacion);
    public IReadOnlyList<EjecucionActivaResumen> ObtenerEjecucionesActivas() => [];
    public void Dispose() => _eventos.Writer.TryComplete();
}
