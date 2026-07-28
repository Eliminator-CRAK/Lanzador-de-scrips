// (Autor: Alex Roman)
// Descripcion: Pruebas del runtime WebView2 y del ejecutable portable publicado.

using Xunit;

namespace LanzadorScripts.Pruebas;

public sealed class PruebasPublicacionWebView2
{
    // Comprueba la identidad exacta del runtime permitido.
    [Fact]
    public void PublicacionFijaWebView2Runtime150X64()
    {
        var publicacion = File.ReadAllText(ObtenerRutaProyecto("Herramientas", "PublicarPortable.ps1"));

        Assert.Contains("$versionWebView2Fijada = '150.0.4078.48'", publicacion, StringComparison.Ordinal);
        Assert.Contains("9E347BA96D031E381D1041D1C20FD434D457875C422EEAC3F40EEE4A5E0AB5C0", publicacion, StringComparison.Ordinal);
        Assert.Contains("80C46993E2D5922EFDF6463ACDA737BA0525993D4D7757D377C38F50D8BB417B", publicacion, StringComparison.Ordinal);
        Assert.Contains("30428A9075E5706B5E4A77E324B4331326566CDA027F49A8922089733C728859", publicacion, StringComparison.Ordinal);
        Assert.Contains("3345CEC7106D6A8EB3A5770DFF97DF36CB0750DF005331B54AB551CDF11E3DFB", publicacion, StringComparison.Ordinal);
        Assert.Contains("$arquitecturaPeX64 = 0x8664", publicacion, StringComparison.Ordinal);
        Assert.Contains("60926d99-f201-46bb-91a0-d868dc06b275", publicacion, StringComparison.Ordinal);
        Assert.Contains("VersionInfo.FileVersion", publicacion, StringComparison.Ordinal);
        Assert.Contains("Microsoft Corporation", publicacion, StringComparison.Ordinal);
        Assert.DoesNotContain("Sort-Object { [version]", publicacion, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-WebView2FixedRuntimeInfo", publicacion, StringComparison.Ordinal);
    }

    // Comprueba que el recurso se prepara antes de compilar.
    [Fact]
    public void PublicacionPreparaRuntimeAntesDeCompilar()
    {
        var publicacion = File.ReadAllText(ObtenerRutaProyecto("Herramientas", "PublicarPortable.ps1"));
        var preparacion = publicacion.LastIndexOf("Initialize-WebView2EmbeddedRuntime", StringComparison.Ordinal);
        var compilacion = publicacion.IndexOf("Write-Host 'Compilando aplicacion...'", StringComparison.Ordinal);

        Assert.True(preparacion >= 0);
        Assert.True(compilacion > preparacion);
        Assert.Contains("Assert-WebView2EmbeddedResource -RutaEnsamblado", publicacion, StringComparison.Ordinal);
        Assert.Contains("Get-RuntimeContentHash -Ruta $origen", publicacion, StringComparison.Ordinal);
        Assert.Contains("Recursos.WebView2Runtime.zip", publicacion, StringComparison.Ordinal);
        Assert.Contains("$PSVersionTable.PSEdition -ne 'Core'", publicacion, StringComparison.Ordinal);
        Assert.Contains("$PSVersionTable.PSVersion.Minor -ne 6", publicacion, StringComparison.Ordinal);
        Assert.Contains("$cabTemporal = \"$cab.$PID.tmp\"", publicacion, StringComparison.Ordinal);
        Assert.Contains("Move-Item -LiteralPath $cabTemporal -Destination $cab -Force", publicacion, StringComparison.Ordinal);
        Assert.Contains("status --porcelain --untracked-files=all", publicacion, StringComparison.Ordinal);
        Assert.Contains("Assert-PublishedExecutable -RutaExe $exe", publicacion, StringComparison.Ordinal);
        Assert.Contains("obj\\PublicacionStaging", publicacion, StringComparison.Ordinal);
        Assert.Contains("Sustituye la publicacion solo despues de validar todo el staging", publicacion, StringComparison.Ordinal);
        Assert.Contains("Move-Item -LiteralPath $stagingCompleta -Destination $salidaCompleta", publicacion, StringComparison.Ordinal);
        Assert.Contains("Move-Item -LiteralPath $salidaCompleta -Destination $stagingCompleta", publicacion, StringComparison.Ordinal);
        Assert.Contains("$publicacionNuevaInstalada = $true", publicacion, StringComparison.Ordinal);
        Assert.Contains("ProductVersion no identifica el commit publicado", publicacion, StringComparison.Ordinal);
        Assert.Contains("TimeStamperCertificate", publicacion, StringComparison.Ordinal);
        Assert.Contains("SHA-256 final", publicacion, StringComparison.Ordinal);
    }

    // Comprueba la version del producto y sus ensamblados.
    [Fact]
    public void ProyectoPublicaVersion146()
    {
        var proyecto = File.ReadAllText(ObtenerRutaProyecto("LanzadorScripts.csproj"));

        Assert.Contains("<Version>1.4.6</Version>", proyecto, StringComparison.Ordinal);
        Assert.Contains("<AssemblyVersion>1.4.6.0</AssemblyVersion>", proyecto, StringComparison.Ordinal);
        Assert.Contains("<FileVersion>1.4.6.0</FileVersion>", proyecto, StringComparison.Ordinal);
        Assert.Contains("<LogicalName>Recursos.WebView2Runtime.zip</LogicalName>", proyecto, StringComparison.Ordinal);
    }

    // Comprueba que el lanzador prepara .NET antes del codigo administrado.
    [Fact]
    public void PublicacionEnvuelveRuntimeDotNetEnLanzadorNativo()
    {
        var publicacion = File.ReadAllText(ObtenerRutaProyecto("Herramientas", "PublicarPortable.ps1"));
        var codigoNativo = File.ReadAllText(ObtenerRutaProyecto("LanzadorNativo", "LanzadorNativo.cpp"));
        var plantillaRecursos = File.ReadAllText(ObtenerRutaProyecto("LanzadorNativo", "LanzadorNativo.rc.in"));

        var firmaRuntime = publicacion.IndexOf("Write-Host 'Firmando runtime .NET interno...'", StringComparison.Ordinal);
        var creacionNativa = publicacion.IndexOf("Write-Host 'Creando lanzador nativo sin AppData...'", StringComparison.Ordinal);
        var firmaFinal = publicacion.IndexOf("Write-Host 'Firmando lanzador portable final...'", StringComparison.Ordinal);

        Assert.True(firmaRuntime >= 0);
        Assert.True(creacionNativa > firmaRuntime);
        Assert.True(firmaFinal > creacionNativa);
        Assert.Contains("Assert-NativeLauncherPayload", publicacion, StringComparison.Ordinal);
        Assert.Contains("$lineaCompilacion = @(", publicacion, StringComparison.Ordinal);
        Assert.Contains("$lineaCompilacion,", publicacion, StringComparison.Ordinal);
        Assert.DoesNotContain("\"cl.exe /nologo /std:c++20", publicacion, StringComparison.Ordinal);
        Assert.Contains("IDR_APLICACION_DOTNET RCDATA", plantillaRecursos, StringComparison.Ordinal);
        Assert.Contains("SetEnvironmentVariableW(L\"DOTNET_BUNDLE_EXTRACT_BASE_DIR\"", codigoNativo, StringComparison.Ordinal);
        Assert.Contains("SetEnvironmentVariableW(L\"TEMP\"", codigoNativo, StringComparison.Ordinal);
        Assert.Contains("SetEnvironmentVariableW(L\"TMP\"", codigoNativo, StringComparison.Ordinal);
        Assert.Contains("SetEnvironmentVariableW(L\"WEBVIEW2_USER_DATA_FOLDER\"", codigoNativo, StringComparison.Ordinal);
        Assert.Contains("FOLDERID_ProgramFiles", codigoNativo, StringComparison.Ordinal);
        Assert.Contains("FOLDERID_ProgramData", codigoNativo, StringComparison.Ordinal);
        Assert.Contains("LanzadorScripts.Runtime.exe", codigoNativo, StringComparison.Ordinal);
        Assert.Contains("LANZADOR_DISTRIBUTION_EXE", codigoNativo, StringComparison.Ordinal);
        Assert.DoesNotContain("FOLDERID_LocalAppData", codigoNativo, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalApplicationData", codigoNativo, StringComparison.Ordinal);
    }

    // Comprueba que la publicacion automatica usa PowerShell reproducible.
    [Fact]
    public void CiFijaPowerShell760()
    {
        var ci = File.ReadAllText(ObtenerRutaProyecto(".github", "workflows", "ci.yml"));

        Assert.Contains("$version = '7.6.0'", ci, StringComparison.Ordinal);
        Assert.Contains("9E725837AF682B87BB212CD1EFE3657C06C540404203810857EC2516AE2CA322", ci, StringComparison.Ordinal);
        Assert.Contains("PowerShell-$version-win-x64.zip", ci, StringComparison.Ordinal);
        Assert.Contains("$PSVersionTable.PSVersion.Minor -ne 6", ci, StringComparison.Ordinal);
        Assert.Contains("Microsoft.VisualStudio.Component.VC.Tools.x86.x64", ci, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $certPath", ci, StringComparison.Ordinal);
    }

    // Localiza archivos desde la raiz del proyecto.
    private static string ObtenerRutaProyecto(params string[] partes)
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null)
        {
            if (File.Exists(Path.Combine(directorio.FullName, "LanzadorScripts.csproj")))
            {
                return Path.Combine([directorio.FullName, .. partes]);
            }

            directorio = directorio.Parent;
        }

        throw new DirectoryNotFoundException("No se encontro la raiz del proyecto LanzadorScripts.");
    }
}
