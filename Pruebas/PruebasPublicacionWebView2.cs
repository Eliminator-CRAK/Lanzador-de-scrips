// (Autor: Alex Roman)
// Descripcion: Pruebas del runtime WebView2 y de las distribuciones publicadas.

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
        Assert.Contains("$informacionVersion.FileVersion", publicacion, StringComparison.Ordinal);
        Assert.Contains("$informacionVersion.ProductVersion", publicacion, StringComparison.Ordinal);
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
        Assert.Contains("[System.Reflection.Assembly]::Load($bytesEnsamblado)", publicacion, StringComparison.Ordinal);
        Assert.DoesNotContain("Assembly]::LoadFile", publicacion, StringComparison.Ordinal);
        Assert.Contains("Get-RuntimeContentHash -Ruta $origen", publicacion, StringComparison.Ordinal);
        Assert.Contains("return $ejecutableMsi.Directory.FullName", publicacion, StringComparison.Ordinal);
        Assert.Contains("Recursos.WebView2Runtime.zip", publicacion, StringComparison.Ordinal);
        Assert.Contains("$PSVersionTable.PSEdition -ne 'Core'", publicacion, StringComparison.Ordinal);
        Assert.Contains("$PSVersionTable.PSVersion.Minor -ne 6", publicacion, StringComparison.Ordinal);
        Assert.Contains("$cabTemporal = \"$cab.$PID.tmp\"", publicacion, StringComparison.Ordinal);
        Assert.Contains("Move-Item -LiteralPath $cabTemporal -Destination $cab -Force", publicacion, StringComparison.Ordinal);
        Assert.Contains("status --porcelain --untracked-files=all", publicacion, StringComparison.Ordinal);
        Assert.Contains("Assert-PublishedExecutable", publicacion, StringComparison.Ordinal);
        Assert.Contains("-SufijoProducto '.portable'", publicacion, StringComparison.Ordinal);
        Assert.Contains("LanzadorScripts-1.7.0-x64.msi", publicacion, StringComparison.Ordinal);
        Assert.Contains("LanzadorScripts_Portable-1.7.0-x64.exe", publicacion, StringComparison.Ordinal);
        Assert.Contains("CompilarMsi.ps1", publicacion, StringComparison.Ordinal);
        Assert.Contains("obj\\PublicacionStaging", publicacion, StringComparison.Ordinal);
        Assert.Contains("Sustituye la publicacion solo despues de validar todo el staging", publicacion, StringComparison.Ordinal);
        Assert.Contains("Move-Item -LiteralPath $stagingCompleta -Destination $salidaCompleta", publicacion, StringComparison.Ordinal);
        Assert.Contains("Move-Item -LiteralPath $salidaCompleta -Destination $stagingCompleta", publicacion, StringComparison.Ordinal);
        Assert.Contains("$publicacionNuevaInstalada = $true", publicacion, StringComparison.Ordinal);
        Assert.Contains("ProductVersion no identifica el commit publicado", publicacion, StringComparison.Ordinal);
        Assert.Contains("TimeStamperCertificate", publicacion, StringComparison.Ordinal);
        Assert.Contains("FinalReleaseComObject($fila)", publicacion, StringComparison.Ordinal);
        Assert.Contains("FinalReleaseComObject($vista)", publicacion, StringComparison.Ordinal);
        Assert.Contains("[void]$vista.Execute()", publicacion, StringComparison.Ordinal);
        Assert.Contains("[void]$vista.Close()", publicacion, StringComparison.Ordinal);
        Assert.Contains("function ConvertTo-WindowsExtendedPath", publicacion, StringComparison.Ordinal);
        Assert.Contains("GetVersionInfo($rutaVersion)", publicacion, StringComparison.Ordinal);
        Assert.Contains("SHA-256 final", publicacion, StringComparison.Ordinal);
    }

    // Comprueba la version del producto y sus ensamblados.
    [Fact]
    public void ProyectoPublicaVersion170()
    {
        var proyecto = File.ReadAllText(ObtenerRutaProyecto("LanzadorScripts.csproj"));
        var cliente = File.ReadAllText(ObtenerRutaProyecto(
            "ClienteWeb",
            "assets",
            "index-DgdNDMM1.js"));

        Assert.Contains("<Version>1.7.0</Version>", proyecto, StringComparison.Ordinal);
        Assert.Contains("<AssemblyVersion>1.7.0.0</AssemblyVersion>", proyecto, StringComparison.Ordinal);
        Assert.Contains("<FileVersion>1.7.0.0</FileVersion>", proyecto, StringComparison.Ordinal);
        Assert.Contains("<UseWindowsForms>true</UseWindowsForms>", proyecto, StringComparison.Ordinal);
        Assert.Contains("<ApplicationIcon>Recursos\\IconoLanzador.ico</ApplicationIcon>", proyecto, StringComparison.Ordinal);
        Assert.Contains("<LogicalName>Recursos.WebView2Runtime.zip</LogicalName>", proyecto, StringComparison.Ordinal);
        Assert.Contains("v1.7.0", cliente, StringComparison.Ordinal);
        Assert.DoesNotContain("v1.2.0", cliente, StringComparison.Ordinal);
    }

    // Comprueba que el icono conserva resoluciones adecuadas para Windows.
    [Fact]
    public void IconoAplicacionIncluyeResolucionesDeVentanaYBandeja()
    {
        var contenido = File.ReadAllBytes(ObtenerRutaProyecto("Recursos", "IconoLanzador.ico"));

        Assert.True(contenido.Length > 6);
        Assert.Equal((ushort)0, BitConverter.ToUInt16(contenido, 0));
        Assert.Equal((ushort)1, BitConverter.ToUInt16(contenido, 2));

        var cantidad = BitConverter.ToUInt16(contenido, 4);
        Assert.True(cantidad >= 9);

        var resoluciones = Enumerable.Range(0, cantidad)
            .Select(indice =>
            {
                var ancho = contenido[6 + (indice * 16)];
                return ancho == 0 ? 256 : ancho;
            })
            .ToHashSet();

        Assert.Contains(16, resoluciones);
        Assert.Contains(20, resoluciones);
        Assert.Contains(24, resoluciones);
        Assert.Contains(32, resoluciones);
        Assert.Contains(48, resoluciones);
        Assert.Contains(64, resoluciones);
        Assert.Contains(128, resoluciones);
        Assert.Contains(256, resoluciones);
    }

    // Comprueba que el lanzador prepara .NET antes del codigo administrado.
    [Fact]
    public void PublicacionEnvuelveRuntimeDotNetEnLanzadorNativo()
    {
        var publicacion = File.ReadAllText(ObtenerRutaProyecto("Herramientas", "PublicarPortable.ps1"));
        var codigoNativo = File.ReadAllText(ObtenerRutaProyecto("LanzadorNativo", "LanzadorNativo.cpp"));
        var plantillaRecursos = File.ReadAllText(ObtenerRutaProyecto("LanzadorNativo", "LanzadorNativo.rc.in"));

        var firmaRuntime = publicacion.IndexOf("Write-Host 'Firmando runtime .NET interno...'", StringComparison.Ordinal);
        var creacionNativa = publicacion.IndexOf("Write-Host 'Creando el lanzador nativo portable...'", StringComparison.Ordinal);
        var firmaFinal = publicacion.IndexOf("Write-Host 'Firmando el lanzador portable final...'", StringComparison.Ordinal);

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
        Assert.DoesNotContain("WEBVIEW2_USER_DATA_FOLDER", codigoNativo, StringComparison.Ordinal);
        Assert.DoesNotContain("L\"WebView2\\\\Perfil\"", codigoNativo, StringComparison.Ordinal);
        Assert.Contains("LanzadorScripts.Runtime.exe", codigoNativo, StringComparison.Ordinal);
        Assert.Contains("LANZADOR_DISTRIBUTION_EXE", codigoNativo, StringComparison.Ordinal);
        Assert.Contains("LanzadorScripts_Portable-1.7.0-x64.exe", publicacion, StringComparison.Ordinal);
        Assert.DoesNotContain("-Variante normal", publicacion, StringComparison.Ordinal);
        Assert.DoesNotContain("LANZADOR_LIMPIEZA_COMPLETA", publicacion, StringComparison.Ordinal);
        Assert.DoesNotContain("FOLDERID_ProgramFiles", codigoNativo, StringComparison.Ordinal);
        Assert.DoesNotContain("FOLDERID_ProgramData", codigoNativo, StringComparison.Ordinal);
        Assert.Contains("__RUTA_ICONO__", plantillaRecursos, StringComparison.Ordinal);
        Assert.DoesNotContain("FOLDERID_LocalAppData", codigoNativo, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalApplicationData", codigoNativo, StringComparison.Ordinal);
    }

    // Comprueba que la publicacion automatica usa PowerShell reproducible.
    [Fact]
    public void CiFijaPowerShell760()
    {
        var ci = File.ReadAllText(ObtenerRutaProyecto(".github", "workflows", "ci.yml"));
        var etapas = File.ReadAllText(ObtenerRutaProyecto("Herramientas", "EjecutarEtapaCi.ps1"));
        var preparacion = File.ReadAllText(ObtenerRutaProyecto("Herramientas", "PrepararVisualStudioInstalador.ps1"));

        Assert.Contains(
            "./Herramientas/EjecutarEtapaCi.ps1 -Etapa PrepararPowerShell",
            ci,
            StringComparison.Ordinal);
        Assert.Contains("$version = '7.6.0'", etapas, StringComparison.Ordinal);
        Assert.Contains("9E725837AF682B87BB212CD1EFE3657C06C540404203810857EC2516AE2CA322", etapas, StringComparison.Ordinal);
        Assert.Contains("PowerShell-$version-win-x64.zip", etapas, StringComparison.Ordinal);
        Assert.Contains("$PSVersionTable.PSVersion.Minor -ne 6", etapas, StringComparison.Ordinal);
        Assert.Contains("Microsoft.VisualStudio.Component.VC.Tools.x86.x64", preparacion, StringComparison.Ordinal);
        Assert.Contains("Microsoft.VisualStudio.Product.Professional", preparacion, StringComparison.Ordinal);
        Assert.Contains("vs-professional-2026", ci, StringComparison.Ordinal);
        Assert.Contains("X509Store]::new", etapas, StringComparison.Ordinal);
        Assert.Contains("StoreLocation]::LocalMachine", etapas, StringComparison.Ordinal);
        Assert.Contains("StoreLocation]::CurrentUser", etapas, StringComparison.Ordinal);
        Assert.Contains("Import-PfxCertificate", etapas, StringComparison.Ordinal);
        Assert.Contains("Remove-ConfianzaCertificadoFirmaCi", etapas, StringComparison.Ordinal);
        Assert.Contains("El PFX del runner no coincide", etapas, StringComparison.Ordinal);
        Assert.DoesNotContain("Import-Certificate", etapas, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $certPath", etapas, StringComparison.Ordinal);
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
