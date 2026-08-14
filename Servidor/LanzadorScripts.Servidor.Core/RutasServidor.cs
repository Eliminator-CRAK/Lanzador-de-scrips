// (Autor: Alex Roman)
// Descripcion: Centraliza las rutas locales usadas por el servidor.

using System.Security.AccessControl;
using System.Security.Principal;

namespace LanzadorScripts.Servidor.Core;

public sealed class RutasServidor
{
    private readonly bool _usarAclAdministrativa;

    public RutasServidor(string? raiz = null)
    {
        _usarAclAdministrativa = string.IsNullOrWhiteSpace(raiz);
        Raiz = Path.GetFullPath(raiz ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LanzadorScriptsServidor"));
        RutaDatos = Path.Combine(Raiz, "Datos");
        RutaSeguridad = Path.Combine(Raiz, "Seguridad");
        RutaCopias = Path.Combine(Raiz, "CopiasSeguridad");
        RutaLogs = Path.Combine(Raiz, "Logs");
        RutaBaseDatos = Path.Combine(RutaDatos, "LanzadorScripts.db");
        RutaClaveProtegida = Path.Combine(RutaSeguridad, "base-datos.key.dpapi");
        RutaConfiguracion = Path.Combine(Raiz, "configuracion-servidor.json");
    }

    public string Raiz { get; }

    public string RutaDatos { get; }

    public string RutaSeguridad { get; }

    public string RutaCopias { get; }

    public string RutaLogs { get; }

    public string RutaBaseDatos { get; }

    public string RutaClaveProtegida { get; }

    public string RutaConfiguracion { get; }

    public void PrepararDirectorios()
    {
        Directory.CreateDirectory(Raiz);
        RechazarPuntoReanalisis(Raiz);

        if (_usarAclAdministrativa)
        {
            AplicarAclAdministrativa(Raiz);
        }

        foreach (var ruta in new[] { RutaDatos, RutaSeguridad, RutaCopias, RutaLogs })
        {
            Directory.CreateDirectory(ruta);
            RechazarPuntoReanalisis(ruta);
        }
    }

    public static void RechazarPuntoReanalisis(string ruta)
    {
        var completa = Path.GetFullPath(ruta);
        var actual = File.Exists(completa) || Directory.Exists(completa)
            ? completa
            : Path.GetDirectoryName(completa);
        while (!string.IsNullOrWhiteSpace(actual))
        {
            if (!File.Exists(actual) && !Directory.Exists(actual))
            {
                actual = Path.GetDirectoryName(actual);
                continue;
            }

            var atributos = File.GetAttributes(actual);
            if ((atributos & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"La ruta protegida no puede atravesar un punto de reanalisis: {actual}");
            }

            var padre = Path.GetDirectoryName(actual);
            if (string.IsNullOrWhiteSpace(padre)
                || string.Equals(padre, actual, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            actual = padre;
        }
    }

    private static void AplicarAclAdministrativa(string ruta)
    {
        // Limita los datos del servidor a SYSTEM y administradores locales.
        var administradores = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var sistema = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var herencia = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var seguridad = new DirectorySecurity();
        seguridad.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        seguridad.SetOwner(administradores);
        seguridad.AddAccessRule(new FileSystemAccessRule(
            administradores,
            FileSystemRights.FullControl,
            herencia,
            PropagationFlags.None,
            AccessControlType.Allow));
        seguridad.AddAccessRule(new FileSystemAccessRule(
            sistema,
            FileSystemRights.FullControl,
            herencia,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(ruta).SetAccessControl(seguridad);
    }
}
