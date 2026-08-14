// (Autor: Alex Roman)
// Descripcion: Gestiona tokens administrativos efimeros durante la sesion local.

using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace LanzadorScripts.Servicios;

public sealed class ServicioTokensAdmin
{
    private readonly ConcurrentDictionary<string, TokenAdmin> _tokens =
        new(StringComparer.OrdinalIgnoreCase);

    public TokenAdmin ObtenerOCrear(string usuarioWindows)
    {
        var cuenta = NormalizarCuenta(usuarioWindows);
        return _tokens.GetOrAdd(cuenta, static valor => new TokenAdmin(
            valor,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            DateTimeOffset.UtcNow));
    }

    public bool Validar(string usuarioWindows, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)
            || !_tokens.TryGetValue(NormalizarCuenta(usuarioWindows), out var guardado))
        {
            return false;
        }

        Span<byte> esperado = stackalloc byte[32];
        Span<byte> recibido = stackalloc byte[32];
        if (!Convert.TryFromBase64String(guardado.Valor, esperado, out var longitudEsperada)
            || !Convert.TryFromBase64String(token, recibido, out var longitudRecibida)
            || longitudEsperada != longitudRecibida)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            esperado[..longitudEsperada],
            recibido[..longitudRecibida]);
    }

    private static string NormalizarCuenta(string usuarioWindows)
    {
        var cuenta = usuarioWindows?.Trim() ?? string.Empty;
        if (cuenta.Length is <= 0 or > 256
            || cuenta.Contains('/', StringComparison.Ordinal)
            || cuenta.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("La cuenta de Windows no es valida.", nameof(usuarioWindows));
        }

        return cuenta;
    }
}

public sealed record TokenAdmin(string UsuarioWindows, string Valor, DateTimeOffset Creado);
