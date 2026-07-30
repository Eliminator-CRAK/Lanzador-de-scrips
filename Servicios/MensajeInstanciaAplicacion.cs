// (Autor: Alex Roman)
// Descripcion: Define los mensajes admitidos entre instancias de la aplicacion.

using System.Text.Json;

namespace LanzadorScripts.Servicios;

internal enum AccionInstanciaAplicacion
{
    Mostrar,
    ImportarPaquete
}

internal sealed record MensajeInstanciaAplicacion(
    AccionInstanciaAplicacion Accion,
    string? Ruta = null);

internal static class ProtocoloInstanciaAplicacion
{
    internal const int LongitudMaximaMensaje = 16384;

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static string Serializar(MensajeInstanciaAplicacion mensaje)
    {
        // Serializa un mensaje limitado para el pipe local.
        var json = JsonSerializer.Serialize(mensaje, OpcionesJson);
        if (json.Length > LongitudMaximaMensaje)
        {
            throw new InvalidOperationException("El mensaje entre instancias supera el limite permitido.");
        }

        return json;
    }

    internal static bool IntentarDeserializar(string json, out MensajeInstanciaAplicacion? mensaje)
    {
        // Rechaza mensajes vacios, sobredimensionados o desconocidos.
        mensaje = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > LongitudMaximaMensaje)
        {
            return false;
        }

        try
        {
            mensaje = JsonSerializer.Deserialize<MensajeInstanciaAplicacion>(json, OpcionesJson);
            return mensaje is not null && Enum.IsDefined(mensaje.Accion);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
