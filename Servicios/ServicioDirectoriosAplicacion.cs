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
        // Prepara solo la raiz y conserva la ACL heredada del perfil de Windows.
        PrepararDirectorioWebView2(RutasAplicacion.RutaRaizWebView2Usuario);
    }

    public static void PrepararEstructuraAplicacion()
    {
        if (RutasAplicacion.Distribucion.EsPortable)
        {
            // Aisla todos los datos dentro de la sesion temporal validada.
            PrepararDirectorioPrivado(RutasAplicacion.Distribucion.RaizPortable!);
            PrepararDatosUsuario();
            return;
        }

        // Protege la raiz comun antes de crear los datos del usuario.
        PrepararDirectorioBase(RutasAplicacion.RaizProgramData);
        PrepararDirectorioBase(RutasAplicacion.RutaUsuarios);
        PrepararDatosUsuario();
    }

    public static void PrepararRecuperacionWebView2Local()
    {
        // Usa una segunda raiz del perfil local sin depender de ProgramData.
        PrepararDirectorioWebView2(RutasAplicacion.RutaRaizWebView2RecuperacionLocal);
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
        // Conserva la ACL de Windows y agrega acceso para las identidades del perfil.
        var directorio = new DirectoryInfo(ruta);
        directorio.Create();
        RechazarPuntosReanalisis(directorio.FullName);
        ConcederEscrituraPerfilWebView2(directorio);
    }

    internal static void PrepararPerfilWebView2(string ruta)
    {
        // Prepara el perfil final sin retirar las reglas requeridas por WebView2.
        var directorio = new DirectoryInfo(ruta);
        directorio.Create();
        RechazarPuntosReanalisis(directorio.FullName);
        ConcederEscrituraPerfilWebView2(directorio);
    }

    private static void ConcederEscrituraPerfilWebView2(DirectoryInfo directorio)
    {
        // Mantiene la herencia para los procesos LowIL y AppContainer del navegador.
        var identidadesEscritura = ObtenerIdentidadesEscrituraPerfil(directorio.FullName);
        var herencia = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var seguridad = directorio.GetAccessControl(AccessControlSections.Access);
        foreach (var identidad in identidadesEscritura)
        {
            seguridad.SetAccessRule(CrearRegla(
                identidad,
                FileSystemRights.Modify | FileSystemRights.ReadAndExecute,
                herencia));
        }

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

    private static IReadOnlyList<SecurityIdentifier> ObtenerIdentidadesEscrituraPerfil(string ruta)
    {
        // Incluye la cuenta elevada y el propietario del perfil que ejecutara WebView2.
        var identidades = new Dictionary<string, SecurityIdentifier>(StringComparer.Ordinal);
        using var identidadActual = WindowsIdentity.GetCurrent();
        if (identidadActual.User is not null)
        {
            identidades[identidadActual.User.Value] = identidadActual.User;
        }

        var actual = new DirectoryInfo(Path.GetFullPath(ruta));
        for (var nivel = 0; nivel < 12 && actual is not null; nivel++, actual = actual.Parent)
        {
            if (!actual.Exists)
            {
                continue;
            }

            SecurityIdentifier? propietario;
            try
            {
                propietario = actual
                    .GetAccessControl(AccessControlSections.Owner)
                    .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            if (propietario?.AccountDomainSid is not null
                && !propietario.Equals(Administradores)
                && !propietario.Equals(Sistema))
            {
                identidades[propietario.Value] = propietario;
            }
        }

        if (identidades.Count == 0)
        {
            throw new InvalidOperationException("No se pudo identificar una cuenta para el perfil de WebView2.");
        }

        return identidades.Values.ToArray();
    }
}
