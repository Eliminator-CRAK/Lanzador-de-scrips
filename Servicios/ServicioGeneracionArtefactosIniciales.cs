// (Autor: Alex Roman)
// Descripcion: Genera permisos y catalogo iniciales para la publicacion portable.

using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LanzadorScripts.Servicios;

public static class ServicioGeneracionArtefactosIniciales
{
    public const string ArgumentoGenerar = "--generar-artefactos-iniciales";
    public const string AdministradorPredeterminado = @"MAD00\aroperez_micro";

    private static readonly Regex PatronCuentaWindows = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\\[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static bool EsSolicitud(string[] argumentos)
    {
        return argumentos.Any(argumento =>
            string.Equals(argumento, ArgumentoGenerar, StringComparison.OrdinalIgnoreCase));
    }

    public static int Ejecutar(string[] argumentos)
    {
        try
        {
            var rutaScripts = DecodificarArgumento(argumentos, "--scripts-base64");
            var rutaSalida = DecodificarArgumento(argumentos, "--salida-base64");
            var administrador = DecodificarArgumentoOpcional(
                argumentos,
                "--administrador-base64",
                AdministradorPredeterminado);
            Generar(rutaScripts, rutaSalida, administrador);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ServicioRedaccionSecretos.Sanitizar(ex.Message));
            return 1;
        }
    }

    public static void Generar(string rutaScripts, string rutaSalida)
    {
        Generar(rutaScripts, rutaSalida, AdministradorPredeterminado);
    }

    public static void Generar(
        string rutaScripts,
        string rutaSalida,
        string administrador)
    {
        Generar(
            rutaScripts,
            rutaSalida,
            new ServicioArtefactosProtegidos(),
            administrador);
    }

    internal static void Generar(
        string rutaScripts,
        string rutaSalida,
        ServicioArtefactosProtegidos artefactos)
    {
        Generar(rutaScripts, rutaSalida, artefactos, AdministradorPredeterminado);
    }

    internal static void Generar(
        string rutaScripts,
        string rutaSalida,
        ServicioArtefactosProtegidos artefactos,
        string administrador)
    {
        // Valida la identidad antes de crear los artefactos.
        var administradorValidado = ValidarAdministrador(administrador);
        var validador = new ServicioValidacionScripts();
        var scripts = validador.DescubrirScripts(rutaScripts);
        if (scripts.Count == 0)
        {
            throw new InvalidOperationException("No se encontraron scripts para generar el catalogo inicial.");
        }

        foreach (var script in scripts.Where(script => script.Tipo == "powershell"))
        {
            var texto = script.RutaValidada.LeerTexto(Encoding.UTF8);
            if (texto.Contains("# SIG # Begin signature block", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"El script conserva una firma Authenticode y debe limpiarse antes de publicar: {script.Id}");
            }
        }

        var rutaSalidaValidada = ServicioRutasSeguras.ResolverCarpetaAbsoluta(
            rutaSalida,
            "salida de artefactos iniciales");
        Directory.CreateDirectory(rutaSalidaValidada);
        var permisos = CrearPermisosIniciales(administradorValidado);
        artefactos.GuardarTextoProtegido(
            Path.Combine(rutaSalidaValidada, RutasArtefactosProtegidos.NombrePermisos),
            ServicioArtefactosProtegidos.TipoPermisos,
            permisos.ToJsonString(OpcionesJson));

        var servicioCatalogo = new ServicioCatalogoScripts(artefactos);
        var catalogo = servicioCatalogo.Crear(scripts, scripts.Select(script => script.Id));
        servicioCatalogo.Guardar(
            Path.Combine(rutaSalidaValidada, ServicioCatalogoScripts.NombreArchivo),
            catalogo);
        ValidarResultado(
            rutaSalidaValidada,
            scripts.Count,
            artefactos,
            servicioCatalogo,
            administradorValidado);
    }

    internal static JsonObject CrearPermisosIniciales(string administrador)
    {
        // Crea un unico administrador principal para el paquete inicial.
        return new JsonObject
        {
            ["scriptsAdmin"] = new JsonArray(),
            ["usuarios"] = new JsonArray
            {
                CrearAdministrador("administrador-principal", administrador)
            },
            ["seguridadScripts"] = new JsonObject
            {
                ["scriptsElevadosPermitidos"] = new JsonArray(),
                ["permitirExecutionPolicyBypass"] = false
            },
            ["rolUsuarioActual"] = "nominal",
            ["maxScriptsSimultaneos"] = 5
        };
    }

    private static JsonObject CrearAdministrador(string id, string nombreUsuario)
    {
        return new JsonObject
        {
            ["id"] = id,
            ["nombreUsuario"] = nombreUsuario,
            ["rol"] = "admin",
            ["maxScriptsSimultaneos"] = 5,
            ["carpetasPermitidas"] = new JsonArray()
        };
    }

    private static void ValidarResultado(
        string rutaSalida,
        int totalScripts,
        ServicioArtefactosProtegidos artefactos,
        ServicioCatalogoScripts servicioCatalogo,
        string administradorEsperado)
    {
        var rutaPermisos = Path.Combine(rutaSalida, RutasArtefactosProtegidos.NombrePermisos);
        var permisosProtegidos = File.ReadAllText(rutaPermisos, Encoding.UTF8);
        if (!artefactos.IntentarDesprotegerTexto(
                ServicioArtefactosProtegidos.TipoPermisos,
                permisosProtegidos,
                out var permisosJson,
                out _))
        {
            throw new InvalidOperationException("No se pudieron validar los permisos iniciales.");
        }

        var permisos = JsonNode.Parse(permisosJson) as JsonObject;
        var administradores = permisos?["usuarios"]?.AsArray()
            .Where(usuario => string.Equals(
                usuario?["rol"]?.GetValue<string>(),
                "admin",
                StringComparison.OrdinalIgnoreCase))
            .Select(usuario => usuario?["nombreUsuario"]?.GetValue<string>())
            .Where(usuario => !string.IsNullOrWhiteSpace(usuario))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (administradores is null
            || !administradores.SetEquals([administradorEsperado]))
        {
            throw new InvalidOperationException(
                "Los permisos iniciales no contienen exclusivamente el administrador requerido.");
        }

        var rutaCatalogo = Path.Combine(rutaSalida, ServicioCatalogoScripts.NombreArchivo);
        if (!servicioCatalogo.IntentarCargar(rutaCatalogo, out var catalogo, out _)
            || catalogo?.Scripts.Count != totalScripts)
        {
            throw new InvalidOperationException("No se pudo validar el catalogo inicial.");
        }
    }

    private static string DecodificarArgumento(string[] argumentos, string nombre)
    {
        var indice = Array.FindIndex(
            argumentos,
            argumento => string.Equals(argumento, nombre, StringComparison.OrdinalIgnoreCase));
        if (indice < 0 || indice + 1 >= argumentos.Length)
        {
            throw new InvalidOperationException($"Falta el argumento requerido {nombre}.");
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(argumentos[indice + 1]));
    }

    private static string DecodificarArgumentoOpcional(
        string[] argumentos,
        string nombre,
        string valorPredeterminado)
    {
        // Conserva compatibilidad con publicaciones que no indican administrador.
        var indice = Array.FindIndex(
            argumentos,
            argumento => string.Equals(argumento, nombre, StringComparison.OrdinalIgnoreCase));
        return indice < 0
            ? valorPredeterminado
            : indice + 1 < argumentos.Length
                ? Encoding.UTF8.GetString(Convert.FromBase64String(argumentos[indice + 1]))
                : throw new InvalidOperationException($"Falta el valor requerido para {nombre}.");
    }

    internal static string ValidarAdministrador(string administrador)
    {
        // Acepta solo el formato DOMINIO\\usuario sin caracteres de control o ruta.
        var valor = administrador?.Trim() ?? string.Empty;
        if (!PatronCuentaWindows.IsMatch(valor))
        {
            throw new ArgumentException(
                "El administrador debe usar el formato DOMINIO\\usuario.",
                nameof(administrador));
        }

        return valor;
    }
}
