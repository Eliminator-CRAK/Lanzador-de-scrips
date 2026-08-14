// (Autor: Alex Roman)
// Descripcion: Exporta e importa la conexion del cliente sin incluir permisos ni secretos.

using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LanzadorScripts.Modelos;
using LanzadorScripts.Protocolo;

namespace LanzadorScripts.Servicios;

public sealed class ServicioPaquetesConfiguracion
{
    public const string ExtensionPaquete = ".lanzadorconfig";
    public const int LongitudMaximaContenido = 256 * 1024;
    public const int LongitudMaximaBase64 = ((LongitudMaximaContenido + 2) / 3) * 4;
    private const int VersionActual = 2;
    private const string TipoActual = "configuracion-cliente";

    private static readonly HashSet<string> PropiedadesPermitidas = new(StringComparer.Ordinal)
    {
        "autor",
        "descripcion",
        "version",
        "tipo",
        "rutaScripts",
        "servidorCentral",
        "puertoServidorCentral",
        "creadoUtc"
    };

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public ServicioPaquetesConfiguracion()
    {
    }

    public ServicioPaquetesConfiguracion(ServicioCifradoAplicacion servicioCifrado)
    {
        ArgumentNullException.ThrowIfNull(servicioCifrado);
    }

    internal ServicioPaquetesConfiguracion(
        ServicioCifradoAplicacion servicioCifrado,
        ServicioArtefactosFirmados servicioArtefactos)
    {
        ArgumentNullException.ThrowIfNull(servicioCifrado);
        ArgumentNullException.ThrowIfNull(servicioArtefactos);
    }

    public PaqueteExportado Exportar(ConfiguracionLanzador configuracion)
    {
        ArgumentNullException.ThrowIfNull(configuracion);
        configuracion.Normalizar();
        var payload = new PayloadConfiguracionExportada(
            "Alex Roman",
            "Conexion de LanzadorScripts con el servidor central.",
            VersionActual,
            TipoActual,
            configuracion.RutaScripts,
            configuracion.ServidorCentral,
            configuracion.PuertoServidorCentral,
            DateTimeOffset.UtcNow);
        var contenido = JsonSerializer.Serialize(payload, OpcionesJson);
        var nombre = $"LanzadorScripts_{DateTime.Now:yyyyMMdd_HHmmss}{ExtensionPaquete}";
        return new PaqueteExportado(
            nombre,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(contenido)));
    }

    public PaqueteExportado Exportar(ConfiguracionLanzador configuracion, JsonObject permisos)
    {
        ArgumentNullException.ThrowIfNull(permisos);
        return Exportar(configuracion);
    }

    public ResultadoImportacionConfiguracion ImportarContenido(
        string contenido,
        ConfiguracionLanzador configuracionActual)
    {
        ArgumentNullException.ThrowIfNull(configuracionActual);
        ValidarContenido(contenido);
        var payload = JsonSerializer.Deserialize<PayloadConfiguracionExportada>(
            contenido,
            OpcionesJson)
            ?? throw new InvalidOperationException("El paquete de configuracion esta vacio.");
        if (payload.Version != VersionActual
            || !string.Equals(payload.Tipo, TipoActual, StringComparison.Ordinal)
            || !string.Equals(payload.Autor, "Alex Roman", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "El paquete usa un formato anterior o no compatible con la base central.");
        }

        var rutaScripts = ServicioRutasSeguras.ResolverCarpetaAbsoluta(
            payload.RutaScripts,
            "scripts del paquete");
        _ = new ClienteServidorCentral(
            payload.ServidorCentral,
            payload.PuertoServidorCentral,
            TimeSpan.FromSeconds(3));
        configuracionActual.RutaScripts = rutaScripts;
        configuracionActual.ServidorCentral = payload.ServidorCentral;
        configuracionActual.PuertoServidorCentral = payload.PuertoServidorCentral;
        configuracionActual.Normalizar();
        return new ResultadoImportacionConfiguracion(configuracionActual, null);
    }

    public static string ResolverRutaImportacion(string rutaArchivo)
    {
        return ServicioRutasSeguras.ResolverArchivoAbsoluto(
            rutaArchivo,
            "paquete de configuracion",
            ExtensionPaquete);
    }

    public static bool EsRutaImportacionValida(string rutaArchivo)
    {
        try
        {
            return File.Exists(ResolverRutaImportacion(rutaArchivo));
        }
        catch
        {
            return false;
        }
    }

    private static void ValidarContenido(string contenido)
    {
        if (string.IsNullOrWhiteSpace(contenido))
        {
            throw new InvalidOperationException("El paquete de configuracion esta vacio.");
        }

        if (Encoding.UTF8.GetByteCount(contenido) > LongitudMaximaContenido)
        {
            throw new InvalidOperationException("El paquete de configuracion supera el limite permitido.");
        }

        try
        {
            using var documento = JsonDocument.Parse(contenido, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            if (documento.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("El paquete no contiene un objeto JSON.");
            }

            var encontradas = new HashSet<string>(StringComparer.Ordinal);
            foreach (var propiedad in documento.RootElement.EnumerateObject())
            {
                if (!PropiedadesPermitidas.Contains(propiedad.Name)
                    || !encontradas.Add(propiedad.Name))
                {
                    throw new InvalidOperationException(
                        "El paquete contiene propiedades desconocidas o duplicadas.");
                }
            }

            if (!encontradas.SetEquals(PropiedadesPermitidas))
            {
                throw new InvalidOperationException("El paquete no contiene todos los campos requeridos.");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("El paquete no contiene JSON valido.", ex);
        }
    }

    private sealed record PayloadConfiguracionExportada(
        string Autor,
        string Descripcion,
        int Version,
        string Tipo,
        string RutaScripts,
        string ServidorCentral,
        int PuertoServidorCentral,
        DateTimeOffset CreadoUtc);
}

public sealed record PaqueteExportado(string NombreArchivo, string ContenidoBase64);

public sealed record ResultadoImportacionConfiguracion(
    ConfiguracionLanzador Configuracion,
    JsonObject? Permisos);
