// (Autor: Alex Roman)
// Descripcion: Ejecuta pruebas basicas del validador de scripts.

using LanzadorScripts.Servicios;
using System.IO;

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

    var descubiertos = validador.DescubrirScripts(raiz);
    Verificar(
        descubiertos.Count == 2
        && descubiertos.Any(script => script.Id == "ok.ps1")
        && descubiertos.Any(script => script.Id == "sub/ok.cmd"),
        "Descubre solo scripts permitidos.");

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
