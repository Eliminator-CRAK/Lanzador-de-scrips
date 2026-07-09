// (Autor: Alex Roman)
// Descripcion: Genera permisos y catalogo iniciales para la publicacion portable.

using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LanzadorScripts.Servicios;

public static class ServicioGeneracionArtefactosIniciales
{
    public const string ArgumentoGenerar = "--generar-artefactos-iniciales";

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
            Generar(rutaScripts, rutaSalida);
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
        var validador = new ServicioValidacionScripts();
        var scripts = validador.DescubrirScripts(rutaScripts);
        if (scripts.Count == 0)
        {
            throw new InvalidOperationException("No se encontraron scripts para generar el catalogo inicial.");
        }

        foreach (var script in scripts.Where(script => script.Tipo == "powershell"))
        {
            var texto = File.ReadAllText(script.RutaCompleta, Encoding.UTF8);
            if (texto.Contains("# SIG # Begin signature block", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"El script conserva una firma Authenticode y debe limpiarse antes de publicar: {script.Id}");
            }
        }

        Directory.CreateDirectory(rutaSalida);
        var artefactos = new ServicioArtefactosProtegidos();
        var permisos = CrearPermisosIniciales();
        artefactos.GuardarTextoProtegido(
            Path.Combine(rutaSalida, RutasArtefactosProtegidos.NombrePermisos),
            ServicioArtefactosProtegidos.TipoPermisos,
            permisos.ToJsonString(OpcionesJson));

        var servicioCatalogo = new ServicioCatalogoScripts(artefactos);
        var catalogo = servicioCatalogo.Crear(scripts, scripts.Select(script => script.Id));
        servicioCatalogo.Guardar(
            Path.Combine(rutaSalida, ServicioCatalogoScripts.NombreArchivo),
            catalogo);
        ValidarResultado(rutaSalida, scripts.Count, artefactos, servicioCatalogo);
    }

    internal static JsonObject CrearPermisosIniciales()
    {
        return new JsonObject
        {
            ["scriptsAdmin"] = new JsonArray(),
            ["usuarios"] = new JsonArray
            {
                CrearAdministrador("pcera-alero", @"PCERA\alero"),
                CrearAdministrador("mad00-aroperez-micro", @"MAD00\aroperez_micro")
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
        ServicioCatalogoScripts servicioCatalogo)
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
            || !administradores.SetEquals(new[] { @"PCERA\alero", @"MAD00\aroperez_micro" }))
        {
            throw new InvalidOperationException("Los permisos iniciales no contienen los administradores requeridos.");
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
}
