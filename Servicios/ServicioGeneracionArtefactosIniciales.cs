// (Autor: Alex Roman)
// Descripcion: Genera permisos y catalogo firmados para la distribucion corporativa.

using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LanzadorScripts.Servicios;

public static class ServicioGeneracionArtefactosIniciales
{
    public const string ArgumentoGenerar = "--generar-artefactos-iniciales";

    public static readonly IReadOnlyList<string> AdministradoresPredeterminados =
    [
        @"MAD00\aroperez_micro",
        @"PCERA\alero"
    ];

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
            var administradores = DecodificarAdministradores(argumentos);
            var resultado = Generar(rutaScripts, rutaSalida, administradores);
            Console.WriteLine(
                $"Conjunto firmado creado. ConjuntoId={resultado.ConjuntoId}; Scripts={resultado.TotalScripts}; Salida={resultado.RutaSalida}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ServicioRedaccionSecretos.Sanitizar(ex.Message));
            return 1;
        }
    }

    public static ResultadoGeneracionConjuntoArtefactos Generar(
        string rutaScripts,
        string rutaSalida)
    {
        return Generar(rutaScripts, rutaSalida, AdministradoresPredeterminados);
    }

    public static ResultadoGeneracionConjuntoArtefactos Generar(
        string rutaScripts,
        string rutaSalida,
        IEnumerable<string> administradores)
    {
        return Generar(
            rutaScripts,
            rutaSalida,
            administradores,
            new ServicioArtefactosFirmados());
    }

    internal static ResultadoGeneracionConjuntoArtefactos Generar(
        string rutaScripts,
        string rutaSalida,
        IEnumerable<string> administradores,
        ServicioArtefactosFirmados artefactos,
        string? conjuntoId = null)
    {
        var administradoresValidados = ValidarAdministradores(administradores);
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
            "salida de artefactos firmados");
        Directory.CreateDirectory(rutaSalidaValidada);
        var conjuntoIdValidado = conjuntoId ?? ServicioArtefactosFirmados.CrearConjuntoId();
        ServicioArtefactosFirmados.ValidarConjuntoId(conjuntoIdValidado);

        var permisos = CrearPermisosIniciales(administradoresValidados);
        artefactos.GuardarTextoFirmado(
            Path.Combine(rutaSalidaValidada, RutasArtefactosProtegidos.NombrePermisos),
            ServicioArtefactosFirmados.TipoPermisos,
            permisos.ToJsonString(OpcionesJson),
            conjuntoIdValidado);

        var servicioCatalogo = new ServicioCatalogoScripts(artefactos);
        var catalogo = servicioCatalogo.Crear(
            scripts,
            scripts.Select(script => script.Id),
            conjuntoIdValidado);
        servicioCatalogo.Guardar(
            Path.Combine(rutaSalidaValidada, ServicioCatalogoScripts.NombreArchivo),
            catalogo);
        ValidarResultado(
            rutaSalidaValidada,
            scripts.Count,
            artefactos,
            servicioCatalogo,
            administradoresValidados,
            conjuntoIdValidado);

        return new ResultadoGeneracionConjuntoArtefactos(
            conjuntoIdValidado,
            scripts.Count,
            rutaSalidaValidada);
    }

    internal static JsonObject CrearPermisosIniciales(IEnumerable<string> administradores)
    {
        var usuarios = new JsonArray();
        var indice = 1;
        foreach (var administrador in ValidarAdministradores(administradores))
        {
            usuarios.Add(CrearAdministrador($"administrador-{indice}", administrador));
            indice++;
        }

        return new JsonObject
        {
            ["scriptsAdmin"] = new JsonArray(),
            ["usuarios"] = usuarios,
            ["seguridadScripts"] = new JsonObject
            {
                ["scriptsElevadosPermitidos"] = new JsonArray(),
                ["permitirExecutionPolicyBypass"] = false
            },
            ["rolUsuarioActual"] = "nominal",
            ["maxScriptsSimultaneos"] = 5
        };
    }

    internal static IReadOnlyList<string> ValidarAdministradores(IEnumerable<string> administradores)
    {
        ArgumentNullException.ThrowIfNull(administradores);
        var resultado = administradores
            .Select(ValidarAdministrador)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (resultado.Count != 2)
        {
            throw new ArgumentException(
                "El conjunto inicial debe contener exactamente dos administradores distintos.",
                nameof(administradores));
        }

        return resultado;
    }

    internal static string ValidarAdministrador(string administrador)
    {
        var valor = administrador?.Trim() ?? string.Empty;
        if (!PatronCuentaWindows.IsMatch(valor))
        {
            throw new ArgumentException(
                "El administrador debe usar el formato DOMINIO\\usuario.",
                nameof(administrador));
        }

        return valor;
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
        ServicioArtefactosFirmados artefactos,
        ServicioCatalogoScripts servicioCatalogo,
        IReadOnlyCollection<string> administradoresEsperados,
        string conjuntoIdEsperado)
    {
        var rutaPermisos = Path.Combine(rutaSalida, RutasArtefactosProtegidos.NombrePermisos);
        if (!artefactos.IntentarCargarTextoFirmado(
                rutaPermisos,
                ServicioArtefactosFirmados.TipoPermisos,
                out var permisosJson,
                out var conjuntoIdPermisos,
                out _,
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
            .Select(usuario => usuario!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (administradores is null || !administradores.SetEquals(administradoresEsperados))
        {
            throw new InvalidOperationException(
                "Los permisos iniciales no contienen exclusivamente los administradores requeridos.");
        }

        var rutaCatalogo = Path.Combine(rutaSalida, ServicioCatalogoScripts.NombreArchivo);
        if (!servicioCatalogo.IntentarCargar(rutaCatalogo, out var catalogo, out _)
            || catalogo?.Scripts.Count != totalScripts
            || !string.Equals(conjuntoIdPermisos, conjuntoIdEsperado, StringComparison.Ordinal)
            || !string.Equals(catalogo.ConjuntoId, conjuntoIdEsperado, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("No se pudo validar el conjunto firmado inicial.");
        }
    }

    private static IReadOnlyList<string> DecodificarAdministradores(string[] argumentos)
    {
        var indice = Array.FindIndex(
            argumentos,
            argumento => string.Equals(argumento, "--administradores-base64", StringComparison.OrdinalIgnoreCase));
        if (indice < 0)
        {
            return AdministradoresPredeterminados;
        }

        if (indice + 1 >= argumentos.Length)
        {
            throw new InvalidOperationException("Falta el valor requerido para --administradores-base64.");
        }

        var json = Encoding.UTF8.GetString(Convert.FromBase64String(argumentos[indice + 1]));
        return JsonSerializer.Deserialize<string[]>(json)
            ?? throw new InvalidOperationException("La lista de administradores no es valida.");
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
