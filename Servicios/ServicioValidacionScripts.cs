// (Autor: Alex Roman)
// Descripcion: Valida rutas y scripts antes de mostrarlos o ejecutarlos.

using System.IO;

namespace LanzadorScripts.Servicios;

public sealed class ServicioValidacionScripts
{
    private static readonly HashSet<string> ExtensionesPermitidas = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1",
        ".bat",
        ".cmd"
    };

    private static readonly HashSet<string> CarpetasExcluidas = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        "PERMISOS",
        "node_modules",
        "bin",
        "obj"
    };

    public ResultadoValidacionConfiguracion ValidarConfiguracionBasica(string rutaScripts, string rutaPermisos)
    {
        if (string.IsNullOrWhiteSpace(rutaScripts))
        {
            return ResultadoValidacionConfiguracion.Error("La ruta de scripts no puede estar vacia.");
        }

        if (ContieneCaracteresInvalidos(rutaScripts))
        {
            return ResultadoValidacionConfiguracion.Error("La ruta de scripts contiene caracteres no validos.");
        }

        if (string.IsNullOrWhiteSpace(rutaPermisos))
        {
            return ResultadoValidacionConfiguracion.Error("La ruta de la carpeta de permisos no puede estar vacia.");
        }

        if (ContieneCaracteresInvalidos(rutaPermisos))
        {
            return ResultadoValidacionConfiguracion.Error("La ruta de permisos contiene caracteres no validos.");
        }

        try
        {
            _ = RutasArtefactosProtegidos.Resolver(rutaPermisos);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or NotSupportedException)
        {
            return ResultadoValidacionConfiguracion.Error(ex.Message);
        }

        return ResultadoValidacionConfiguracion.Correcta();
    }

    public string CrearAvisoConfiguracionNoDisponible(string rutaScripts, string rutaPermisos)
    {
        // Genera avisos operativos sin bloquear el guardado de rutas.
        var avisos = new List<string>();
        var rutaScriptsCompleta = ResolverRutaCompletaSegura(rutaScripts);
        if (rutaScriptsCompleta is null || !Directory.Exists(rutaScriptsCompleta))
        {
            avisos.Add($"La carpeta de scripts no esta disponible: {rutaScripts.Trim()}.");
        }

        var rutasArtefactos = ResolverRutasArtefactosSegura(rutaPermisos);
        if (rutasArtefactos is null)
        {
            avisos.Add($"La carpeta de permisos no se pudo resolver: {rutaPermisos.Trim()}.");
            return string.Join(" ", avisos);
        }

        if (!Directory.Exists(rutasArtefactos.Carpeta))
        {
            avisos.Add($"La carpeta de permisos no esta disponible: {rutasArtefactos.Carpeta}.");
        }
        else
        {
            if (!File.Exists(rutasArtefactos.RutaPermisos))
            {
                avisos.Add($"El archivo de permisos no existe: {rutasArtefactos.RutaPermisos}.");
            }

            if (!File.Exists(rutasArtefactos.RutaCatalogo))
            {
                avisos.Add($"El catalogo de scripts no existe: {rutasArtefactos.RutaCatalogo}.");
            }
        }

        return string.Join(" ", avisos);
    }

    public ResultadoValidacionScript ValidarScriptParaEjecucion(string rutaScripts, string identificador)
    {
        var resultadoRaiz = ObtenerRaizSegura(rutaScripts);
        if (!resultadoRaiz.EsValida)
        {
            return ResultadoValidacionScript.Error(resultadoRaiz.Codigo, resultadoRaiz.Mensaje);
        }

        var resultadoIdentificador = ValidarIdentificadorRelativo(identificador);
        if (!resultadoIdentificador.EsValida)
        {
            return ResultadoValidacionScript.Error(resultadoIdentificador.Codigo, resultadoIdentificador.Mensaje);
        }

        var rutaCompleta = Path.GetFullPath(Path.Combine(resultadoRaiz.RutaCompleta!, NormalizarSeparadores(identificador)));
        if (!EstaDentroDeRaiz(resultadoRaiz.RutaCompleta!, rutaCompleta))
        {
            return ResultadoValidacionScript.Error(CodigoValidacionScript.ScriptFueraDeRaiz, "El script queda fuera de la ruta autorizada.");
        }

        if (!File.Exists(rutaCompleta))
        {
            return ResultadoValidacionScript.Error(CodigoValidacionScript.ScriptNoEncontrado, "El script no existe o no esta disponible.");
        }

        if (ContieneEnlaceNoPermitido(resultadoRaiz.RutaCompleta!, rutaCompleta))
        {
            return ResultadoValidacionScript.Error(CodigoValidacionScript.EnlaceNoPermitido, "El script usa enlaces de sistema no permitidos.");
        }

        var script = CrearScriptDesdeRuta(resultadoRaiz.RutaCompleta!, rutaCompleta);
        return script is null
            ? ResultadoValidacionScript.Error(CodigoValidacionScript.IdentificadorNoPermitido, "El script no cumple las reglas de seguridad.")
            : ResultadoValidacionScript.Correcto(script);
    }

    internal ResultadoValidacionScript ValidarRutaConocida(
        string raizAutorizada,
        string rutaCompleta,
        string identificador,
        string nombre,
        string tipo)
    {
        // Revalida rutas internas antes de ejecutar o elevar.
        var resultadoRaiz = ObtenerRaizSegura(raizAutorizada);
        if (!resultadoRaiz.EsValida)
        {
            return ResultadoValidacionScript.Error(resultadoRaiz.Codigo, resultadoRaiz.Mensaje);
        }

        var resultadoIdentificador = ValidarIdentificadorRelativo(identificador);
        if (!resultadoIdentificador.EsValida)
        {
            return ResultadoValidacionScript.Error(resultadoIdentificador.Codigo, resultadoIdentificador.Mensaje);
        }

        string rutaSegura;
        try
        {
            rutaSegura = ServicioRutasSeguras.ResolverArchivoAbsoluto(
                rutaCompleta,
                "script validado",
                ".ps1",
                ".bat",
                ".cmd");
        }
        catch (InvalidOperationException ex)
        {
            return ResultadoValidacionScript.Error(
                CodigoValidacionScript.ScriptFueraDeRaiz,
                ex.Message);
        }

        if (!EstaDentroDeRaiz(resultadoRaiz.RutaCompleta!, rutaSegura))
        {
            return ResultadoValidacionScript.Error(
                CodigoValidacionScript.ScriptFueraDeRaiz,
                "El script queda fuera de la ruta autorizada.");
        }

        if (!File.Exists(rutaSegura))
        {
            return ResultadoValidacionScript.Error(
                CodigoValidacionScript.ScriptNoEncontrado,
                "El script no existe o no esta disponible.");
        }

        if (ContieneEnlaceNoPermitido(resultadoRaiz.RutaCompleta!, rutaSegura))
        {
            return ResultadoValidacionScript.Error(
                CodigoValidacionScript.EnlaceNoPermitido,
                "El script usa enlaces de sistema no permitidos.");
        }

        var extension = Path.GetExtension(rutaSegura);
        var tipoEsperado = extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
            ? "powershell"
            : "batch";
        if (!string.Equals(nombre, Path.GetFileName(rutaSegura), StringComparison.Ordinal)
            || !string.Equals(tipo, tipoEsperado, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(identificador), extension, StringComparison.OrdinalIgnoreCase))
        {
            return ResultadoValidacionScript.Error(
                CodigoValidacionScript.IdentificadorNoPermitido,
                "Los metadatos del script no coinciden con la ruta validada.");
        }

        var rutaValidada = new RutaScriptValidada(
            resultadoRaiz.RutaCompleta!,
            rutaSegura,
            identificador,
            extension.ToLowerInvariant());
        return ResultadoValidacionScript.Correcto(
            new ScriptInterno(identificador, nombre, tipoEsperado, rutaValidada));
    }

    public IReadOnlyList<ScriptInterno> DescubrirScripts(string rutaScripts)
    {
        var resultadoRaiz = ObtenerRaizSegura(rutaScripts);
        if (!resultadoRaiz.EsValida)
        {
            return [];
        }

        var scripts = new List<ScriptInterno>();
        foreach (var ruta in EnumerarScriptsPermitidos(resultadoRaiz.RutaCompleta!))
        {
            var script = CrearScriptDesdeRuta(resultadoRaiz.RutaCompleta!, ruta);
            if (script is not null)
            {
                scripts.Add(script);
            }
        }

        return scripts
            .OrderBy(script => script.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> DescubrirCarpetasScripts(string rutaScripts)
    {
        var resultadoRaiz = ObtenerRaizSegura(rutaScripts);
        if (!resultadoRaiz.EsValida)
        {
            return [];
        }

        return EnumerarCarpetasPermitidas(resultadoRaiz.RutaCompleta!)
            .Select(ruta => Path.GetRelativePath(resultadoRaiz.RutaCompleta!, ruta).Replace('\\', '/').Trim('/'))
            .Where(carpeta => !string.IsNullOrWhiteSpace(carpeta) && EsIdentificadorCarpetaSeguro(carpeta))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(carpeta => carpeta, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string ResolverRutaPermisos(string rutaScripts, string rutaPermisos)
    {
        _ = rutaScripts;
        return RutasArtefactosProtegidos.Resolver(rutaPermisos).RutaPermisos;
    }

    public RutasArtefactos ResolverRutasArtefactos(string rutaCarpetaPermisos)
    {
        return RutasArtefactosProtegidos.Resolver(rutaCarpetaPermisos);
    }

    private static string? ResolverRutaCompletaSegura(string ruta)
    {
        // Resuelve rutas para avisos sin lanzar errores al cliente.
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(ruta.Trim()));
        }
        catch
        {
            return null;
        }
    }

    private static RutasArtefactos? ResolverRutasArtefactosSegura(string rutaPermisos)
    {
        // Resuelve la carpeta y los dos archivos para diagnostico.
        try
        {
            return RutasArtefactosProtegidos.Resolver(rutaPermisos);
        }
        catch
        {
            return null;
        }
    }

    public static int ObtenerCodigoHttp(CodigoValidacionScript codigo)
    {
        return codigo switch
        {
            CodigoValidacionScript.RutaScriptsNoConfigurada => 409,
            CodigoValidacionScript.RutaScriptsNoDisponible => 409,
            CodigoValidacionScript.ScriptNoEncontrado => 404,
            CodigoValidacionScript.ExtensionNoPermitida => 403,
            CodigoValidacionScript.CarpetaExcluida => 403,
            CodigoValidacionScript.ScriptFueraDeRaiz => 403,
            CodigoValidacionScript.EnlaceNoPermitido => 403,
            CodigoValidacionScript.MetacaracterPeligroso => 403,
            _ => 400
        };
    }

    private static IEnumerable<string> EnumerarScriptsPermitidos(string raiz)
    {
        var carpetas = new Stack<string>();
        carpetas.Push(raiz);

        while (carpetas.Count > 0)
        {
            var actual = carpetas.Pop();
            if (EsCarpetaExcluida(actual) || TieneAtributoReparsePoint(actual))
            {
                continue;
            }

            IEnumerable<string> archivos;
            try
            {
                archivos = Directory.EnumerateFiles(actual)
                    .Where(EsExtensionPermitida)
                    .ToList();
            }
            catch
            {
                continue;
            }

            foreach (var archivo in archivos)
            {
                yield return archivo;
            }

            IEnumerable<string> hijas;
            try
            {
                hijas = Directory.EnumerateDirectories(actual).ToList();
            }
            catch
            {
                continue;
            }

            foreach (var carpeta in hijas)
            {
                if (!EsCarpetaExcluida(carpeta))
                {
                    carpetas.Push(carpeta);
                }
            }
        }
    }

    private static IEnumerable<string> EnumerarCarpetasPermitidas(string raiz)
    {
        var carpetas = new Stack<string>();
        carpetas.Push(raiz);

        while (carpetas.Count > 0)
        {
            var actual = carpetas.Pop();
            IEnumerable<string> hijas;
            try
            {
                hijas = Directory.EnumerateDirectories(actual).ToList();
            }
            catch
            {
                continue;
            }

            foreach (var carpeta in hijas)
            {
                if (EsCarpetaExcluida(carpeta) || TieneAtributoReparsePoint(carpeta))
                {
                    continue;
                }

                yield return carpeta;
                carpetas.Push(carpeta);
            }
        }
    }

    private static ScriptInterno? CrearScriptDesdeRuta(string raiz, string rutaCompleta)
    {
        if (!EstaDentroDeRaiz(raiz, rutaCompleta)
            || !EsExtensionPermitida(rutaCompleta)
            || ContieneCarpetaExcluida(raiz, rutaCompleta)
            || ContieneEnlaceNoPermitido(raiz, rutaCompleta))
        {
            return null;
        }

        var relativo = Path.GetRelativePath(raiz, rutaCompleta).Replace('\\', '/');
        if (ServicioSeguridadScripts.ContieneMetacaracteresPeligrosos(relativo)
            || ServicioSeguridadScripts.ContieneMetacaracteresPeligrosos(Path.GetFileName(rutaCompleta)))
        {
            return null;
        }

        var tipo = Path.GetExtension(rutaCompleta).Equals(".ps1", StringComparison.OrdinalIgnoreCase)
            ? "powershell"
            : "batch";

        var rutaValidada = new RutaScriptValidada(
            raiz,
            rutaCompleta,
            relativo,
            Path.GetExtension(rutaCompleta).ToLowerInvariant());
        return new ScriptInterno(
            relativo,
            Path.GetFileName(rutaCompleta),
            tipo,
            rutaValidada);
    }

    private static ResultadoRaiz ObtenerRaizSegura(string rutaScripts)
    {
        if (string.IsNullOrWhiteSpace(rutaScripts))
        {
            return ResultadoRaiz.Error(CodigoValidacionScript.RutaScriptsNoConfigurada, "La ruta de scripts no esta configurada.");
        }

        if (ContieneCaracteresInvalidos(rutaScripts))
        {
            return ResultadoRaiz.Error(CodigoValidacionScript.RutaScriptsNoConfigurada, "La ruta de scripts contiene caracteres no validos.");
        }

        var rutaExpandida = Environment.ExpandEnvironmentVariables(rutaScripts.Trim());
        var rutaCompleta = Path.GetFullPath(rutaExpandida).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(rutaCompleta))
        {
            return ResultadoRaiz.Error(CodigoValidacionScript.RutaScriptsNoDisponible, "La ruta de scripts no esta disponible.");
        }

        if (TieneAtributoReparsePoint(rutaCompleta))
        {
            return ResultadoRaiz.Error(CodigoValidacionScript.EnlaceNoPermitido, "La ruta de scripts usa enlaces de sistema no permitidos.");
        }

        return ResultadoRaiz.Correcta(rutaCompleta);
    }

    private static ResultadoIdentificador ValidarIdentificadorRelativo(string identificador)
    {
        if (string.IsNullOrWhiteSpace(identificador))
        {
            return ResultadoIdentificador.Error(CodigoValidacionScript.IdentificadorVacio, "No se indico el script a ejecutar.");
        }

        if (ContieneCaracteresInvalidos(identificador) || identificador.Contains('\0'))
        {
            return ResultadoIdentificador.Error(CodigoValidacionScript.IdentificadorNoPermitido, "El identificador del script contiene caracteres no validos.");
        }

        if (ServicioSeguridadScripts.ContieneMetacaracteresPeligrosos(identificador))
        {
            return ResultadoIdentificador.Error(CodigoValidacionScript.MetacaracterPeligroso, "El identificador del script contiene metacaracteres peligrosos.");
        }

        if (Path.IsPathRooted(identificador) || Path.IsPathFullyQualified(identificador))
        {
            return ResultadoIdentificador.Error(CodigoValidacionScript.IdentificadorNoRelativo, "El identificador del script debe ser relativo.");
        }

        var segmentos = identificador.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segmentos.Length == 0 || segmentos.Any(segmento => segmento == "." || segmento == ".."))
        {
            return ResultadoIdentificador.Error(CodigoValidacionScript.IdentificadorNoPermitido, "El identificador del script no es seguro.");
        }

        if (segmentos.Any(segmento => CarpetasExcluidas.Contains(segmento)))
        {
            return ResultadoIdentificador.Error(CodigoValidacionScript.CarpetaExcluida, "La carpeta del script no esta permitida.");
        }

        if (!EsExtensionPermitida(identificador))
        {
            return ResultadoIdentificador.Error(CodigoValidacionScript.ExtensionNoPermitida, "Solo se permiten scripts PowerShell o batch autorizados.");
        }

        return ResultadoIdentificador.Correcto();
    }

    private static bool EstaDentroDeRaiz(string raiz, string ruta)
    {
        var relativo = Path.GetRelativePath(raiz, ruta);
        return !string.IsNullOrWhiteSpace(relativo)
            && relativo != "."
            && !relativo.StartsWith("..", StringComparison.Ordinal)
            && !Path.IsPathRooted(relativo);
    }

    private static bool ContieneCarpetaExcluida(string raiz, string ruta)
    {
        var relativo = Path.GetRelativePath(raiz, ruta);
        var segmentos = relativo.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return segmentos.Take(Math.Max(0, segmentos.Length - 1)).Any(segmento => CarpetasExcluidas.Contains(segmento));
    }

    private static bool ContieneEnlaceNoPermitido(string raiz, string ruta)
    {
        if (TieneAtributoReparsePoint(ruta))
        {
            return true;
        }

        var actual = Path.GetDirectoryName(ruta);
        while (!string.IsNullOrWhiteSpace(actual) && EstaDentroDeRaiz(raiz, actual))
        {
            if (TieneAtributoReparsePoint(actual))
            {
                return true;
            }

            actual = Path.GetDirectoryName(actual);
        }

        return false;
    }

    private static bool EsCarpetaExcluida(string ruta)
    {
        return CarpetasExcluidas.Contains(Path.GetFileName(ruta));
    }

    private static bool EsExtensionPermitida(string ruta)
    {
        return ExtensionesPermitidas.Contains(Path.GetExtension(ruta));
    }

    private static bool EsIdentificadorCarpetaSeguro(string carpeta)
    {
        if (string.IsNullOrWhiteSpace(carpeta)
            || Path.IsPathRooted(carpeta)
            || Path.IsPathFullyQualified(carpeta)
            || ServicioSeguridadScripts.ContieneMetacaracteresPeligrosos(carpeta))
        {
            return false;
        }

        var segmentos = carpeta.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return segmentos.Length > 0
            && segmentos.All(segmento => segmento != "." && segmento != "..")
            && string.IsNullOrWhiteSpace(Path.GetExtension(carpeta));
    }

    private static bool TieneAtributoReparsePoint(string ruta)
    {
        try
        {
            return (File.GetAttributes(ruta) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }
        catch
        {
            return true;
        }
    }

    private static bool ContieneCaracteresInvalidos(string texto)
    {
        return texto.IndexOfAny(Path.GetInvalidPathChars()) >= 0;
    }

    private static string NormalizarSeparadores(string ruta)
    {
        return ruta.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }

    private sealed record ResultadoRaiz(bool EsValida, CodigoValidacionScript Codigo, string Mensaje, string? RutaCompleta)
    {
        public static ResultadoRaiz Correcta(string rutaCompleta)
        {
            return new ResultadoRaiz(true, CodigoValidacionScript.Correcto, string.Empty, rutaCompleta);
        }

        public static ResultadoRaiz Error(CodigoValidacionScript codigo, string mensaje)
        {
            return new ResultadoRaiz(false, codigo, mensaje, null);
        }
    }

    private sealed record ResultadoIdentificador(bool EsValida, CodigoValidacionScript Codigo, string Mensaje)
    {
        public static ResultadoIdentificador Correcto()
        {
            return new ResultadoIdentificador(true, CodigoValidacionScript.Correcto, string.Empty);
        }

        public static ResultadoIdentificador Error(CodigoValidacionScript codigo, string mensaje)
        {
            return new ResultadoIdentificador(false, codigo, mensaje);
        }
    }
}
