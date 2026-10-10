// (Autor: Alex Roman)
// Descripcion: Comprueba aislamiento de ejecuciones y rechazo de identificadores ajenos.

using System.Collections;
using System.Net;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using LanzadorScripts.Servicios;
using Xunit;

namespace LanzadorScripts.Pruebas;

public sealed class PruebasPropietarioEjecuciones
{
    [Theory]
    [InlineData("entrada")]
    [InlineData("cancelar")]
    [InlineData("observar")]
    public async Task IdentificadorAjenoEInexistenteSeRechazanIgual(string operacion)
    {
        using var escenario = new Escenario();
        var otro = escenario.Usuario with { NombreUsuario = "OTRO\\usuario", Rol = "admin" };
        var ajeno = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => OperarAsync(escenario, escenario.Id, otro, operacion));
        var ausente = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => OperarAsync(escenario, Guid.NewGuid(), otro, operacion));
        Assert.Equal(ajeno.Message, ausente.Message);
        Assert.False(escenario.Cancelacion.IsCancellationRequested);
        Assert.False(escenario.Entradas.Reader.TryRead(out _));
        Assert.Empty(escenario.Gestor.ObtenerEjecucionesActivas(otro));
    }

    [Theory]
    [InlineData("entrada")]
    [InlineData("cancelar")]
    [InlineData("observar")]
    public async Task PropietarioSinAutorizacionNoControlaSuEjecucion(string operacion)
    {
        using var escenario = new Escenario();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => OperarAsync(escenario, escenario.Id,
            escenario.Usuario with { EstaAutorizado = false }, operacion));
        Assert.Empty(escenario.Gestor.ObtenerEjecucionesActivas(escenario.Usuario with { EstaAutorizado = false }));
    }

    [Fact]
    public async Task NombreCoincidenteConOtroSidNoAutoriza()
    {
        using var escenario = new Escenario();
        // Simula una ejecucion de otra identidad sin suplantar usuarios del equipo.
        escenario.Tipo.GetField("<SidPropietario>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(escenario.Ejecucion, "S-1-5-21-100-200-300-999");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => escenario.Gestor.CancelarAsync(escenario.Id, escenario.Usuario));
        Assert.Empty(escenario.Gestor.ObtenerEjecucionesActivas(escenario.Usuario));
    }

    [Fact]
    public async Task PropietarioConservaEntradaEventosYCancelacion()
    {
        using var escenario = new Escenario();
        var propietario = escenario.Usuario with { NombreUsuario = escenario.Usuario.NombreUsuario.ToUpperInvariant() };
        Assert.Equal(escenario.Id, Assert.Single(escenario.Gestor.ObtenerEjecucionesActivas(propietario)).Id);
        await escenario.Gestor.EnviarEntradaAsync(escenario.Id, "respuesta", propietario);
        Assert.True(escenario.Entradas.Reader.TryRead(out var entrada));
        Assert.Equal("respuesta", entrada);
        await foreach (var evento in escenario.Gestor.ObservarEventosAsync(escenario.Id, propietario))
        {
            Assert.Equal("salida privada", evento.Mensaje);
            break;
        }
        await escenario.Gestor.CancelarAsync(escenario.Id, propietario);
        Assert.True(escenario.Cancelacion.IsCancellationRequested);
    }

    [Theory]
    [InlineData("entrada")]
    [InlineData("cancelar")]
    [InlineData("observar")]
    public async Task AdaptadorNativoResuelveIdentidadSinAceptarOtroPropietario(string operacion)
    {
        using var escenario = new Escenario(propietarioAjeno: true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            if (operacion == "entrada") await escenario.Servidor.EnviarEntradaNativaAsync(escenario.Id, "respuesta");
            else if (operacion == "cancelar") await escenario.Servidor.CancelarEjecucionNativaAsync(escenario.Id);
            else await foreach (var _ in escenario.Servidor.ObservarEjecucionNativaAsync(escenario.Id, CancellationToken.None)) { }
        });
        Assert.Empty(escenario.Servidor.ObtenerEjecucionesActivas());
        Assert.False(escenario.Cancelacion.IsCancellationRequested);
        Assert.False(escenario.Entradas.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("entrada")]
    [InlineData("cancelar")]
    [InlineData("eventos")]
    public async Task HttpNoRevelaNiModificaEjecucionesAjenas(string operacion)
    {
        using var escenario = new Escenario(propietarioAjeno: true, http: true);
        using var cliente = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
            { BaseAddress = escenario.Servidor.UrlBase };
        using var sesion = await cliente.GetAsync("/");
        cliente.DefaultRequestHeaders.Add("X-LanzadorScripts-ApiToken", escenario.Servidor.TokenApiInterno);
        async Task<string> SolicitarAsync(Guid id)
        {
            using var peticion = new HttpRequestMessage(operacion == "eventos" ? HttpMethod.Get : HttpMethod.Post,
                $"/api/ejecuciones/{id}/{operacion}?usuario=OTRO%5Cusuario&rol=admin");
            if (operacion != "eventos") peticion.Content = new StringContent(
                "{\"texto\":\"respuesta\",\"usuario\":\"OTRO\\\\usuario\",\"rol\":\"admin\"}", Encoding.UTF8, "application/json");
            using var respuesta = await cliente.SendAsync(peticion);
            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
            Assert.StartsWith("application/json", respuesta.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);
            return await respuesta.Content.ReadAsStringAsync();
        }
        Assert.Equal(await SolicitarAsync(escenario.Id), await SolicitarAsync(Guid.NewGuid()));
        Assert.False(escenario.Cancelacion.IsCancellationRequested);
        Assert.False(escenario.Entradas.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ClienteVacioNoEjecutaNiModificaConfiguracion()
    {
        using var cliente = new ClienteNativoSinConexion();
        Assert.False((await cliente.ObtenerSesionAsync()).Usuario.EstaAutorizado);
        Assert.Empty(await cliente.ListarScriptsAsync("", ""));
        Assert.Empty(cliente.ObtenerEjecucionesActivas());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cliente.IniciarAsync("ok.ps1"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cliente.ObtenerPermisosAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cliente.PublicarCatalogoAsync(["ok.ps1"]));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cliente.AplicarExecutionPolicyAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cliente.EnviarEntradaAsync(Guid.NewGuid(), "x"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cliente.CancelarAsync(Guid.NewGuid()));
        Assert.Throws<UnauthorizedAccessException>(() => cliente.ObservarAsync(Guid.NewGuid()));
    }

    private static async Task OperarAsync(Escenario escenario, Guid id, UsuarioCliente usuario, string operacion)
    {
        if (operacion == "entrada") await escenario.Gestor.EnviarEntradaAsync(id, "respuesta", usuario);
        else if (operacion == "cancelar") await escenario.Gestor.CancelarAsync(id, usuario);
        else await foreach (var _ in escenario.Gestor.ObservarEventosAsync(id, usuario)) break;
    }

    private sealed class Escenario : IDisposable
    {
        private readonly EntornoPruebas _entorno = EntornoPruebas.Crear();
        public ServidorLocalWeb Servidor { get; }
        public GestorEjecucionesWeb Gestor { get; }
        public UsuarioCliente Usuario { get; }
        public Type Tipo { get; } = typeof(GestorEjecucionesWeb).GetNestedType("EjecucionWeb", BindingFlags.NonPublic)!;
        public object Ejecucion { get; }
        public Guid Id => (Guid)Tipo.GetProperty("Id")!.GetValue(Ejecucion)!;
        public CancellationTokenSource Cancelacion => (CancellationTokenSource)Tipo.GetProperty("Cancelacion")!.GetValue(Ejecucion)!;
        public Channel<string> Entradas => (Channel<string>)Tipo.GetProperty("Entradas")!.GetValue(Ejecucion)!;

        public Escenario(bool propietarioAjeno = false, bool http = false)
        {
            using var identidad = WindowsIdentity.GetCurrent();
            Usuario = new UsuarioCliente(identidad.Name, "admin", 5, true);
            _entorno.GuardarPermisosProtegidos(new JsonObject
            {
                ["usuarios"] = new JsonArray(new JsonObject { ["id"] = "prueba", ["nombreUsuario"] = identidad.Name,
                    ["rol"] = "admin", ["maxScriptsSimultaneos"] = 5, ["carpetasPermitidas"] = new JsonArray() })
            });
            Servidor = http ? ServidorLocalWeb.IniciarParaPruebas(_entorno.CrearConfiguracion(), _entorno.Artefactos)
                : ServidorLocalWeb.CrearMotorNativoParaPruebas(_entorno.CrearConfiguracion(), _entorno.Artefactos);
            Gestor = (GestorEjecucionesWeb)typeof(ServidorLocalWeb).GetField("_gestorEjecuciones", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Servidor)!;
            var script = new ServicioValidacionScripts().ValidarRutaConocida(_entorno.Raiz,
                Path.Combine(_entorno.Raiz, "ok.ps1"), "ok.ps1", "ok.ps1", "powershell").Script!;
            var catalogo = new ServicioCatalogoScripts(_entorno.Artefactos).Crear([script], [script.Id], _entorno.ConjuntoId);
            // Inserta una ejecucion sin proceso para probar controles sin ejecutar scripts reales.
            Ejecucion = Activator.CreateInstance(Tipo, script, "", propietarioAjeno ? Usuario with { NombreUsuario = "OTRO\\usuario" } : Usuario,
                true, new JsonObject(), catalogo, false, new string('A', 64))!;
            var ejecuciones = (IDictionary)typeof(GestorEjecucionesWeb).GetField("_ejecuciones", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Gestor)!;
            ejecuciones.Add(Id, Ejecucion);
            Tipo.GetMethod("AgregarEvento")!.Invoke(Ejecucion, ["info", "salida privada", null, false]);
        }

        public void Dispose() { Servidor.Dispose(); _entorno.Dispose(); }
    }
}
