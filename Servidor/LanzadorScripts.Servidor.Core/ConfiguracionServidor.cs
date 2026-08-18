// (Autor: Alex Roman)
// Descripcion: Define y persiste la configuracion local del servidor.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanzadorScripts.Servidor.Core;

public sealed class ConfiguracionServidor
{
    public const int VersionActual = 1;
    public const int PuertoPredeterminado = 47831;

    public int Version { get; set; } = VersionActual;

    public int Puerto { get; set; } = PuertoPredeterminado;

    public int MaximoConexiones { get; set; } = 64;

    public int DiasRetencionAuditoria { get; set; } = 3650;

    public string RutaScripts { get; set; } = @"R:\SCRIPS";

    public void Validar()
    {
        if (Version != VersionActual)
        {
            throw new InvalidDataException("La configuracion del servidor tiene una version no compatible.");
        }

        if (Puerto is < 1024 or > 65535)
        {
            throw new InvalidDataException("El puerto del servidor debe estar entre 1024 y 65535.");
        }

        MaximoConexiones = Math.Clamp(MaximoConexiones, 4, 256);
        DiasRetencionAuditoria = Math.Clamp(DiasRetencionAuditoria, 30, 36500);
        if (string.IsNullOrWhiteSpace(RutaScripts)
            || RutaScripts.Contains("..", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(RutaScripts))
        {
            throw new InvalidDataException("La carpeta local de scripts del servidor no es valida.");
        }

        RutaScripts = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RutaScripts.Trim()));
    }

    public static string NormalizarCuenta(string? cuenta)
    {
        var valor = cuenta?.Trim() ?? string.Empty;
        if (valor.Length is <= 0 or > 256
            || valor.Any(caracter => char.IsControl(caracter))
            || valor.Count(caracter => caracter == '\\') != 1)
        {
            return string.Empty;
        }

        var partes = valor.Split('\\');
        return partes.Any(parte => parte.Length == 0 || parte is "." or "..")
            ? string.Empty
            : $"{partes[0]}\\{partes[1]}";
    }
}

public sealed class AlmacenConfiguracionServidor
{
    private const string PropiedadAdministradoresLegada = "administradoresIniciales";
    private static readonly UTF8Encoding Utf8Estricto = new(false, true);

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    private readonly RutasServidor _rutas;

    public AlmacenConfiguracionServidor(RutasServidor rutas)
    {
        _rutas = rutas;
    }

    public ConfiguracionServidor CargarOCrear()
    {
        _rutas.PrepararDirectorios();
        if (!File.Exists(_rutas.RutaConfiguracion))
        {
            var inicial = new ConfiguracionServidor();
            Guardar(inicial);
            return inicial;
        }

        RutasServidor.RechazarPuntoReanalisis(_rutas.RutaConfiguracion);
        var bytes = File.ReadAllBytes(_rutas.RutaConfiguracion);
        if (bytes.Length is <= 0 or > 128 * 1024)
        {
            throw new InvalidDataException("El archivo de configuracion del servidor tiene un tamano no valido.");
        }

        var bytesCompatibles = RetirarAdministradoresLegados(bytes, out var migrada);
        var configuracion = JsonSerializer.Deserialize<ConfiguracionServidor>(bytesCompatibles, OpcionesJson)
            ?? throw new InvalidDataException("La configuracion del servidor esta vacia.");
        configuracion.Validar();
        if (migrada)
        {
            Guardar(configuracion);
        }

        return configuracion;
    }

    public void Guardar(ConfiguracionServidor configuracion)
    {
        ArgumentNullException.ThrowIfNull(configuracion);
        configuracion.Validar();
        _rutas.PrepararDirectorios();
        var contenido = JsonSerializer.Serialize(configuracion, OpcionesJson);
        var temporal = _rutas.RutaConfiguracion + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var flujo = new FileStream(
                temporal,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            using (var escritor = new StreamWriter(flujo, Utf8Estricto))
            {
                escritor.Write(contenido);
                escritor.Flush();
                flujo.Flush(flushToDisk: true);
            }

            File.Move(temporal, _rutas.RutaConfiguracion, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporal))
            {
                File.Delete(temporal);
            }
        }
    }

    private static byte[] RetirarAdministradoresLegados(byte[] contenido, out bool migrada)
    {
        // Migra la propiedad antigua sin aceptar campos o claves duplicadas.
        using var documento = JsonDocument.Parse(
            contenido,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
        if (documento.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("La configuracion del servidor debe ser un objeto JSON.");
        }

        migrada = false;
        var propiedades = new HashSet<string>(StringComparer.Ordinal);
        using var flujo = new MemoryStream(contenido.Length);
        using (var escritor = new Utf8JsonWriter(flujo))
        {
            escritor.WriteStartObject();
            foreach (var propiedad in documento.RootElement.EnumerateObject())
            {
                if (!propiedades.Add(propiedad.Name))
                {
                    throw new JsonException($"La propiedad '{propiedad.Name}' esta duplicada.");
                }

                if (propiedad.NameEquals(PropiedadAdministradoresLegada))
                {
                    ValidarAdministradoresLegados(propiedad.Value);
                    migrada = true;
                    continue;
                }

                propiedad.WriteTo(escritor);
            }

            escritor.WriteEndObject();
        }

        return flujo.ToArray();
    }

    private static void ValidarAdministradoresLegados(JsonElement administradores)
    {
        // Valida el formato retirado antes de ignorarlo durante la migracion.
        if (administradores.ValueKind != JsonValueKind.Array
            || administradores.GetArrayLength() is <= 0 or > 20)
        {
            throw new JsonException("La lista antigua de administradores no tiene un formato valido.");
        }

        foreach (var elemento in administradores.EnumerateArray())
        {
            if (elemento.ValueKind != JsonValueKind.String
                || ConfiguracionServidor.NormalizarCuenta(elemento.GetString()).Length == 0)
            {
                throw new JsonException("La lista antigua de administradores contiene una cuenta no valida.");
            }
        }
    }
}
