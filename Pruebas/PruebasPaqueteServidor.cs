// (Autor: Alex Roman)
// Descripcion: Valida el contenido y la instalacion del paquete servidor.

using Xunit;

namespace LanzadorScripts.Pruebas;

public sealed class PruebasPaqueteServidor
{
    [Fact]
    public void SolucionIncluyeLosTresProyectosDelServidor()
    {
        var solucion = Leer("LanzadorScripts.slnx");

        Assert.Contains("LanzadorScripts.Protocolo.csproj", solucion, StringComparison.Ordinal);
        Assert.Contains("LanzadorScripts.Servidor.Core.csproj", solucion, StringComparison.Ordinal);
        Assert.Contains("LanzadorScripts.Servidor.Servicio.csproj", solucion, StringComparison.Ordinal);
        Assert.Contains("LanzadorScripts.Servidor.Administracion.csproj", solucion, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicacionServidorGeneraSoloElZipVersionado()
    {
        var publicacion = Leer("Herramientas", "PublicarServidor.ps1");

        Assert.Contains("LanzadorScripts_Servidor-$version-x64.zip", publicacion, StringComparison.Ordinal);
        Assert.Contains("$version = '1.8.1'", publicacion, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS.txt", publicacion, StringComparison.Ordinal);
        Assert.Contains("LanzadorScripts-CodeSigning-Public.cer", publicacion, StringComparison.Ordinal);
        Assert.Contains("Instalar-Servidor.ps1", publicacion, StringComparison.Ordinal);
        Assert.Contains("Desinstalar-Servidor.ps1", publicacion, StringComparison.Ordinal);
        Assert.Contains("Crear-ConfiguracionCliente.ps1", publicacion, StringComparison.Ordinal);
        Assert.DoesNotContain(".pfx", publicacion, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LanzadorScripts.db'", publicacion, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("base-datos.key.dpapi'", publicacion, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstaladorServidorConfiguraServicioSeguroYPersistente()
    {
        var instalador = Leer("Servidor", "Distribucion", "Instalar-Servidor.ps1");
        var control = Leer(
            "Servidor",
            "LanzadorScripts.Servidor.Administracion",
            "ServicioControlWindows.cs");

        Assert.Contains("LanzadorScriptsServidor", instalador, StringComparison.Ordinal);
        Assert.Contains("$env:ProgramFiles", instalador, StringComparison.Ordinal);
        Assert.Contains("$env:ProgramData", instalador, StringComparison.Ordinal);
        Assert.Contains("'start=', 'auto'", instalador, StringComparison.Ordinal);
        Assert.Contains("'obj=', 'LocalSystem'", instalador, StringComparison.Ordinal);
        Assert.Contains("'profile=domain'", instalador, StringComparison.Ordinal);
        Assert.Contains("'sidtype'", instalador, StringComparison.Ordinal);
        Assert.Contains("icacls.exe", instalador, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("*S-1-5-18:(OI)(CI)F", instalador, StringComparison.Ordinal);
        Assert.Contains("*S-1-5-32-544:(OI)(CI)F", instalador, StringComparison.Ordinal);
        Assert.Contains("Assert-IntegridadPaquete", instalador, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS.txt", instalador, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", instalador, StringComparison.Ordinal);
        Assert.Contains("$huellaFirmaEsperada", instalador, StringComparison.Ordinal);
        Assert.Contains("$puertoEfectivo", instalador, StringComparison.Ordinal);
        Assert.Contains("TryGetInt32", instalador, StringComparison.Ordinal);
        Assert.DoesNotContain("administradoresIniciales", instalador, StringComparison.Ordinal);
        Assert.Contains("--preparar-administrador-inicial", instalador, StringComparison.Ordinal);
        Assert.Contains("WindowsIdentity]::GetCurrent().Name", instalador, StringComparison.Ordinal);
        Assert.Contains("PrepararAdministradorInicial", control, StringComparison.Ordinal);
        Assert.Contains("failureflag", control, StringComparison.Ordinal);
        Assert.Contains("ignorarError: true", control, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsolaServidorUsaCanalLocalYTemaOscuroCompleto()
    {
        var codigo = Leer(
            "Servidor",
            "LanzadorScripts.Servidor.Administracion",
            "MainWindow.xaml.cs");
        var ventana = Leer(
            "Servidor",
            "LanzadorScripts.Servidor.Administracion",
            "MainWindow.xaml");
        var estilos = Leer(
            "Servidor",
            "LanzadorScripts.Servidor.Administracion",
            "App.xaml");
        var canalLocal = Leer(
            "Servidor",
            "LanzadorScripts.Servidor.Core",
            "ServidorAdministracionLocal.cs");

        Assert.Contains("new ClienteAdministracionLocal", codigo, StringComparison.Ordinal);
        Assert.Contains("NamedPipeServerStreamAcl.Create", canalLocal, StringComparison.Ordinal);
        Assert.Contains("WellKnownSidType.LocalSystemSid", canalLocal, StringComparison.Ordinal);
        Assert.Contains("WellKnownSidType.BuiltinAdministratorsSid", canalLocal, StringComparison.Ordinal);
        Assert.Contains("GetImpersonationUserName", canalLocal, StringComparison.Ordinal);
        Assert.Contains("Background=\"{StaticResource Fondo}\"", ventana, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"TextBlock\">", estilos, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"DataGridCell\">", estilos, StringComparison.Ordinal);
    }

    [Fact]
    public void DesinstaladorConservaDatosSalvoPeticionExplicita()
    {
        var desinstalador = Leer("Servidor", "Distribucion", "Desinstalar-Servidor.ps1");

        Assert.Contains("[switch]$EliminarDatos", desinstalador, StringComparison.Ordinal);
        Assert.Contains("if ($EliminarDatos)", desinstalador, StringComparison.Ordinal);
        Assert.Contains("La base y las copias se conservan", desinstalador, StringComparison.Ordinal);
        Assert.Contains("Assert-ArbolSinReparse", desinstalador, StringComparison.Ordinal);
    }

    [Fact]
    public void PaqueteClienteNoTransportaPermisosNiSecretos()
    {
        var generador = Leer("Servidor", "Distribucion", "Crear-ConfiguracionCliente.ps1");
        var importador = Leer("Servicios", "ServicioPaquetesConfiguracion.cs");

        Assert.Contains("version = 2", generador, StringComparison.Ordinal);
        Assert.Contains("tipo = 'configuracion-cliente'", generador, StringComparison.Ordinal);
        Assert.Contains("servidorCentral", generador, StringComparison.Ordinal);
        Assert.Contains("puertoServidorCentral", generador, StringComparison.Ordinal);
        Assert.DoesNotContain("rutaPermisos", generador, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CertificadoPublicoBase64", generador, StringComparison.Ordinal);
        Assert.DoesNotContain("Thumbprint", generador, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".pfx", generador, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clave =", generador, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("private const int VersionActual = 2", importador, StringComparison.Ordinal);
        Assert.Contains("private const string TipoActual = \"configuracion-cliente\"", importador, StringComparison.Ordinal);
    }

    [Fact]
    public void CiCompilaYVerificaClienteYServidor()
    {
        var github = Leer(".github", "workflows", "ci.yml");
        var gitlab = Leer(".gitlab-ci.yml");
        var etapas = Leer("Herramientas", "EjecutarEtapaCi.ps1");

        Assert.Contains("LanzadorScripts.Servidor.Servicio.csproj", github, StringComparison.Ordinal);
        Assert.Contains("LanzadorScripts.Servidor.Administracion.csproj", github, StringComparison.Ordinal);
        Assert.Contains("LanzadorScripts.Servidor.Servicio.csproj", gitlab, StringComparison.Ordinal);
        Assert.Contains("LanzadorScripts.Servidor.Administracion.csproj", gitlab, StringComparison.Ordinal);
        Assert.Contains("PublicarServidor.ps1", etapas, StringComparison.Ordinal);
        Assert.Contains("LanzadorScripts_Servidor-1.8.1-x64.zip", etapas, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS.txt no cubre", etapas, StringComparison.Ordinal);
    }

    [Fact]
    public void ProyectosServidorPublicanVersion181()
    {
        foreach (var ruta in new[]
                 {
                     new[] { "Servidor", "LanzadorScripts.Servidor.Core", "LanzadorScripts.Servidor.Core.csproj" },
                     new[] { "Servidor", "LanzadorScripts.Servidor.Servicio", "LanzadorScripts.Servidor.Servicio.csproj" },
                     new[] { "Servidor", "LanzadorScripts.Servidor.Administracion", "LanzadorScripts.Servidor.Administracion.csproj" }
                 })
        {
            Assert.Contains("<Version>1.8.1</Version>", Leer(ruta), StringComparison.Ordinal);
        }
    }

    private static string Leer(params string[] partes)
    {
        return File.ReadAllText(Path.Combine([ObtenerRaizProyecto(), .. partes]));
    }

    private static string ObtenerRaizProyecto()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null)
        {
            if (File.Exists(Path.Combine(directorio.FullName, "LanzadorScripts.csproj")))
            {
                return directorio.FullName;
            }

            directorio = directorio.Parent;
        }

        throw new DirectoryNotFoundException("No se encontro la raiz del proyecto LanzadorScripts.");
    }
}
