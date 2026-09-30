// (Autor: Alex Roman)
// Descripcion: Prepara directorios locales y aplica permisos seguros.

using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace LanzadorScripts.Servicios;

public static class ServicioDirectoriosAplicacion
{
    private static readonly SecurityIdentifier Administradores = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Sistema = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Usuarios = new(WellKnownSidType.BuiltinUsersSid, null);

    public static void PrepararDatosUsuario()
    {
        PrepararDirectorioPrivado(RutasAplicacion.RaizDatosUsuario);
    }

    public static void PrepararEstructuraAplicacion()
    {
        // Los datos temporales de sesion quedan limitados al usuario.
        RechazarPuntosReanalisis(ServicioSesionCliente.RutaActual);
        Directory.CreateDirectory(ServicioSesionCliente.RutaActual);
        PrepararDatosUsuario();
    }

    internal static void PrepararDirectorioPrivado(string ruta)
    {
        // Aisla los datos para el usuario que abre la aplicacion.
        var directorio = new DirectoryInfo(ruta);
        directorio.Create();
        RechazarPuntosReanalisis(directorio.FullName);

        using var identidad = WindowsIdentity.GetCurrent();
        var usuario = identidad.User
            ?? throw new InvalidOperationException("No se pudo identificar al usuario actual.");
        var herencia = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var seguridad = new DirectorySecurity();
        seguridad.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        seguridad.AddAccessRule(CrearRegla(usuario, FileSystemRights.Modify | FileSystemRights.ReadAndExecute, herencia));
        seguridad.AddAccessRule(CrearRegla(Administradores, FileSystemRights.FullControl, herencia));
        seguridad.AddAccessRule(CrearRegla(Sistema, FileSystemRights.FullControl, herencia));
        AsegurarPropietario(seguridad, directorio, usuario, forzarPropietarioAdministrativo: false);
        directorio.SetAccessControl(seguridad);
    }

    internal static void PrepararDirectorioBase(string ruta)
    {
        // Impide que otros usuarios creen carpetas privadas ajenas.
        var directorio = new DirectoryInfo(ruta);
        directorio.Create();
        RechazarPuntosReanalisis(directorio.FullName);

        using var identidad = WindowsIdentity.GetCurrent();
        var usuario = identidad.User
            ?? throw new InvalidOperationException("No se pudo identificar al usuario actual.");
        var herencia = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var seguridad = new DirectorySecurity();
        seguridad.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        seguridad.AddAccessRule(CrearRegla(Administradores, FileSystemRights.FullControl, herencia));
        seguridad.AddAccessRule(CrearRegla(Sistema, FileSystemRights.FullControl, herencia));
        seguridad.AddAccessRule(CrearRegla(Usuarios, FileSystemRights.ReadAndExecute, herencia));
        AsegurarPropietario(seguridad, directorio, usuario, forzarPropietarioAdministrativo: true);
        directorio.SetAccessControl(seguridad);
    }

    internal static void RechazarPuntosReanalisis(string ruta)
    {
        // Revisa cada carpeta existente desde la raiz del volumen.
        var rutaCompleta = Path.GetFullPath(ruta);
        var raiz = Path.GetPathRoot(rutaCompleta)
            ?? throw new IOException($"La ruta local no tiene una raiz valida: {ruta}");
        var actual = raiz;
        var segmentos = rutaCompleta[raiz.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        foreach (var segmento in segmentos)
        {
            actual = Path.Combine(actual, segmento);
            if ((Directory.Exists(actual) || File.Exists(actual))
                && File.GetAttributes(actual).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException($"La ruta local no puede contener puntos de reanalisis: {actual}");
            }
        }
    }

    internal static void EliminarArbolSinAtravesarReanalisis(
        string raizAutorizada,
        string rutaObjetivo)
    {
        // Limita el borrado a una subcarpeta y elimina los enlaces sin seguirlos.
        var raiz = ServicioRutasSeguras.ResolverCarpetaAbsoluta(
            raizAutorizada,
            "raiz autorizada de limpieza");
        var objetivo = ServicioRutasSeguras.ResolverCarpetaAbsoluta(
            rutaObjetivo,
            "carpeta de limpieza");
        if (string.Equals(raiz, objetivo, StringComparison.OrdinalIgnoreCase)
            || !ServicioRutasSeguras.EstaDentroDeCarpeta(raiz, objetivo))
        {
            throw new InvalidOperationException("La carpeta de limpieza queda fuera de la raiz autorizada.");
        }

        RechazarPuntosReanalisis(raiz);
        var carpetaPadre = Path.GetDirectoryName(objetivo)
            ?? throw new IOException("La carpeta de limpieza no tiene un directorio padre valido.");
        RechazarPuntosReanalisis(carpetaPadre);
        EliminarEntradaSinAtravesarReanalisis(objetivo);
    }

    private static void EliminarEntradaSinAtravesarReanalisis(string ruta)
    {
        FileAttributes atributos;
        try
        {
            atributos = File.GetAttributes(ruta);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }

        var esDirectorio = atributos.HasFlag(FileAttributes.Directory);
        if (atributos.HasFlag(FileAttributes.ReparsePoint))
        {
            // Retira el enlace encontrado sin acceder a su destino.
            if (esDirectorio)
            {
                Directory.Delete(ruta, recursive: false);
            }
            else
            {
                File.Delete(ruta);
            }

            return;
        }

        if (!esDirectorio)
        {
            if (atributos.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(ruta, atributos & ~FileAttributes.ReadOnly);
            }

            File.Delete(ruta);
            return;
        }

        foreach (var entrada in Directory.EnumerateFileSystemEntries(
                     ruta,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            EliminarEntradaSinAtravesarReanalisis(entrada);
        }

        if (atributos.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(ruta, atributos & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(ruta, recursive: false);
    }

    private static void AsegurarPropietario(
        DirectorySecurity seguridad,
        DirectoryInfo directorio,
        SecurityIdentifier usuario,
        bool forzarPropietarioAdministrativo)
    {
        // Recupera carpetas precreadas por otra cuenta.
        var propietario = directorio
            .GetAccessControl(AccessControlSections.Owner)
            .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var propietarioAdministrativo = propietario is not null
            && (propietario.Equals(Administradores) || propietario.Equals(Sistema));
        var propietarioUsuarioPermitido = !forzarPropietarioAdministrativo
            && propietario is not null
            && propietario.Equals(usuario);
        using var identidadActual = WindowsIdentity.GetCurrent();
        var procesoElevado = new WindowsPrincipal(identidadActual).IsInRole(WindowsBuiltInRole.Administrator);
        if (propietarioAdministrativo
            || propietarioUsuarioPermitido
            || (forzarPropietarioAdministrativo && !procesoElevado && propietario is not null && propietario.Equals(usuario)))
        {
            return;
        }

        seguridad.SetOwner(Administradores);
    }

    private static FileSystemAccessRule CrearRegla(
        SecurityIdentifier identidad,
        FileSystemRights permisos,
        InheritanceFlags herencia)
    {
        return new FileSystemAccessRule(
            identidad,
            permisos,
            herencia,
            PropagationFlags.None,
            AccessControlType.Allow);
    }


}
