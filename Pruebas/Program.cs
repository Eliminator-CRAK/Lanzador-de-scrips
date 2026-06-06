// (Autor: Alex Roman)
// Descripcion: Ejecuta pruebas basicas del validador de scripts.

using LanzadorScripts.Servicios;
using LanzadorScripts.Modelos;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

var raiz = Path.Combine(Path.GetTempPath(), "LanzadorScripts_Pruebas_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(raiz);

try
{
    File.WriteAllText(Path.Combine(raiz, "ok.ps1"), "Write-Output 'ok'");
    Directory.CreateDirectory(Path.Combine(raiz, "sub"));
    File.WriteAllText(Path.Combine(raiz, "sub", "ok.cmd"), "echo ok");
    Directory.CreateDirectory(Path.Combine(raiz, "PERMISOS"));
    File.WriteAllText(Path.Combine(raiz, "PERMISOS", "bloqueado.ps1"), "Write-Output 'no'");
    Directory.CreateDirectory(Path.Combine(raiz, ".git"));
    File.WriteAllText(Path.Combine(raiz, ".git", "bloqueado.ps1"), "Write-Output 'no'");
    File.WriteAllText(Path.Combine(raiz, "texto.txt"), "no");
    File.WriteAllText(Path.Combine(raiz, "bad&name.ps1"), "Write-Output 'no'");

    var validador = new ServicioValidacionScripts();

    Verificar(
        validador.ValidarScriptParaEjecucion(raiz, "ok.ps1").EsValido,
        "Permite un script PowerShell valido.");

    Verificar(
        validador.ValidarScriptParaEjecucion(raiz, "sub/ok.cmd").EsValido,
        "Permite un script batch valido en subcarpeta.");

    Verificar(
        validador.ValidarScriptParaEjecucion(raiz, "../fuera.ps1").Codigo == CodigoValidacionScript.IdentificadorNoPermitido,
        "Bloquea rutas con salida de carpeta.");

    Verificar(
        validador.ValidarScriptParaEjecucion(raiz, "PERMISOS/bloqueado.ps1").Codigo == CodigoValidacionScript.CarpetaExcluida,
        "Bloquea scripts dentro de PERMISOS.");

    Verificar(
        validador.ValidarScriptParaEjecucion(raiz, "texto.txt").Codigo == CodigoValidacionScript.ExtensionNoPermitida,
        "Bloquea extensiones no permitidas.");

    Verificar(
        validador.ValidarScriptParaEjecucion(raiz, "bad&name.ps1").Codigo == CodigoValidacionScript.MetacaracterPeligroso,
        "Bloquea metacaracteres peligrosos.");

    var descubiertos = validador.DescubrirScripts(raiz);
    Verificar(
        descubiertos.Count == 2
        && descubiertos.Any(script => script.Id == "ok.ps1")
        && descubiertos.Any(script => script.Id == "sub/ok.cmd"),
        "Descubre solo scripts permitidos.");

    var seguridad = new ServicioSeguridadScripts();
    var permisosVacios = new JsonObject
    {
        ["seguridadScripts"] = ServicioSeguridadScripts.NormalizarPolitica(null)
    };

    var ps1 = validador.ValidarScriptParaEjecucion(raiz, "ok.ps1").Script!;
    Verificar(
        !seguridad.Diagnosticar(ps1, permisosVacios).Permitido,
        "Bloquea PowerShell sin certificado permitido.");

    var cmd = validador.ValidarScriptParaEjecucion(raiz, "sub/ok.cmd").Script!;
    Verificar(
        !seguridad.Diagnosticar(cmd, permisosVacios).Permitido,
        "Bloquea batch sin hash permitido.");

    var permisosHash = new JsonObject
    {
        ["seguridadScripts"] = new JsonObject
        {
            ["certificadosPowerShellPermitidos"] = new JsonArray(),
            ["hashesBatchPermitidos"] = new JsonArray
            {
                new JsonObject
                {
                    ["scriptId"] = "sub/ok.cmd",
                    ["sha256"] = ServicioSeguridadScripts.CalcularSha256(cmd.RutaCompleta)
                }
            },
            ["permitirExecutionPolicyBypass"] = false
        }
    };

    Verificar(
        seguridad.Diagnosticar(cmd, permisosHash).Permitido,
        "Permite batch con hash SHA-256 autorizado.");

    Verificar(
        seguridad.Diagnosticar(cmd, permisosVacios, modoDesarrolloFirmas: true).Permitido,
        "Permite batch sin hash en modo desarrollo temporal.");

    var scriptMalo = new ScriptInterno("bad&name.ps1", "bad&name.ps1", "powershell", Path.Combine(raiz, "bad&name.ps1"));
    Verificar(
        !seguridad.Diagnosticar(scriptMalo, permisosVacios, modoDesarrolloFirmas: true).Permitido,
        "Mantiene bloqueo de metacaracteres en modo desarrollo.");

    ProbarExportacionPermisos(raiz);
    ProbarEstadoRutasPermisos(raiz);

    await ProbarProteccionApiAsync();
    await ProbarPermisosOfflineAsync(raiz);

    Console.WriteLine("Pruebas correctas.");
}
finally
{
    Directory.Delete(raiz, recursive: true);
}

static void Verificar(bool condicion, string mensaje)
{
    if (!condicion)
    {
        throw new InvalidOperationException("Prueba fallida: " + mensaje);
    }

    Console.WriteLine("OK - " + mensaje);
}

static void ProbarEstadoRutasPermisos(string raiz)
{
    var carpetaInexistente = Path.Combine(raiz, "PERMISOS_INACCESIBLES");
    var rutaInaccesible = Path.Combine(carpetaInexistente, "permisos.json");
    Verificar(
        ServidorLocalWeb.RutaPermisosInaccesible(rutaInaccesible),
        "Detecta ruta de permisos inaccesible si falta la carpeta padre.");

    var rutaNoEncontrada = Path.Combine(raiz, "PERMISOS", "permisos_ausente.json");
    Verificar(
        !ServidorLocalWeb.RutaPermisosInaccesible(rutaNoEncontrada),
        "Mantiene archivo de permisos no encontrado si la carpeta existe.");
}

static void ProbarExportacionPermisos(string raiz)
{
    var configuracion = new LanzadorScripts.Modelos.ConfiguracionLanzador
    {
        RutaScripts = raiz,
        RutaPermisos = Path.Combine(raiz, "PERMISOS", "permisos.json")
    };
    var permisos = new JsonObject
    {
        ["seguridadScripts"] = new JsonObject
        {
            ["certificadosPowerShellPermitidos"] = new JsonArray("ABCDEF"),
            ["hashesBatchPermitidos"] = new JsonArray(),
            ["permitirExecutionPolicyBypass"] = false
        },
        ["usuarios"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "u1",
                ["nombreUsuario"] = "DOMINIO\\usuario",
                ["rol"] = "nominal",
                ["maxScriptsSimultaneos"] = 5,
                ["carpetasPermitidas"] = new JsonArray("sub")
            }
        }
    };

    var servicio = new ServicioPaquetesConfiguracion();
    var paquete = servicio.Exportar(configuracion, permisos);
    var rutaPaquete = Path.Combine(raiz, paquete.NombreArchivo);
    File.WriteAllBytes(rutaPaquete, Convert.FromBase64String(paquete.ContenidoBase64));
    var importacion = servicio.Importar(rutaPaquete, new LanzadorScripts.Modelos.ConfiguracionLanzador());

    Verificar(
        importacion.Permisos?["seguridadScripts"]?["certificadosPowerShellPermitidos"]?.AsArray().Count == 1,
        "Exporta e importa certificados permitidos.");

    Verificar(
        importacion.Permisos?["usuarios"]?[0]?["carpetasPermitidas"]?.AsArray().Count == 1,
        "Exporta e importa permisos por subcarpeta.");
}

static async Task ProbarPermisosOfflineAsync(string raiz)
{
    var configuracion = new ConfiguracionLanzador
    {
        RutaScripts = raiz,
        RutaPermisos = Path.Combine(raiz, "PERMISOS_INACCESIBLES", "permisos.json")
    };

    using var servidor = ServidorLocalWeb.IniciarParaPruebas(configuracion);
    var cookies = new CookieContainer();
    using var manejador = new HttpClientHandler
    {
        CookieContainer = cookies
    };
    using var cliente = new HttpClient(manejador)
    {
        BaseAddress = servidor.UrlBase
    };

    _ = await cliente.GetAsync("/");
    cliente.DefaultRequestHeaders.Add("X-LanzadorScripts-ApiToken", servidor.TokenApiInterno);

    var usuario = await LeerJsonAsync(await cliente.GetAsync("/api/usuario"));
    Verificar(
        usuario?["modoOffline"]?.GetValue<bool>() == true
        && usuario?["avisoConexion"]?.GetValue<string>() == "No se puede conectar al servidor.",
        "Devuelve aviso offline amarillo desde usuario.");

    var scripts = await LeerJsonAsync(await cliente.GetAsync("/api/scripts")) as JsonArray;
    Verificar(
        scripts is not null
        && scripts.Count > 0
        && scripts.All(script =>
            script?["estaBloqueado"]?.GetValue<bool>() == true
            && script?["motivoBloqueo"]?.GetValue<string>() == "No se puede conectar al servidor."),
        "Bloquea todos los scripts cuando permisos esta inaccesible.");

    using var cuerpo = new StringContent("{\"scriptId\":\"ok.ps1\"}", Encoding.UTF8, "application/json");
    var respuestaEjecucion = await cliente.PostAsync("/api/ejecuciones", cuerpo);
    var error = await LeerJsonAsync(respuestaEjecucion);
    Verificar(
        respuestaEjecucion.StatusCode == HttpStatusCode.Forbidden
        && error?["error"]?.GetValue<string>() == "No se puede conectar al servidor.",
        "Bloquea ejecucion directa si permisos esta inaccesible.");
}

static async Task ProbarProteccionApiAsync()
{
    using var servidor = ServidorLocalWeb.Iniciar();
    var cookies = new CookieContainer();
    using var manejador = new HttpClientHandler
    {
        CookieContainer = cookies
    };
    using var cliente = new HttpClient(manejador)
    {
        BaseAddress = servidor.UrlBase
    };

    _ = await cliente.GetAsync("/");

    var sinTokenApi = await cliente.GetAsync("/api/scripts");
    Verificar(
        sinTokenApi.StatusCode == HttpStatusCode.Forbidden,
        "Bloquea API local sin token interno.");

    using var sinBearer = new HttpRequestMessage(HttpMethod.Get, "/api/ajustes");
    sinBearer.Headers.Add("X-LanzadorScripts-ApiToken", servidor.TokenApiInterno);
    var respuestaSinBearer = await cliente.SendAsync(sinBearer);
    Verificar(
        respuestaSinBearer.StatusCode == HttpStatusCode.Unauthorized,
        "Bloquea endpoint admin sin Bearer.");

    using var modoDesarrolloSinBearer = new HttpRequestMessage(HttpMethod.Get, "/api/desarrollo-firmas");
    modoDesarrolloSinBearer.Headers.Add("X-LanzadorScripts-ApiToken", servidor.TokenApiInterno);
    var respuestaModoDesarrolloSinBearer = await cliente.SendAsync(modoDesarrolloSinBearer);
    Verificar(
        respuestaModoDesarrolloSinBearer.StatusCode == HttpStatusCode.Unauthorized,
        "Bloquea modo desarrollo sin Bearer.");

    using var bearerInvalido = new HttpRequestMessage(HttpMethod.Get, "/api/ajustes");
    bearerInvalido.Headers.Add("X-LanzadorScripts-ApiToken", servidor.TokenApiInterno);
    bearerInvalido.Headers.TryAddWithoutValidation("Authorization", "Bearer invalido");
    var respuestaBearerInvalido = await cliente.SendAsync(bearerInvalido);
    Verificar(
        respuestaBearerInvalido.StatusCode == HttpStatusCode.Forbidden,
        "Bloquea endpoint admin con Bearer invalido.");
}

static async Task<JsonNode?> LeerJsonAsync(HttpResponseMessage respuesta)
{
    var contenido = await respuesta.Content.ReadAsStringAsync();
    return string.IsNullOrWhiteSpace(contenido) ? null : JsonNode.Parse(contenido);
}
