// (Autor: Alex Roman)
// Descripcion: Ejecuta pruebas basicas del validador de scripts.

using LanzadorScripts.Servicios;
using System.IO;
using System.Net;
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

    await ProbarProteccionApiAsync();

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

    using var bearerInvalido = new HttpRequestMessage(HttpMethod.Get, "/api/ajustes");
    bearerInvalido.Headers.Add("X-LanzadorScripts-ApiToken", servidor.TokenApiInterno);
    bearerInvalido.Headers.TryAddWithoutValidation("Authorization", "Bearer invalido");
    var respuestaBearerInvalido = await cliente.SendAsync(bearerInvalido);
    Verificar(
        respuestaBearerInvalido.StatusCode == HttpStatusCode.Forbidden,
        "Bloquea endpoint admin con Bearer invalido.");
}
