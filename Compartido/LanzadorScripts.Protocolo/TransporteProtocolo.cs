// (Autor: Alex Roman)
// Descripcion: Serializa mensajes limitados dentro del canal autenticado.

using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanzadorScripts.Protocolo;

public static class TransporteProtocolo
{
    public const int VersionActual = 1;
    public const int LongitudMaximaMensaje = 8 * 1024 * 1024;

    public static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64
    };

    public static async Task EscribirAsync<T>(
        Stream flujo,
        T mensaje,
        CancellationToken cancelacion)
    {
        ArgumentNullException.ThrowIfNull(flujo);
        var contenido = JsonSerializer.SerializeToUtf8Bytes(mensaje, OpcionesJson);
        if (contenido.Length is <= 0 or > LongitudMaximaMensaje)
        {
            throw new InvalidDataException("El mensaje del protocolo tiene un tamano no permitido.");
        }

        var cabecera = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(cabecera, contenido.Length);
        await flujo.WriteAsync(cabecera, cancelacion);
        await flujo.WriteAsync(contenido, cancelacion);
        await flujo.FlushAsync(cancelacion);
    }

    public static async Task<T> LeerAsync<T>(Stream flujo, CancellationToken cancelacion)
    {
        ArgumentNullException.ThrowIfNull(flujo);
        var cabecera = new byte[sizeof(int)];
        await LeerExactamenteAsync(flujo, cabecera, cancelacion);
        var longitud = BinaryPrimitives.ReadInt32BigEndian(cabecera);
        if (longitud is <= 0 or > LongitudMaximaMensaje)
        {
            throw new InvalidDataException("La longitud del mensaje del protocolo no es valida.");
        }

        var contenido = new byte[longitud];
        await LeerExactamenteAsync(flujo, contenido, cancelacion);
        try
        {
            using var documento = JsonDocument.Parse(contenido, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            ValidarPropiedadesUnicas(documento.RootElement);
            return documento.RootElement.Deserialize<T>(OpcionesJson)
                ?? throw new InvalidDataException("El mensaje del protocolo no contiene datos validos.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("El mensaje del protocolo no contiene JSON valido.", ex);
        }
    }

    public static JsonElement CrearDatos<T>(T datos)
    {
        return JsonSerializer.SerializeToElement(datos, OpcionesJson);
    }

    private static async Task LeerExactamenteAsync(
        Stream flujo,
        Memory<byte> destino,
        CancellationToken cancelacion)
    {
        var total = 0;
        while (total < destino.Length)
        {
            var leidos = await flujo.ReadAsync(destino[total..], cancelacion);
            if (leidos == 0)
            {
                throw new EndOfStreamException("La conexion termino antes de recibir el mensaje completo.");
            }

            total += leidos;
        }
    }

    private static void ValidarPropiedadesUnicas(JsonElement elemento)
    {
        if (elemento.ValueKind == JsonValueKind.Object)
        {
            var nombres = new HashSet<string>(StringComparer.Ordinal);
            foreach (var propiedad in elemento.EnumerateObject())
            {
                if (!nombres.Add(propiedad.Name))
                {
                    throw new InvalidDataException(
                        $"El mensaje contiene la propiedad duplicada '{propiedad.Name}'.");
                }

                ValidarPropiedadesUnicas(propiedad.Value);
            }

            return;
        }

        if (elemento.ValueKind == JsonValueKind.Array)
        {
            foreach (var valor in elemento.EnumerateArray())
            {
                ValidarPropiedadesUnicas(valor);
            }
        }
    }
}
