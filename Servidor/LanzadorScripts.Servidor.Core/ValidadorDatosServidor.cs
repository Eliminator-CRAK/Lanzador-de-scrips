// (Autor: Alex Roman)
// Descripcion: Valida permisos y catalogos antes de almacenarlos.

using System.Text.Json.Nodes;
using LanzadorScripts.Protocolo;

namespace LanzadorScripts.Servidor.Core;

internal static class ValidadorDatosServidor
{
    private static readonly HashSet<string> PropiedadesPermisos = new(StringComparer.Ordinal)
    {
        "scriptsAdmin",
        "usuarios",
        "seguridadScripts",
        "rolUsuarioActual",
        "maxScriptsSimultaneos"
    };

    private static readonly HashSet<string> PropiedadesUsuario = new(StringComparer.Ordinal)
    {
        "id",
        "nombreUsuario",
        "rol",
        "maxScriptsSimultaneos",
        "carpetasPermitidas"
    };

    private static readonly HashSet<string> PropiedadesSeguridad = new(StringComparer.Ordinal)
    {
        "scriptsElevadosPermitidos",
        "permitirExecutionPolicyBypass"
    };

    private static readonly HashSet<string> PropiedadesCatalogo = new(StringComparer.Ordinal)
    {
        "version",
        "generadoUtc",
        "conjuntoId",
        "scripts"
    };

    private static readonly HashSet<string> PropiedadesEntradaCatalogo = new(StringComparer.Ordinal)
    {
        "scriptId",
        "extension",
        "longitud",
        "sha256"
    };

    public static DatosPermisosServidor ValidarPermisos(JsonObject permisos)
    {
        ArgumentNullException.ThrowIfNull(permisos);
        if (!TienePropiedadesExactas(permisos, PropiedadesPermisos)
            || permisos["scriptsAdmin"] is not JsonArray scriptsAdmin
            || permisos["usuarios"] is not JsonArray usuarios
            || usuarios.Count > 10_000
            || permisos["seguridadScripts"] is not JsonObject seguridad
            || !TienePropiedadesExactas(seguridad, PropiedadesSeguridad)
            || permisos["rolUsuarioActual"] is not JsonValue rolActualJson
            || !rolActualJson.TryGetValue<string>(out var rolActual)
            || permisos["maxScriptsSimultaneos"] is not JsonValue maximoJson
            || !maximoJson.TryGetValue<int>(out var maximoGlobal))
        {
            throw new InvalidDataException("El objeto de permisos no tiene el esquema esperado.");
        }

        var listaScriptsAdmin = LeerScripts(scriptsAdmin, "scriptsAdmin");
        var scriptsElevados = seguridad["scriptsElevadosPermitidos"] is JsonArray elevados
            ? LeerScripts(elevados, "scriptsElevadosPermitidos")
            : throw new InvalidDataException("La lista de scripts elevados no es valida.");
        if (seguridad["permitirExecutionPolicyBypass"] is not JsonValue bypassJson
            || !bypassJson.TryGetValue<bool>(out var permitirBypass))
        {
            throw new InvalidDataException("La politica de ExecutionPolicy no es valida.");
        }

        var resultadoUsuarios = new List<UsuarioServidorCentral>();
        var nombres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var nodo in usuarios)
        {
            if (nodo is not JsonObject usuario
                || !TienePropiedadesExactas(usuario, PropiedadesUsuario))
            {
                throw new InvalidDataException("La lista de permisos contiene un usuario no valido.");
            }

            var id = LeerTexto(usuario, "id", 100);
            var nombre = ConfiguracionServidor.NormalizarCuenta(LeerTexto(usuario, "nombreUsuario", 256));
            var rol = LeerTexto(usuario, "rol", 30).ToLowerInvariant();
            var maximo = LeerEntero(usuario, "maxScriptsSimultaneos", 1, 50);
            if (nombre.Length == 0
                || rol is not "admin" and not "nominal"
                || !ids.Add(id)
                || !nombres.Add(nombre))
            {
                throw new InvalidDataException("Los identificadores, cuentas o roles de permisos no son validos.");
            }

            if (usuario["carpetasPermitidas"] is not JsonArray carpetas)
            {
                throw new InvalidDataException("Las carpetas permitidas del usuario no son validas.");
            }

            if (carpetas.Count > 200)
            {
                throw new InvalidDataException("Un usuario supera el limite de carpetas permitidas.");
            }

            var carpetasNormalizadas = carpetas
                .Select(nodoCarpeta => nodoCarpeta?.GetValue<string>()?.Trim() ?? string.Empty)
                .Where(carpeta => carpeta.Length > 0)
                .Select(ValidarCarpetaRelativa)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(carpeta => carpeta, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            resultadoUsuarios.Add(new UsuarioServidorCentral(
                id,
                nombre,
                rol,
                maximo,
                carpetasNormalizadas,
                true));
        }

        if (!resultadoUsuarios.Any(usuario => usuario.Activo && usuario.Rol == "admin"))
        {
            throw new InvalidDataException("Debe existir al menos un administrador activo.");
        }

        if (rolActual is not "admin" and not "nominal"
            || maximoGlobal is < 1 or > 50)
        {
            throw new InvalidDataException("Los valores globales de permisos no son validos.");
        }

        return new DatosPermisosServidor(
            listaScriptsAdmin,
            scriptsElevados,
            permitirBypass,
            rolActual,
            maximoGlobal,
            resultadoUsuarios);
    }

    public static DatosCatalogoServidor ValidarCatalogo(JsonObject catalogo)
    {
        ArgumentNullException.ThrowIfNull(catalogo);
        if (!TienePropiedadesExactas(catalogo, PropiedadesCatalogo)
            || LeerEntero(catalogo, "version", 1, 1) != 1
            || catalogo["scripts"] is not JsonArray scripts
            || scripts.Count > 10_000)
        {
            throw new InvalidDataException("El catalogo no tiene el esquema esperado.");
        }

        var generadoTexto = LeerTexto(catalogo, "generadoUtc", 100);
        if (!DateTimeOffset.TryParse(generadoTexto, out var generadoUtc))
        {
            throw new InvalidDataException("La fecha del catalogo no es valida.");
        }

        var entradas = new List<EntradaCatalogoServidor>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var nodo in scripts)
        {
            if (nodo is not JsonObject entrada
                || !TienePropiedadesExactas(entrada, PropiedadesEntradaCatalogo))
            {
                throw new InvalidDataException("El catalogo contiene una entrada no valida.");
            }

            var scriptId = NormalizarScriptId(LeerTexto(entrada, "scriptId", 1024));
            var extension = LeerTexto(entrada, "extension", 10).ToLowerInvariant();
            var longitud = LeerLong(entrada, "longitud", 0, 512L * 1024 * 1024);
            var sha256 = LeerTexto(entrada, "sha256", 64).ToUpperInvariant();
            if (!ids.Add(scriptId)
                || extension is not ".ps1" and not ".bat" and not ".cmd"
                || !scriptId.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                || sha256.Length != 64
                || sha256.Any(caracter => !Uri.IsHexDigit(caracter)))
            {
                throw new InvalidDataException($"La entrada de catalogo no es valida: {scriptId}");
            }

            entradas.Add(new EntradaCatalogoServidor(scriptId, extension, longitud, sha256));
        }

        return new DatosCatalogoServidor(generadoUtc.ToUniversalTime(), entradas);
    }

    public static UsuarioServidorCentral ValidarUsuario(GuardarUsuarioServidorCentral usuario)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        var id = string.IsNullOrWhiteSpace(usuario.Id)
            ? Guid.NewGuid().ToString("N")
            : usuario.Id.Trim();
        var nombre = ConfiguracionServidor.NormalizarCuenta(usuario.NombreUsuario);
        var rol = usuario.Rol.Trim().ToLowerInvariant();
        if (id.Length is <= 0 or > 100
            || id.Any(caracter => !char.IsLetterOrDigit(caracter) && caracter is not '-' and not '_')
            || nombre.Length == 0
            || rol is not "admin" and not "nominal"
            || usuario.MaxScriptsSimultaneos is < 1 or > 50
            || usuario.CarpetasPermitidas.Count > 200)
        {
            throw new InvalidDataException("Los datos del usuario no son validos.");
        }

        var carpetas = usuario.CarpetasPermitidas
            .Select(ValidarCarpetaRelativa)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(carpeta => carpeta, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new UsuarioServidorCentral(
            id,
            nombre,
            rol,
            usuario.MaxScriptsSimultaneos,
            carpetas,
            usuario.Activo);
    }

    private static IReadOnlyList<string> LeerScripts(JsonArray valores, string campo)
    {
        if (valores.Count > 10000)
        {
            throw new InvalidDataException($"La lista {campo} supera el limite permitido.");
        }

        return valores
            .Select(nodo => NormalizarScriptId(nodo?.GetValue<string>() ?? string.Empty))
            .Where(valor => valor.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(valor => valor, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizarScriptId(string valor)
    {
        var original = valor.Trim();
        if (Path.IsPathRooted(original))
        {
            throw new InvalidDataException("El identificador de script debe ser relativo.");
        }

        var script = original.Replace('\\', '/');
        if (script.Length is <= 0 or > 1024
            || script.Split('/').Any(segmento => !EsSegmentoRutaSeguro(segmento)))
        {
            throw new InvalidDataException("El identificador de script no es seguro.");
        }

        return script;
    }

    private static string ValidarCarpetaRelativa(string valor)
    {
        var original = valor.Trim();
        if (Path.IsPathRooted(original))
        {
            throw new InvalidDataException("La carpeta permitida debe ser relativa.");
        }

        var carpeta = original.Replace('\\', '/');
        if (carpeta.Length is <= 0 or > 512
            || carpeta.Split('/').Any(segmento => !EsSegmentoRutaSeguro(segmento)))
        {
            throw new InvalidDataException("La carpeta permitida no es una ruta relativa segura.");
        }

        return carpeta;
    }

    private static bool EsSegmentoRutaSeguro(string segmento)
    {
        return segmento.Length is > 0 and <= 255
            && segmento is not "." and not ".."
            && !segmento.EndsWith(' ')
            && !segmento.EndsWith('.')
            && segmento.All(caracter => !char.IsControl(caracter)
                && caracter is not '<' and not '>' and not ':' and not '"'
                and not '|' and not '?' and not '*');
    }

    private static bool TienePropiedadesExactas(JsonObject objeto, IReadOnlySet<string> propiedades)
    {
        return objeto.Count == propiedades.Count
            && objeto.All(propiedad => propiedades.Contains(propiedad.Key));
    }

    private static string LeerTexto(JsonObject objeto, string propiedad, int maximo)
    {
        if (objeto[propiedad] is not JsonValue valor
            || !valor.TryGetValue<string>(out var texto))
        {
            throw new InvalidDataException($"El campo {propiedad} no es texto.");
        }

        texto = texto.Trim();
        if (texto.Length is <= 0 || texto.Length > maximo || texto.Any(char.IsControl))
        {
            throw new InvalidDataException($"El campo {propiedad} no tiene una longitud valida.");
        }

        return texto;
    }

    private static int LeerEntero(JsonObject objeto, string propiedad, int minimo, int maximo)
    {
        if (objeto[propiedad] is not JsonValue valor
            || !valor.TryGetValue<int>(out var numero)
            || numero < minimo
            || numero > maximo)
        {
            throw new InvalidDataException($"El campo {propiedad} no es un entero valido.");
        }

        return numero;
    }

    private static long LeerLong(JsonObject objeto, string propiedad, long minimo, long maximo)
    {
        if (objeto[propiedad] is not JsonValue valor
            || !valor.TryGetValue<long>(out var numero)
            || numero < minimo
            || numero > maximo)
        {
            throw new InvalidDataException($"El campo {propiedad} no es un numero valido.");
        }

        return numero;
    }
}

internal sealed record DatosPermisosServidor(
    IReadOnlyList<string> ScriptsAdmin,
    IReadOnlyList<string> ScriptsElevados,
    bool PermitirExecutionPolicyBypass,
    string RolUsuarioActual,
    int MaxScriptsSimultaneos,
    IReadOnlyList<UsuarioServidorCentral> Usuarios);

internal sealed record DatosCatalogoServidor(
    DateTimeOffset GeneradoUtc,
    IReadOnlyList<EntradaCatalogoServidor> Scripts);

internal sealed record EntradaCatalogoServidor(
    string ScriptId,
    string Extension,
    long Longitud,
    string Sha256);
