// (Autor: Alex Roman)
// Descripcion: Valida permisos para ejecutar scripts en subcarpetas.

using System.IO;
using System.Security.Principal;
using System.Text.Json;

namespace LanzadorScripts.Servicios;

public sealed class ServicioPermisos
{
    private readonly HashSet<string> _ejecutoresSubcarpetas = new(StringComparer.OrdinalIgnoreCase);

    public string UsuarioActual { get; } = WindowsIdentity.GetCurrent().Name;

    public bool EsAdministrador
    {
        get
        {
            using var identidad = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identidad);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public void Cargar(string rutaScripts)
    {
        _ejecutoresSubcarpetas.Clear();

        if (string.IsNullOrWhiteSpace(rutaScripts))
        {
            return;
        }

        var rutaPermisos = Path.Combine(rutaScripts, "PERMISOS", "permissions.json");
        if (!File.Exists(rutaPermisos))
        {
            return;
        }

        try
        {
            using var documento = JsonDocument.Parse(File.ReadAllText(rutaPermisos));
            var raiz = documento.RootElement;
            CargarLista(raiz, "EjecutoresSubcarpetas");
        }
        catch
        {
            _ejecutoresSubcarpetas.Clear();
        }
    }

    public bool RequierePermisoExtra(string rutaScripts, string rutaScript)
    {
        var raiz = Path.GetFullPath(rutaScripts).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var carpeta = Path.GetFullPath(Path.GetDirectoryName(rutaScript) ?? rutaScripts).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return !string.Equals(raiz, carpeta, StringComparison.OrdinalIgnoreCase);
    }

    public bool PuedeEjecutar(string rutaScripts, string rutaScript)
    {
        if (!RequierePermisoExtra(rutaScripts, rutaScript))
        {
            return true;
        }

        return _ejecutoresSubcarpetas.Contains(UsuarioActual);
    }

    private void CargarLista(JsonElement raiz, string propiedad)
    {
        if (!raiz.TryGetProperty(propiedad, out var lista) || lista.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var usuario in lista.EnumerateArray())
        {
            if (usuario.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(usuario.GetString()))
            {
                _ejecutoresSubcarpetas.Add(usuario.GetString()!.Trim());
            }
        }
    }
}
