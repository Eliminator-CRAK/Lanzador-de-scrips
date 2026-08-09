// (Autor: Alex Roman)
// Descripcion: Comprueba el formato estricto y la coherencia de los artefactos firmados v3.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LanzadorScripts.Servicios;
using Xunit;

namespace LanzadorScripts.Pruebas;

public sealed class PruebasCompatibilidadArtefactos
{
    private const string PermisosValidos = "{\"scriptsAdmin\":[],\"usuarios\":[],\"seguridadScripts\":{\"scriptsElevadosPermitidos\":[],\"permitirExecutionPolicyBypass\":false},\"rolUsuarioActual\":\"nominal\",\"maxScriptsSimultaneos\":5}";

    [Fact]
    public void ContenedorV3EsLegibleFirmadoYSeparaTipos()
    {
        using var rsa = RSA.Create(3072);
        var servicio = new ServicioArtefactosFirmados(rsa, rsa);
        var conjuntoId = ServicioArtefactosFirmados.CrearConjuntoId();
        var contenedor = servicio.FirmarTexto(
            ServicioArtefactosFirmados.TipoPermisos,
            "{\"usuarios\":[]}",
            conjuntoId);

        Assert.Contains("\"Version\": 3", contenedor, StringComparison.Ordinal);
        Assert.Contains("\"Contenido\"", contenedor, StringComparison.Ordinal);
        Assert.Contains("\"usuarios\"", contenedor, StringComparison.Ordinal);
        using var documento = JsonDocument.Parse(contenedor);
        Assert.Equal(
            ServicioArtefactosFirmados.AlgoritmoActual,
            documento.RootElement.GetProperty("Algoritmo").GetString());
        Assert.False(documento.RootElement.TryGetProperty("Nonce", out _));
        Assert.False(documento.RootElement.TryGetProperty("Tag", out _));
        Assert.False(documento.RootElement.TryGetProperty("TextoCifrado", out _));
        Assert.True(servicio.IntentarValidarTexto(
            ServicioArtefactosFirmados.TipoPermisos,
            contenedor,
            out var contenido,
            out var conjuntoLeido,
            out _));
        Assert.Equal("{\"usuarios\":[]}", contenido);
        Assert.Equal(conjuntoId, conjuntoLeido);
        Assert.False(servicio.IntentarValidarTexto(
            ServicioArtefactosFirmados.TipoCatalogoScripts,
            contenedor,
            out _,
            out _,
            out _));
    }

    [Theory]
    [InlineData("Autor", "Otro autor")]
    [InlineData("Descripcion", "Otra descripcion")]
    [InlineData("Version", "4")]
    [InlineData("Tipo", "script-catalog")]
    [InlineData("Algoritmo", "RSA-PKCS1-SHA256")]
    [InlineData("ConjuntoId", "00000000000000000000000000000000")]
    public void ModificarMetadatosInvalidaLaFirma(string propiedad, string valor)
    {
        using var rsa = RSA.Create(3072);
        var servicio = new ServicioArtefactosFirmados(rsa, rsa);
        var contenedor = JsonNode.Parse(servicio.FirmarTexto(
            ServicioArtefactosFirmados.TipoPermisos,
            "{\"usuarios\":[]}",
            ServicioArtefactosFirmados.CrearConjuntoId()))!.AsObject();
        contenedor[propiedad] = propiedad == "Version" ? int.Parse(valor) : valor;

        Assert.False(servicio.IntentarValidarTexto(
            ServicioArtefactosFirmados.TipoPermisos,
            contenedor.ToJsonString(),
            out _,
            out _,
            out _));
    }

    [Fact]
    public void ModificarContenidoFirmaOClavePublicaSeRechaza()
    {
        using var rsa = RSA.Create(3072);
        using var rsaIncorrecta = RSA.Create(3072);
        var escritor = new ServicioArtefactosFirmados(rsa, rsa);
        var lectorIncorrecto = new ServicioArtefactosFirmados(rsa, rsaIncorrecta);
        var firmado = escritor.FirmarTexto(
            ServicioArtefactosFirmados.TipoPermisos,
            "{\"valor\":1}",
            ServicioArtefactosFirmados.CrearConjuntoId());
        var contenidoManipulado = JsonNode.Parse(firmado)!.AsObject();
        contenidoManipulado["Contenido"]!["valor"] = 2;
        var firmaInvalida = JsonNode.Parse(firmado)!.AsObject();
        firmaInvalida["Firma"] = "***";
        var firmaIncorrecta = JsonNode.Parse(firmado)!.AsObject();
        firmaIncorrecta["Firma"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(384));

        Assert.False(escritor.IntentarValidarTexto(
            ServicioArtefactosFirmados.TipoPermisos,
            contenidoManipulado.ToJsonString(),
            out _,
            out _,
            out _));
        Assert.False(escritor.IntentarValidarTexto(
            ServicioArtefactosFirmados.TipoPermisos,
            firmaInvalida.ToJsonString(),
            out _,
            out _,
            out var errorFirma));
        Assert.Contains("Base64", errorFirma, StringComparison.Ordinal);
        Assert.False(escritor.IntentarValidarTexto(
            ServicioArtefactosFirmados.TipoPermisos,
            firmaIncorrecta.ToJsonString(),
            out _,
            out _,
            out _));
        Assert.False(lectorIncorrecto.IntentarValidarTexto(
            ServicioArtefactosFirmados.TipoPermisos,
            firmado,
            out _,
            out _,
            out _));
    }

    [Fact]
    public void PropiedadesDesconocidasODuplicadasSeRechazan()
    {
        using var rsa = RSA.Create(3072);
        var servicio = new ServicioArtefactosFirmados(rsa, rsa);
        var firmado = servicio.FirmarTexto(
            ServicioArtefactosFirmados.TipoPermisos,
            "{\"usuarios\":[]}",
            ServicioArtefactosFirmados.CrearConjuntoId());
        var desconocida = JsonNode.Parse(firmado)!.AsObject();
        desconocida["Extra"] = true;
        var duplicada = firmado.Replace(
            "  \"Autor\": \"Alex Roman\",",
            "  \"Autor\": \"Alex Roman\",\n  \"Autor\": \"Alex Roman\",",
            StringComparison.Ordinal);

        Assert.False(servicio.IntentarValidarTexto(
            ServicioArtefactosFirmados.TipoPermisos,
            desconocida.ToJsonString(),
            out _,
            out _,
            out _));
        Assert.False(servicio.IntentarValidarTexto(
            ServicioArtefactosFirmados.TipoPermisos,
            duplicada,
            out _,
            out _,
            out _));
        Assert.Throws<ArgumentException>(() => servicio.FirmarTexto(
            ServicioArtefactosFirmados.TipoPermisos,
            "{\"valor\":1,\"valor\":2}",
            ServicioArtefactosFirmados.CrearConjuntoId()));
    }

    [Fact]
    public void ContenedorAesAnteriorSeRechazaConErrorDeMigracion()
    {
        using var rsa = RSA.Create(3072);
        var servicio = new ServicioArtefactosFirmados(rsa, rsa);
        const string anterior = "{\"Version\":2,\"Tipo\":\"permissions\",\"Algoritmo\":\"AES-256-GCM+RSA-PSS-SHA256\",\"KeyId\":\"0123456789ABCDEF\"}";

        Assert.False(servicio.IntentarValidarTexto(
            ServicioArtefactosFirmados.TipoPermisos,
            anterior,
            out _,
            out _,
            out var error));
        Assert.Contains("AES v1/v2 obsoleto", error, StringComparison.Ordinal);
    }

    [Fact]
    public void LecturaRecuperaCopiaBakValida()
    {
        using var entorno = EntornoTemporal.Crear();
        using var rsa = RSA.Create(3072);
        var servicio = new ServicioArtefactosFirmados(rsa, rsa);
        var ruta = Path.Combine(entorno.Raiz, "permisos.json");
        var conjuntoId = ServicioArtefactosFirmados.CrearConjuntoId();
        servicio.GuardarTextoFirmado(
            ruta,
            ServicioArtefactosFirmados.TipoPermisos,
            "{\"version\":1}",
            conjuntoId);
        servicio.GuardarTextoFirmado(
            ruta,
            ServicioArtefactosFirmados.TipoPermisos,
            "{\"version\":2}",
            conjuntoId);
        File.WriteAllText(ruta, "{");

        Assert.True(servicio.IntentarCargarTextoFirmado(
            ruta,
            ServicioArtefactosFirmados.TipoPermisos,
            out var contenido,
            out var conjuntoLeido,
            out _,
            out var recuperado));
        Assert.True(recuperado);
        Assert.Equal(conjuntoId, conjuntoLeido);
        Assert.Contains("\"version\":1", contenido, StringComparison.Ordinal);
    }

    [Fact]
    public void Utf8IncorrectoSeRechaza()
    {
        using var entorno = EntornoTemporal.Crear();
        using var rsa = RSA.Create(3072);
        var servicio = new ServicioArtefactosFirmados(rsa, rsa);
        var ruta = Path.Combine(entorno.Raiz, "permisos.json");
        File.WriteAllBytes(ruta, [0xC3, 0x28]);

        Assert.False(servicio.IntentarCargarTextoFirmado(
            ruta,
            ServicioArtefactosFirmados.TipoPermisos,
            out _,
            out _,
            out var error,
            out _));
        Assert.Contains("UTF-8", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArchivoYFirmaConTamanoExcesivoSeRechazan()
    {
        using var entorno = EntornoTemporal.Crear();
        using var rsa = RSA.Create(3072);
        var servicio = new ServicioArtefactosFirmados(rsa, rsa);
        var ruta = Path.Combine(entorno.Raiz, "permisos.json");
        File.WriteAllText(ruta, new string('A', 24 * 1024 * 1024 + 1), Encoding.ASCII);

        Assert.False(servicio.IntentarCargarTextoFirmado(
            ruta,
            ServicioArtefactosFirmados.TipoPermisos,
            out _,
            out _,
            out var errorArchivo,
            out _));
        Assert.Contains("tamano", errorArchivo, StringComparison.OrdinalIgnoreCase);

        var firmado = JsonNode.Parse(servicio.FirmarTexto(
            ServicioArtefactosFirmados.TipoPermisos,
            "{\"usuarios\":[]}",
            ServicioArtefactosFirmados.CrearConjuntoId()))!.AsObject();
        firmado["Firma"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16 * 1024 + 1));
        Assert.False(servicio.IntentarValidarTexto(
            ServicioArtefactosFirmados.TipoPermisos,
            firmado.ToJsonString(),
            out _,
            out _,
            out var errorFirma));
        Assert.Contains("longitud", errorFirma, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PermisosFirmadosConPropiedadInternaDesconocidaSeRechazan()
    {
        using var entorno = EntornoTemporal.Crear();
        using var rsa = RSA.Create(3072);
        var artefactos = new ServicioArtefactosFirmados(rsa, rsa);
        var conjunto = new ServicioConjuntoArtefactos(artefactos);
        var ruta = Path.Combine(entorno.Raiz, "permisos.json");
        artefactos.GuardarTextoFirmado(
            ruta,
            ServicioArtefactosFirmados.TipoPermisos,
            "{\"scriptsAdmin\":[],\"usuarios\":[],\"seguridadScripts\":{\"scriptsElevadosPermitidos\":[],\"permitirExecutionPolicyBypass\":false},\"rolUsuarioActual\":\"nominal\",\"maxScriptsSimultaneos\":5,\"campoIgnorado\":true}",
            ServicioArtefactosFirmados.CrearConjuntoId());

        Assert.False(conjunto.IntentarCargarPermisos(
            ruta,
            out _,
            out _,
            out var error,
            out _));
        Assert.Contains("propiedades", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RutaFirmadaMedianteEnlaceSeRechazaCuandoElSistemaPermiteCrearlo()
    {
        using var entorno = EntornoTemporal.Crear();
        using var rsa = RSA.Create(3072);
        var servicio = new ServicioArtefactosFirmados(rsa, rsa);
        var destino = Path.Combine(entorno.Raiz, "destino.json");
        var enlace = Path.Combine(entorno.Raiz, "permisos.json");
        servicio.GuardarTextoFirmado(
            destino,
            ServicioArtefactosFirmados.TipoPermisos,
            "{\"usuarios\":[]}",
            ServicioArtefactosFirmados.CrearConjuntoId());
        try
        {
            File.CreateSymbolicLink(enlace, destino);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Assert.False(servicio.IntentarCargarTextoFirmado(
            enlace,
            ServicioArtefactosFirmados.TipoPermisos,
            out _,
            out _,
            out var error,
            out _));
        Assert.Contains("leer", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParejaConConjuntoIdDistintoSeRechaza()
    {
        using var entorno = EntornoTemporal.CrearConScripts();
        using var rsa = RSA.Create(3072);
        var artefactos = new ServicioArtefactosFirmados(rsa, rsa);
        var rutaPermisos = Path.Combine(entorno.Raiz, "permisos.json");
        artefactos.GuardarTextoFirmado(
            rutaPermisos,
            ServicioArtefactosFirmados.TipoPermisos,
            PermisosValidos,
            ServicioArtefactosFirmados.CrearConjuntoId());
        var scripts = new ServicioValidacionScripts().DescubrirScripts(entorno.RutaScripts);
        var catalogos = new ServicioCatalogoScripts(artefactos);
        catalogos.Guardar(
            Path.Combine(entorno.Raiz, ServicioCatalogoScripts.NombreArchivo),
            catalogos.Crear(
                scripts,
                scripts.Select(script => script.Id),
                ServicioArtefactosFirmados.CrearConjuntoId()));

        Assert.False(new ServicioConjuntoArtefactos(artefactos).IntentarCargarPareja(
            rutaPermisos,
            out _,
            out _,
            out _,
            out var error));
        Assert.Contains("ConjuntoId", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EscrituraEsperaBloqueoYConservaConjuntoId()
    {
        using var entorno = EntornoTemporal.CrearConScripts();
        using var rsa = RSA.Create(3072);
        var artefactos = new ServicioArtefactosFirmados(rsa, rsa);
        var conjuntoId = ServicioArtefactosFirmados.CrearConjuntoId();
        var rutaPermisos = Path.Combine(entorno.Raiz, "permisos.json");
        artefactos.GuardarTextoFirmado(
            rutaPermisos,
            ServicioArtefactosFirmados.TipoPermisos,
            PermisosValidos,
            conjuntoId);
        var scripts = new ServicioValidacionScripts().DescubrirScripts(entorno.RutaScripts);
        var catalogos = new ServicioCatalogoScripts(artefactos);
        catalogos.Guardar(
            Path.Combine(entorno.Raiz, ServicioCatalogoScripts.NombreArchivo),
            catalogos.Crear(scripts, scripts.Select(script => script.Id), conjuntoId));
        var rutaBloqueo = Path.Combine(entorno.Raiz, ".lanzadorscripts-conjunto.lock");
        using var bloqueo = new FileStream(
            rutaBloqueo,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        var conjunto = new ServicioConjuntoArtefactos(artefactos);
        var escritura = Task.Run(() => conjunto.GuardarPermisosPreservandoConjunto(
            rutaPermisos,
            JsonNode.Parse(PermisosValidos)!.AsObject()));
        await Task.Delay(250);
        Assert.False(escritura.IsCompleted);
        bloqueo.Dispose();
        await escritura;

        Assert.True(conjunto.IntentarCargarPareja(
            rutaPermisos,
            out _,
            out _,
            out var conjuntoLeido,
            out _));
        Assert.Equal(conjuntoId, conjuntoLeido);
        Assert.True(File.Exists(rutaPermisos + ".bak"));
    }

    private sealed class EntornoTemporal : IDisposable
    {
        private EntornoTemporal(string raiz, string rutaScripts)
        {
            Raiz = raiz;
            RutaScripts = rutaScripts;
        }

        public string Raiz { get; }

        public string RutaScripts { get; }

        public static EntornoTemporal Crear()
        {
            var raiz = Path.Combine(Path.GetTempPath(), "LanzadorScripts_Firmas_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(raiz);
            return new EntornoTemporal(raiz, raiz);
        }

        public static EntornoTemporal CrearConScripts()
        {
            var entorno = Crear();
            var scripts = Path.Combine(entorno.Raiz, "scripts");
            Directory.CreateDirectory(scripts);
            File.WriteAllText(Path.Combine(scripts, "ok.cmd"), "echo ok", Encoding.UTF8);
            return new EntornoTemporal(entorno.Raiz, scripts);
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
}
