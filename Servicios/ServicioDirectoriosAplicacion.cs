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
    private static readonly SecurityIdentifier PaquetesAplicacion = new("S-1-15-2-1");
    private static readonly SecurityIdentifier PaquetesRestringidos = new("S-1-15-2-2");

    public static void PrepararDatosUsuario()
    {
        PrepararDirectorioPrivado(RutasAplicacion.RaizDatosUsuario);
    }

    public static void PrepararDatosWebView2()
    {
        // Permite que WebView2 configure la ACL de sus procesos aislados.
        PrepararDatosUsuario();
        PrepararDirectorioWebView2(RutasAplicacion.RutaRaizWebView2Usuario);
    }

    public static void PrepararEstructuraAplicacion()
    {
        // Protege la raiz comun antes de crear los datos del usuario.
        PrepararDirectorioBase(RutasAplicacion.RaizProgramData);
        PrepararDirectorioBase(RutasAplicacion.RutaUsuarios);
        PrepararDirectorioAdministrativo(RutasAplicacion.RutaSeguridad);
        PrepararDatosUsuario();
    }

    public static void PrepararRecuperacionWebView2Sistema()
    {
        var raizTemporal = Path.GetDirectoryName(RutasAplicacion.RutaBaseWebView2RecuperacionSistema)
            ?? throw new IOException("No se pudo resolver la carpeta temporal de WebView2.");
        PrepararDirectorioBase(raizTemporal);
        PrepararDirectorioBase(RutasAplicacion.RutaBaseWebView2RecuperacionSistema);
        PrepararDirectorioPrivado(RutasAplicacion.RutaRaizWebView2RecuperacionSistema);
        PrepararDirectorioWebView2(RutasAplicacion.RutaRaizWebView2RecuperacionSistema);
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

    internal static void PrepararDirectorioWebView2(string ruta)
    {
        // Sustituye ACL incompatibles y permite que WebView2 configure sus procesos aislados.
        var directorio = new DirectoryInfo(ruta);
        directorio.Create();
        RechazarPuntosReanalisis(directorio.FullName);

        using var identidad = WindowsIdentity.GetCurrent();
        var usuario = identidad.User
            ?? throw new InvalidOperationException("No se pudo identificar al usuario actual.");
        var herencia = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var seguridad = directorio.GetAccessControl(AccessControlSections.Access);
        seguridad.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var denegaciones = seguridad
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Where(regla => regla.AccessControlType == AccessControlType.Deny)
            .ToList();
        foreach (var denegacion in denegaciones)
        {
            seguridad.RemoveAccessRuleSpecific(denegacion);
        }

        seguridad.SetAccessRule(CrearRegla(usuario, FileSystemRights.FullControl, herencia));
        seguridad.SetAccessRule(CrearRegla(Administradores, FileSystemRights.FullControl, herencia));
        seguridad.SetAccessRule(CrearRegla(Sistema, FileSystemRights.FullControl, herencia));
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

    internal static void PrepararDirectorioAdministrativo(string ruta)
    {
        // Limita la carpeta a administradores y al sistema.
        var directorio = new DirectoryInfo(ruta);
        directorio.Create();
        RechazarPuntosReanalisis(directorio.FullName);

        var herencia = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var seguridad = new DirectorySecurity();
        seguridad.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        seguridad.AddAccessRule(CrearRegla(Administradores, FileSystemRights.FullControl, herencia));
        seguridad.AddAccessRule(CrearRegla(Sistema, FileSystemRights.FullControl, herencia));
        seguridad.SetOwner(Administradores);
        directorio.SetAccessControl(seguridad);
    }

    internal static void ProtegerArchivoClaveArtefactos()
    {
        // Limita el archivo a administradores y al sistema.
        var archivo = new FileInfo(RutasAplicacion.RutaClaveArtefactos);
        if (!archivo.Exists)
        {
            throw new FileNotFoundException(
                "No se encontro el archivo que se debe proteger.",
                RutasAplicacion.RutaClaveArtefactos);
        }

        var seguridad = CrearSeguridadArchivoAdministrativo();
        archivo.SetAccessControl(seguridad);
    }

    internal static FileSecurity CrearSeguridadArchivoAdministrativo()
    {
        // Crea la ACL usada por las claves de maquina.
        var seguridad = new FileSecurity();
        seguridad.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        seguridad.AddAccessRule(CrearRegla(Administradores, FileSystemRights.FullControl, InheritanceFlags.None));
        seguridad.AddAccessRule(CrearRegla(Sistema, FileSystemRights.FullControl, InheritanceFlags.None));
        seguridad.SetOwner(Administradores);
        return seguridad;
    }

    internal static void PrepararDirectorioRuntime(string ruta)
    {
        // Conserva los permisos de Program Files y habilita AppContainer.
        var directorio = new DirectoryInfo(ruta);
        directorio.Create();
        RechazarPuntosReanalisis(directorio.FullName);

        var herencia = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var seguridad = directorio.GetAccessControl(AccessControlSections.Access);
        seguridad.SetAccessRule(CrearRegla(PaquetesAplicacion, FileSystemRights.ReadAndExecute, herencia));
        seguridad.SetAccessRule(CrearRegla(PaquetesRestringidos, FileSystemRights.ReadAndExecute, herencia));
        using var identidad = WindowsIdentity.GetCurrent();
        var usuario = identidad.User
            ?? throw new InvalidOperationException("No se pudo identificar al usuario actual.");
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
            if (Directory.Exists(actual)
                && File.GetAttributes(actual).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException($"La ruta local no puede contener puntos de reanalisis: {actual}");
            }
        }
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
