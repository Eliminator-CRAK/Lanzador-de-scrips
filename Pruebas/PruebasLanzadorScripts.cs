// (Autor: Alex Roman)
// Descripcion: Pruebas automatizadas de seguridad, permisos y ejecucion.

using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using LanzadorScripts.Modelos;
using LanzadorScripts.Servicios;
using Xunit;

namespace LanzadorScripts.Pruebas;

public sealed class PruebasLanzadorScripts
{
    [Fact]
    public void ValidadorBloqueaRutasNoPermitidas()
    {
        using var entorno = EntornoPruebas.Crear();
        var validador = new ServicioValidacionScripts();

        Assert.True(validador.ValidarScriptParaEjecucion(entorno.Raiz, "ok.ps1").EsValido);
        Assert.True(validador.ValidarScriptParaEjecucion(entorno.Raiz, "sub/ok.cmd").EsValido);
        Assert.Equal(CodigoValidacionScript.IdentificadorNoPermitido, validador.ValidarScriptParaEjecucion(entorno.Raiz, "../fuera.ps1").Codigo);
        Assert.Equal(CodigoValidacionScript.CarpetaExcluida, validador.ValidarScriptParaEjecucion(entorno.Raiz, "PERMISOS/bloqueado.ps1").Codigo);
        Assert.Equal(CodigoValidacionScript.ExtensionNoPermitida, validador.ValidarScriptParaEjecucion(entorno.Raiz, "texto.txt").Codigo);
        Assert.Equal(CodigoValidacionScript.MetacaracterPeligroso, validador.ValidarScriptParaEjecucion(entorno.Raiz, "bad&name.ps1").Codigo);

        var descubiertos = validador.DescubrirScripts(entorno.Raiz);
        Assert.Contains(descubiertos, script => script.Id == "ok.ps1");
        Assert.Contains(descubiertos, script => script.Id == "sub/ok.cmd");
        Assert.DoesNotContain(descubiertos, script => script.Id.Contains("PERMISOS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConfiguracionRechazaAdminShares()
    {
        var validador = new ServicioValidacionScripts();

        Assert.False(validador.ValidarConfiguracionBasica(@"\\SERVIDOR\C$\REPO", @"\\SERVIDOR\REPO\PERMISOS\permisos.json").EsValida);
        Assert.False(validador.ValidarConfiguracionBasica(@"\\SERVIDOR\REPO", @"\\SERVIDOR\D$\PERMISOS\permisos.json").EsValida);
        Assert.True(validador.ValidarConfiguracionBasica(@"\\SERVIDOR\REPO", @"\\SERVIDOR\REPO\PERMISOS\permisos.json").EsValida);
    }

    [Fact]
    public void SeguridadBloqueaScriptsSinFirmaOHash()
    {
        using var entorno = EntornoPruebas.Crear();
        var validador = new ServicioValidacionScripts();
        var seguridad = new ServicioSeguridadScripts();
        var permisosVacios = CrearPermisosBase();

        var ps1 = validador.ValidarScriptParaEjecucion(entorno.Raiz, "ok.ps1").Script!;
        var cmd = validador.ValidarScriptParaEjecucion(entorno.Raiz, "sub/ok.cmd").Script!;

        Assert.False(seguridad.Diagnosticar(ps1, permisosVacios).Permitido);
        Assert.False(seguridad.Diagnosticar(cmd, permisosVacios).Permitido);

        var permisosHash = CrearPermisosBase();
        permisosHash["seguridadScripts"]!["hashesBatchPermitidos"] = new JsonArray
        {
            new JsonObject
            {
                ["scriptId"] = "sub/ok.cmd",
                ["sha256"] = ServicioSeguridadScripts.CalcularSha256(cmd.RutaCompleta)
            }
        };

        Assert.True(seguridad.Diagnosticar(cmd, permisosHash).Permitido);
        Assert.True(seguridad.Diagnosticar(cmd, permisosVacios, modoDesarrolloFirmas: true).Permitido);
    }

    [Fact]
    public void ContenedorFirmadoRechazaManipulacion()
    {
        using var rsa = RSA.Create(3072);
        var servicio = new ServicioCifradoAplicacion(rsa, rsa, "Pruebas");
        var firmado = servicio.CifrarTexto("permisos", "{\"ok\":true}");

        Assert.True(servicio.IntentarDescifrarTexto("permisos", firmado, out var claro));
        Assert.Contains("\"ok\":true", claro, StringComparison.Ordinal);
        var manipulado = JsonNode.Parse(firmado)!.AsObject();
        manipulado["Datos"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"ok\":false}"));
        Assert.False(servicio.IntentarDescifrarTexto("permisos", manipulado.ToJsonString(), out _));
        Assert.False(servicio.IntentarDescifrarTexto("configuracion-exportada", firmado, out _));
    }

    [Fact]
    public void PaqueteConfiguracionFirmadoImportaYRechazaAdminShare()
    {
        using var entorno = EntornoPruebas.Crear();
        using var rsa = RSA.Create(3072);
        var firma = new ServicioCifradoAplicacion(rsa, rsa, "Pruebas");
        var servicio = new ServicioPaquetesConfiguracion(firma);
        var configuracion = new ConfiguracionLanzador
        {
            RutaScripts = entorno.Raiz,
            RutaPermisos = Path.Combine(entorno.Raiz, "PERMISOS", "permisos.json")
        };

        var paquete = servicio.Exportar(configuracion, CrearPermisosAdmin());
        var rutaPaquete = Path.Combine(entorno.Raiz, paquete.NombreArchivo);
        File.WriteAllBytes(rutaPaquete, Convert.FromBase64String(paquete.ContenidoBase64));

        var importacion = servicio.Importar(rutaPaquete, new ConfiguracionLanzador());
        Assert.Equal(configuracion.RutaScripts, importacion.Configuracion.RutaScripts);
        Assert.NotNull(importacion.Permisos);

        var configuracionInsegura = new ConfiguracionLanzador
        {
            RutaScripts = @"\\SERVIDOR\C$\REPO",
            RutaPermisos = @"\\SERVIDOR\C$\REPO\PERMISOS\permisos.json"
        };
        var paqueteInseguro = servicio.Exportar(configuracionInsegura, CrearPermisosAdmin());
        var rutaInsegura = Path.Combine(entorno.Raiz, "inseguro.lanzadorconfig");
        File.WriteAllBytes(rutaInsegura, Convert.FromBase64String(paqueteInseguro.ContenidoBase64));

        Assert.Throws<InvalidOperationException>(() => servicio.Importar(rutaInsegura, new ConfiguracionLanzador()));
    }

    [Fact]
    public async Task ApiBloqueaPermisosAusentesOCorruptos()
    {
        using var entorno = EntornoPruebas.Crear();
        using var servidor = ServidorLocalWeb.IniciarParaPruebas(entorno.CrearConfiguracionPermisosAusentes());
        using var cliente = CrearCliente(servidor);
        await PrepararSesionAsync(cliente, servidor);

        var usuario = await LeerJsonAsync(await cliente.GetAsync("/api/usuario"));
        Assert.True(usuario?["bloqueado"]?.GetValue<bool>());
        Assert.Equal("No se encontro el archivo de permisos.", usuario?["motivoBloqueo"]?.GetValue<string>());

        var scripts = await LeerJsonAsync(await cliente.GetAsync("/api/scripts")) as JsonArray;
        Assert.NotNull(scripts);
        Assert.All(scripts!, script => Assert.True(script?["estaBloqueado"]?.GetValue<bool>()));

        using var cuerpo = new StringContent("{\"scriptId\":\"ok.ps1\"}", Encoding.UTF8, "application/json");
        var respuesta = await cliente.PostAsync("/api/ejecuciones", cuerpo);
        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task ApiBloqueaPermisosCorruptos()
    {
        using var entorno = EntornoPruebas.Crear();
        File.WriteAllText(entorno.RutaPermisos, "{\"usuarios\":[]}");
        using var rsa = RSA.Create(3072);
        using var servidor = ServidorLocalWeb.IniciarParaPruebas(entorno.CrearConfiguracion(), new ServicioCifradoAplicacion(rsa, rsa, "Pruebas"));
        using var cliente = CrearCliente(servidor);
        await PrepararSesionAsync(cliente, servidor);

        var salud = await LeerJsonAsync(await cliente.GetAsync("/api/salud"));
        Assert.Equal("degradado", salud?["estado"]?.GetValue<string>());
        Assert.Equal("Corrupto", salud?["permisos"]?["estado"]?.GetValue<string>());

        var usuario = await LeerJsonAsync(await cliente.GetAsync("/api/usuario"));
        Assert.True(usuario?["bloqueado"]?.GetValue<bool>());
    }

    [Fact]
    public async Task ApiAdminExigeBearer()
    {
        using var entorno = EntornoPruebas.Crear();
        using var rsa = RSA.Create(3072);
        var firma = new ServicioCifradoAplicacion(rsa, rsa, "Pruebas");
        entorno.GuardarPermisosFirmados(firma, CrearPermisosAdmin());
        using var servidor = ServidorLocalWeb.IniciarParaPruebas(entorno.CrearConfiguracion(), firma);
        using var cliente = CrearCliente(servidor);
        await PrepararSesionAsync(cliente, servidor);

        var sinBearer = await cliente.GetAsync("/api/ajustes");
        Assert.Equal(HttpStatusCode.Unauthorized, sinBearer.StatusCode);

        using var bearerInvalido = new HttpRequestMessage(HttpMethod.Get, "/api/ajustes");
        bearerInvalido.Headers.TryAddWithoutValidation("Authorization", "Bearer invalido");
        var respuestaBearerInvalido = await cliente.SendAsync(bearerInvalido);
        Assert.Equal(HttpStatusCode.Forbidden, respuestaBearerInvalido.StatusCode);
    }

    [Fact]
    public async Task EjecucionRealDevuelveEventosFinales()
    {
        using var entorno = EntornoPruebas.Crear();
        using var rsa = RSA.Create(3072);
        var firma = new ServicioCifradoAplicacion(rsa, rsa, "Pruebas");
        entorno.GuardarPermisosFirmados(firma, CrearPermisosAdmin());
        using var servidor = ServidorLocalWeb.IniciarParaPruebas(entorno.CrearConfiguracion(), firma);
        using var cliente = CrearCliente(servidor);
        await PrepararSesionAsync(cliente, servidor);

        var usuario = await LeerJsonAsync(await cliente.GetAsync("/api/usuario"));
        var tokenAdmin = usuario?["tokenAdmin"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(tokenAdmin));

        using var activarDev = new HttpRequestMessage(HttpMethod.Post, "/api/desarrollo-firmas")
        {
            Content = new StringContent("{\"activo\":true}", Encoding.UTF8, "application/json")
        };
        activarDev.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tokenAdmin);
        Assert.Equal(HttpStatusCode.OK, (await cliente.SendAsync(activarDev)).StatusCode);

        using var cuerpo = new StringContent("{\"scriptId\":\"ok.ps1\"}", Encoding.UTF8, "application/json");
        var respuesta = await cliente.PostAsync("/api/ejecuciones", cuerpo);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var inicio = await LeerJsonAsync(respuesta);
        var id = inicio?["id"]?.GetValue<Guid>();
        Assert.NotNull(id);

        var eventos = await LeerEventosAsync(cliente, id!.Value);
        Assert.Contains(eventos, evento => evento.Contains("ok", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(eventos, evento => evento.Contains("Finalizada correctamente", StringComparison.OrdinalIgnoreCase));
    }

    private static HttpClient CrearCliente(ServidorLocalWeb servidor)
    {
        var cookies = new CookieContainer();
        var manejador = new HttpClientHandler
        {
            CookieContainer = cookies
        };

        return new HttpClient(manejador)
        {
            BaseAddress = servidor.UrlBase
        };
    }

    private static async Task PrepararSesionAsync(HttpClient cliente, ServidorLocalWeb servidor)
    {
        _ = await cliente.GetAsync("/");
        cliente.DefaultRequestHeaders.Add("X-LanzadorScripts-ApiToken", servidor.TokenApiInterno);
    }

    private static async Task<JsonNode?> LeerJsonAsync(HttpResponseMessage respuesta)
    {
        var contenido = await respuesta.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(contenido) ? null : JsonNode.Parse(contenido);
    }

    private static async Task<IReadOnlyList<string>> LeerEventosAsync(HttpClient cliente, Guid id)
    {
        using var cancelacion = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var flujo = await cliente.GetStreamAsync($"/api/ejecuciones/{id}/eventos", cancelacion.Token);
        using var lector = new StreamReader(flujo, Encoding.UTF8);
        var eventos = new List<string>();
        while (!cancelacion.IsCancellationRequested)
        {
            var linea = await lector.ReadLineAsync(cancelacion.Token);
            if (linea is null)
            {
                break;
            }

            if (linea.StartsWith("data: ", StringComparison.Ordinal))
            {
                eventos.Add(linea);
                if (linea.Contains("finalizada", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
        }

        return eventos;
    }

    private static JsonObject CrearPermisosBase()
    {
        return new JsonObject
        {
            ["inicioAutomaticoWindows"] = false,
            ["scriptsAdmin"] = new JsonArray(),
            ["usuarios"] = new JsonArray(),
            ["seguridadScripts"] = new JsonObject
            {
                ["certificadosPowerShellPermitidos"] = new JsonArray(),
                ["hashesBatchPermitidos"] = new JsonArray(),
                ["permitirExecutionPolicyBypass"] = false
            },
            ["rolUsuarioActual"] = "nominal",
            ["maxScriptsSimultaneos"] = 5
        };
    }

    private static JsonObject CrearPermisosAdmin()
    {
        var permisos = CrearPermisosBase();
        permisos["usuarios"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "admin-local",
                ["nombreUsuario"] = WindowsIdentity.GetCurrent().Name,
                ["rol"] = "admin",
                ["maxScriptsSimultaneos"] = 5,
                ["carpetasPermitidas"] = new JsonArray()
            }
        };
        return permisos;
    }
}

internal sealed class EntornoPruebas : IDisposable
{
    private EntornoPruebas(string raiz)
    {
        Raiz = raiz;
        RutaPermisos = Path.Combine(Raiz, "PERMISOS", "permisos.json");
    }

    public string Raiz { get; }

    public string RutaPermisos { get; }

    public static EntornoPruebas Crear()
    {
        var raiz = Path.Combine(Path.GetTempPath(), "LanzadorScripts_Pruebas_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(raiz);
        File.WriteAllText(Path.Combine(raiz, "ok.ps1"), "Write-Output 'ok'");
        Directory.CreateDirectory(Path.Combine(raiz, "sub"));
        File.WriteAllText(Path.Combine(raiz, "sub", "ok.cmd"), "echo ok");
        Directory.CreateDirectory(Path.Combine(raiz, "PERMISOS"));
        File.WriteAllText(Path.Combine(raiz, "PERMISOS", "bloqueado.ps1"), "Write-Output 'no'");
        Directory.CreateDirectory(Path.Combine(raiz, ".git"));
        File.WriteAllText(Path.Combine(raiz, ".git", "bloqueado.ps1"), "Write-Output 'no'");
        File.WriteAllText(Path.Combine(raiz, "texto.txt"), "no");
        File.WriteAllText(Path.Combine(raiz, "bad&name.ps1"), "Write-Output 'no'");
        return new EntornoPruebas(raiz);
    }

    public ConfiguracionLanzador CrearConfiguracion()
    {
        return new ConfiguracionLanzador
        {
            RutaScripts = Raiz,
            RutaPermisos = RutaPermisos,
            RutaLogs = Path.Combine(Raiz, "Logs")
        };
    }

    public ConfiguracionLanzador CrearConfiguracionPermisosAusentes()
    {
        return new ConfiguracionLanzador
        {
            RutaScripts = Raiz,
            RutaPermisos = Path.Combine(Raiz, "PERMISOS", "ausente.json"),
            RutaLogs = Path.Combine(Raiz, "Logs")
        };
    }

    public void GuardarPermisosFirmados(ServicioCifradoAplicacion firma, JsonObject permisos)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RutaPermisos)!);
        File.WriteAllText(RutaPermisos, firma.CifrarTexto("permisos", permisos.ToJsonString()));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Raiz, recursive: true);
        }
        catch
        {
        }
    }
}
