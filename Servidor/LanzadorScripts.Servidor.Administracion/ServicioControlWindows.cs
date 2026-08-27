// (Autor: Alex Roman)
// Descripcion: Instala y controla el servicio Windows del servidor central.

using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using LanzadorScripts.Servidor.Core;

namespace LanzadorScripts.Servidor.Administracion;

public sealed class ServicioControlWindows
{
    public const string NombreServicio = "LanzadorScriptsServidor";
    private static readonly TimeSpan TiempoEspera = TimeSpan.FromSeconds(20);
    private static readonly string CarpetaInstalacion = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "LanzadorScriptsServidor");

    public EstadoServicioVista ObtenerEstado()
    {
        try
        {
            using var servicio = new ServiceController(NombreServicio);
            var estado = servicio.Status;
            return new EstadoServicioVista(
                true,
                estado == ServiceControllerStatus.Running,
                TraducirEstado(estado));
        }
        catch (InvalidOperationException)
        {
            return new EstadoServicioVista(false, false, "No instalado");
        }
    }

    public void Instalar(int puerto)
    {
        if (puerto is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(puerto));
        }

        var ejecutable = PrepararBinariosPermanentes();
        PrepararAdministradorInicial();
        EjecutarSc("create", NombreServicio, "binPath=", $"\"{ejecutable}\"", "start=", "delayed-auto", "obj=", "LocalSystem", "DisplayName=", "LanzadorScripts Servidor");
        EjecutarSc("description", NombreServicio, "Servicio central cifrado de permisos, catalogo y auditoria.");
        EjecutarSc("failure", NombreServicio, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000");
        EjecutarSc("failureflag", NombreServicio, "1");
        EjecutarSc("sidtype", NombreServicio, "unrestricted");
        ConfigurarFirewall(ejecutable, puerto);
        CrearAccesoMenuInicio();
    }

    public void Desinstalar()
    {
        var estado = ObtenerEstado();
        if (!estado.Instalado)
        {
            return;
        }

        if (estado.EnEjecucion)
        {
            Detener();
        }

        EjecutarSc("delete", NombreServicio);
        EjecutarProceso(
            Path.Combine(Environment.SystemDirectory, "netsh.exe"),
            ["advfirewall", "firewall", "delete", "rule", "name=LanzadorScripts Servidor"]);
        EliminarAccesoMenuInicio();
    }

    public void Iniciar()
    {
        PrepararAdministradorInicial();
        using var servicio = new ServiceController(NombreServicio);
        servicio.Start();
        servicio.WaitForStatus(ServiceControllerStatus.Running, TiempoEspera);
    }

    public void Detener()
    {
        using var servicio = new ServiceController(NombreServicio);
        if (!servicio.CanStop)
        {
            throw new InvalidOperationException("El servicio no acepta una orden de parada.");
        }

        servicio.Stop();
        servicio.WaitForStatus(ServiceControllerStatus.Stopped, TiempoEspera);
    }

    public void Reiniciar()
    {
        var estado = ObtenerEstado();
        if (estado.EnEjecucion)
        {
            Detener();
        }

        Iniciar();
    }

    private static string ResolverEjecutableServicio()
    {
        var candidatos = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Servicio", "LanzadorScripts.Servidor.Servicio.exe"),
            Path.Combine(AppContext.BaseDirectory, "LanzadorScripts.Servidor.Servicio.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Servicio", "LanzadorScripts.Servidor.Servicio.exe"))
        };
        var ruta = candidatos.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("No se encontro LanzadorScripts.Servidor.Servicio.exe junto a la consola.");
        return Path.GetFullPath(ruta);
    }

    private static void PrepararAdministradorInicial()
    {
        // Entrega al servicio la cuenta elevada que creara una base nueva.
        var ejecutable = Path.Combine(
            CarpetaInstalacion,
            "Servicio",
            "LanzadorScripts.Servidor.Servicio.exe");
        if (!File.Exists(ejecutable))
        {
            throw new FileNotFoundException("No se encontro el servicio instalado.", ejecutable);
        }

        using var identidad = WindowsIdentity.GetCurrent();
        EjecutarProceso(
            ejecutable,
            ["--preparar-administrador-inicial", identidad.Name]);
    }

    private static string PrepararBinariosPermanentes()
    {
        var servicioOrigen = ResolverEjecutableServicio();
        var administracionOrigen = Environment.ProcessPath
            ?? throw new InvalidOperationException("No se pudo localizar la consola administrativa.");
        Directory.CreateDirectory(CarpetaInstalacion);
        RechazarPuntoReanalisis(CarpetaInstalacion);

        var servicioDestino = Path.Combine(
            CarpetaInstalacion,
            "Servicio",
            "LanzadorScripts.Servidor.Servicio.exe");
        var administracionDestino = Path.Combine(
            CarpetaInstalacion,
            "LanzadorScripts.Servidor.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(servicioDestino)!);
        CopiarArchivoAtomico(servicioOrigen, servicioDestino);
        CopiarArchivoAtomico(administracionOrigen, administracionDestino);
        CopiarArchivoOpcional("Desinstalar-Servidor.ps1", CarpetaInstalacion);
        CopiarArchivoOpcional("Crear-ConfiguracionCliente.ps1", CarpetaInstalacion);
        CopiarArchivoOpcional("LEEME-Servidor.txt", CarpetaInstalacion);
        return servicioDestino;
    }

    private static void CopiarArchivoAtomico(string origen, string destino)
    {
        origen = Path.GetFullPath(origen);
        destino = Path.GetFullPath(destino);
        RechazarPuntoReanalisis(origen);
        if (string.Equals(origen, destino, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var temporal = destino + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(origen, temporal, overwrite: false);
            File.Move(temporal, destino, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporal))
            {
                File.Delete(temporal);
            }
        }
    }

    private static void CopiarArchivoOpcional(string nombre, string destino)
    {
        var origen = Path.Combine(AppContext.BaseDirectory, nombre);
        if (File.Exists(origen))
        {
            CopiarArchivoAtomico(origen, Path.Combine(destino, nombre));
        }
    }

    private static void CrearAccesoMenuInicio()
    {
        var carpeta = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            "LanzadorScripts");
        Directory.CreateDirectory(carpeta);
        var acceso = Path.Combine(carpeta, "LanzadorScripts Servidor.lnk");
        var tipoShell = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host no esta disponible.");
        dynamic? shell = null;
        dynamic? enlace = null;
        try
        {
            shell = Activator.CreateInstance(tipoShell);
            enlace = shell!.CreateShortcut(acceso);
            enlace.TargetPath = Path.Combine(CarpetaInstalacion, "LanzadorScripts.Servidor.exe");
            enlace.WorkingDirectory = CarpetaInstalacion;
            enlace.Description = "Administracion de LanzadorScripts Servidor";
            enlace.Save();
        }
        finally
        {
            if (enlace is not null)
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(enlace);
            }

            if (shell is not null)
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private static void EliminarAccesoMenuInicio()
    {
        var acceso = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            "LanzadorScripts",
            "LanzadorScripts Servidor.lnk");
        if (File.Exists(acceso))
        {
            File.Delete(acceso);
        }
    }

    private static void RechazarPuntoReanalisis(string ruta)
    {
        if ((File.GetAttributes(ruta) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("No se permiten enlaces ni puntos de reanalisis en la instalacion.");
        }
    }

    private static void ConfigurarFirewall(string ejecutable, int puerto)
    {
        EjecutarProceso(
            Path.Combine(Environment.SystemDirectory, "netsh.exe"),
            ["advfirewall", "firewall", "delete", "rule", "name=LanzadorScripts Servidor"],
            ignorarError: true);
        EjecutarProceso(
            Path.Combine(Environment.SystemDirectory, "netsh.exe"),
            [
                "advfirewall",
                "firewall",
                "add",
                "rule",
                "name=LanzadorScripts Servidor",
                "dir=in",
                "action=allow",
                "profile=domain",
                "protocol=TCP",
                $"localport={puerto}",
                $"program={ejecutable}",
                "enable=yes"
            ]);
    }

    private static void EjecutarSc(params string[] argumentos)
    {
        EjecutarProceso(Path.Combine(Environment.SystemDirectory, "sc.exe"), argumentos);
    }

    private static void EjecutarProceso(
        string ejecutable,
        IReadOnlyList<string> argumentos,
        bool ignorarError = false)
    {
        var inicio = new ProcessStartInfo
        {
            FileName = ejecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argumento in argumentos)
        {
            inicio.ArgumentList.Add(argumento);
        }

        using var proceso = Process.Start(inicio)
            ?? throw new InvalidOperationException("No se pudo iniciar la herramienta administrativa de Windows.");
        var salidaPendiente = proceso.StandardOutput.ReadToEndAsync();
        var errorPendiente = proceso.StandardError.ReadToEndAsync();
        if (!proceso.WaitForExit(30_000))
        {
            proceso.Kill(entireProcessTree: true);
            proceso.WaitForExit();
            throw new System.TimeoutException("La herramienta administrativa no termino dentro del tiempo permitido.");
        }

        var salida = salidaPendiente.GetAwaiter().GetResult();
        var error = errorPendiente.GetAwaiter().GetResult();

        if (!ignorarError && proceso.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Windows rechazo la operacion del servicio. Codigo {proceso.ExitCode}. {error} {salida}".Trim());
        }
    }

    private static string TraducirEstado(ServiceControllerStatus estado)
    {
        return estado switch
        {
            ServiceControllerStatus.Running => "En ejecución",
            ServiceControllerStatus.Stopped => "Detenido",
            ServiceControllerStatus.StartPending => "Iniciando",
            ServiceControllerStatus.StopPending => "Deteniendo",
            ServiceControllerStatus.Paused => "Pausado",
            _ => estado.ToString()
        };
    }
}
