// (Autor: Alex Roman)
// Descripcion: Gestiona el inicio automatico elevado de la aplicacion en Windows.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace LanzadorScripts.Servicios;

public static class ServicioInicioAutomatico
{
    private const string NombreTarea = "LanzadorScripts";
    private const string NombreValorRun = "LanzadorScripts";
    private const string RutaRun = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly IntPtr ServidorWtsActual = IntPtr.Zero;

    public static void Aplicar(bool habilitado)
    {
        if (habilitado)
        {
            CrearInicioSesionInteractiva();
            return;
        }

        EliminarInicioSesionInteractiva();
    }

    private static void CrearInicioSesionInteractiva()
    {
        var rutaEjecutable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(rutaEjecutable))
        {
            return;
        }

        EliminarTarea();
        if (CrearEntradaRunUsuarioInteractivo(rutaEjecutable))
        {
            return;
        }

        CrearEntradaRunUsuarioActual(rutaEjecutable);
    }

    private static void EliminarInicioSesionInteractiva()
    {
        EliminarEntradaRunUsuarioInteractivo();
        EliminarEntradaRunUsuarioActual();
        EliminarTarea();
    }

    private static bool CrearEntradaRunUsuarioInteractivo(string rutaEjecutable)
    {
        var sid = ObtenerSidUsuarioSesionInteractiva();
        if (string.IsNullOrWhiteSpace(sid))
        {
            return false;
        }

        try
        {
            using var clave = Registry.Users.CreateSubKey($@"{sid}\{RutaRun}", true);
            clave?.SetValue(NombreValorRun, $"\"{rutaEjecutable}\"", RegistryValueKind.String);
            return clave is not null;
        }
        catch
        {
            return false;
        }
    }

    private static void CrearEntradaRunUsuarioActual(string rutaEjecutable)
    {
        try
        {
            using var clave = Registry.CurrentUser.CreateSubKey(RutaRun, true);
            clave?.SetValue(NombreValorRun, $"\"{rutaEjecutable}\"", RegistryValueKind.String);
        }
        catch
        {
        }
    }

    private static void EliminarEntradaRunUsuarioInteractivo()
    {
        var sid = ObtenerSidUsuarioSesionInteractiva();
        if (string.IsNullOrWhiteSpace(sid))
        {
            return;
        }

        try
        {
            using var clave = Registry.Users.OpenSubKey($@"{sid}\{RutaRun}", writable: true);
            clave?.DeleteValue(NombreValorRun, throwOnMissingValue: false);
        }
        catch
        {
        }
    }

    private static void EliminarEntradaRunUsuarioActual()
    {
        try
        {
            using var clave = Registry.CurrentUser.OpenSubKey(RutaRun, writable: true);
            clave?.DeleteValue(NombreValorRun, throwOnMissingValue: false);
        }
        catch
        {
        }
    }

    private static void EliminarTarea()
    {
        EjecutarSchtasks($"/Delete /F /TN \"{NombreTarea}\"");
    }

    private static void EjecutarSchtasks(string argumentos)
    {
        var inicio = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
            Arguments = argumentos,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proceso = Process.Start(inicio);
        proceso?.WaitForExit(5000);
    }

    private static string? ObtenerSidUsuarioSesionInteractiva()
    {
        var sesion = ObtenerSesionActual();
        var cuenta = ObtenerCuentaSesion(sesion);
        return string.IsNullOrWhiteSpace(cuenta) ? null : ResolverSid(cuenta);
    }

    private static int ObtenerSesionActual()
    {
        if (ProcessIdToSessionId(Process.GetCurrentProcess().Id, out var sesion))
        {
            return sesion;
        }

        var consola = WTSGetActiveConsoleSessionId();
        return consola == uint.MaxValue ? -1 : unchecked((int)consola);
    }

    private static string? ObtenerCuentaSesion(int sesion)
    {
        if (sesion < 0)
        {
            return null;
        }

        var usuario = LeerDatoSesion(sesion, WtsInfoClass.WTSUserName);
        if (string.IsNullOrWhiteSpace(usuario))
        {
            return null;
        }

        var dominio = LeerDatoSesion(sesion, WtsInfoClass.WTSDomainName);
        return string.IsNullOrWhiteSpace(dominio) ? usuario : $@"{dominio}\{usuario}";
    }

    private static string? LeerDatoSesion(int sesion, WtsInfoClass clase)
    {
        if (!WTSQuerySessionInformation(ServidorWtsActual, sesion, clase, out var buffer, out _))
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static string? ResolverSid(string cuenta)
    {
        var tamanoSid = 0;
        var tamanoDominio = 0;
        _ = LookupAccountName(null, cuenta, null, ref tamanoSid, null, ref tamanoDominio, out _);
        if (tamanoSid <= 0)
        {
            return null;
        }

        var sid = new byte[tamanoSid];
        var dominio = new StringBuilder(Math.Max(tamanoDominio, 1));
        return LookupAccountName(null, cuenta, sid, ref tamanoSid, dominio, ref tamanoDominio, out _)
            ? new SecurityIdentifier(sid, 0).Value
            : null;
    }

    private enum WtsInfoClass
    {
        WTSUserName = 5,
        WTSDomainName = 7
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(int processId, out int sessionId);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        int sessionId,
        WtsInfoClass wtsInfoClass,
        out IntPtr buffer,
        out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupAccountName(
        string? systemName,
        string accountName,
        byte[]? sid,
        ref int sidSize,
        StringBuilder? referencedDomainName,
        ref int referencedDomainNameSize,
        out int sidNameUse);
}
