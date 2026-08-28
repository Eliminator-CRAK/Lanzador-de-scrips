// (Autor: Alex Roman)
// Descripcion: Valida el contrato reproducible del instalador MSI 1.8.4.

using Xunit;

namespace LanzadorScripts.Pruebas;

public sealed class PruebasInstaladorMsi
{
    [Fact]
    public void VdprojUsaPublishItemsX64YActualizacionEstable()
    {
        var vdproj = File.ReadAllText(ObtenerRutaProyecto(
            "Instalador",
            "LanzadorScripts.Instalador.vdproj"));

        Assert.Contains("\"OutputGroupCanonicalName\" = \"8:PublishItems\"", vdproj, StringComparison.Ordinal);
        Assert.Contains("\"PublishProfilePath\" = \"8:Properties\\\\PublishProfiles\\\\Instalada.pubxml\"", vdproj, StringComparison.Ordinal);
        Assert.Contains("[ProgramFiles64Folder]LanzadorScripts", vdproj, StringComparison.Ordinal);
        Assert.Contains("\"InstallAllUsers\" = \"11:TRUE\"", vdproj, StringComparison.Ordinal);
        Assert.Contains("\"TargetPlatform\" = \"3:1\"", vdproj, StringComparison.Ordinal);
        Assert.Contains("\"ProductVersion\" = \"8:1.8.4\"", vdproj, StringComparison.Ordinal);
        Assert.Contains("{F895FB81-296D-4A0A-AC51-58E4DCF3296B}", vdproj, StringComparison.Ordinal);
        Assert.Contains("{69B1B1FD-FA3A-4955-BEA9-ABF1BE7F46AD}", vdproj, StringComparison.Ordinal);
        Assert.DoesNotContain("{185E5B1A-2386-4CD0-A7B8-8D9FB729AF35}", vdproj, StringComparison.Ordinal);
        Assert.DoesNotContain("{98A61296-1ABD-4C37-A79C-79735955A1E7}", vdproj, StringComparison.Ordinal);
        Assert.DoesNotContain("{96640479-F6DF-4AE5-BC5B-0799ECCC938E}", vdproj, StringComparison.Ordinal);
        Assert.DoesNotContain("{84E73469-1AAD-4C67-BE52-A88A2737CB15}", vdproj, StringComparison.Ordinal);
        Assert.Contains("{24169C78-5164-45C8-AB1A-AFC281D86DE9}", vdproj, StringComparison.Ordinal);
        Assert.Contains("\"RemovePreviousVersions\" = \"11:TRUE\"", vdproj, StringComparison.Ordinal);
        Assert.Contains("\"DetectNewerInstalledVersion\" = \"11:TRUE\"", vdproj, StringComparison.Ordinal);
    }

    [Fact]
    public void PerfilInstaladoEsAutocontenidoYNoPortable()
    {
        var perfil = File.ReadAllText(ObtenerRutaProyecto(
            "Properties",
            "PublishProfiles",
            "Instalada.pubxml"));

        Assert.Contains("<SelfContained>true</SelfContained>", perfil, StringComparison.Ordinal);
        Assert.Contains("<PublishSingleFile>false</PublishSingleFile>", perfil, StringComparison.Ordinal);
        Assert.Contains("<EmbedWebView2Runtime>false</EmbedWebView2Runtime>", perfil, StringComparison.Ordinal);
        Assert.Contains("<IncludeInstalledWebView2Runtime>true</IncludeInstalledWebView2Runtime>", perfil, StringComparison.Ordinal);
        Assert.Contains("<IncludeSourceRevisionInInformationalVersion>false", perfil, StringComparison.Ordinal);
        Assert.Contains("1.8.4+$(LANZADOR_GIT_REVISION).installed", perfil, StringComparison.Ordinal);

        var proyecto = File.ReadAllText(ObtenerRutaProyecto("LanzadorScripts.csproj"));
        Assert.Contains("runtimes\\win-x64\\native\\WebView2Loader.dll", proyecto, StringComparison.Ordinal);
        Assert.Contains("'$(IncludeInstalledWebView2Runtime)' == 'true'", proyecto, StringComparison.Ordinal);
        Assert.Contains("<ResolvedFileToPublish Remove=\"@(ResolvedFileToPublish)\"", proyecto, StringComparison.Ordinal);
        Assert.Contains("BeforeTargets=\"PublishItemsOutputGroup\"", proyecto, StringComparison.Ordinal);
        Assert.Contains("EjecutablePublishItems", proyecto, StringComparison.Ordinal);
    }

    [Fact]
    public void PostprocesadoConfiguraCierreMigracionLimpiezaYOpciones()
    {
        var configuracion = File.ReadAllText(ObtenerRutaProyecto(
            "Herramientas",
            "ConfigurarMsi.ps1"));

        Assert.Contains("LS_CheckClose", configuracion, StringComparison.Ordinal);
        Assert.Contains("--comprobar-cierre [UILevel]", configuracion, StringComparison.Ordinal);
        Assert.Contains("NOT PATCH AND ACTION <> \"ADMIN\"', 1450", configuracion, StringComparison.Ordinal);
        Assert.Contains("LS_Migrate16", configuracion, StringComparison.Ordinal);
        Assert.Contains("NOT Installed AND NOT REMOVE~=\"ALL\" AND NOT PATCH AND ACTION <> \"ADMIN\"", configuracion, StringComparison.Ordinal);
        Assert.Contains("LS_Cleanup", configuracion, StringComparison.Ordinal);
        Assert.Contains("REMOVE~=\"ALL\" AND NOT UPGRADINGPRODUCTCODE AND ACTION <> \"ADMIN\"", configuracion, StringComparison.Ordinal);
        Assert.Contains("CREATE_DESKTOP_SHORTCUT=1", configuracion, StringComparison.Ordinal);
        Assert.Contains("LAUNCH_LANZADORSCRIPTS=1", configuracion, StringComparison.Ordinal);
        Assert.Contains("ControlEvent", configuracion, StringComparison.Ordinal);
        Assert.Contains(".lanzadorconfig", configuracion, StringComparison.Ordinal);
        Assert.Contains("LanzadorScriptsMenuFolder", configuracion, StringComparison.Ordinal);
        Assert.Contains("componentes asociados a directorios inexistentes", configuracion, StringComparison.Ordinal);
        Assert.Contains("una unica WebView2Loader.dll", configuracion, StringComparison.Ordinal);
    }

    [Fact]
    public void CompilacionUsaSoloVisualStudioProfessional2026()
    {
        var preparacion = File.ReadAllText(ObtenerRutaProyecto(
            "Herramientas",
            "PrepararVisualStudioInstalador.ps1"));
        var compilacion = File.ReadAllText(ObtenerRutaProyecto(
            "Herramientas",
            "CompilarMsi.ps1"));
        var publicacion = File.ReadAllText(ObtenerRutaProyecto(
            "Herramientas",
            "PublicarPortable.ps1"));

        Assert.Contains("Microsoft.VisualStudio.Product.Professional", preparacion, StringComparison.Ordinal);
        Assert.Contains("$versionExtension = '3.0.0'", preparacion, StringComparison.Ordinal);
        Assert.Contains("36D2D52176DD7B2FA8D03E80652ACB063498CA3990E101C5CE2350446826541F", preparacion, StringComparison.Ordinal);
        Assert.Contains("Microsoft.VisualStudio.Product.Professional", compilacion, StringComparison.Ordinal);
        Assert.Contains("Microsoft.VisualStudio.Product.Professional", publicacion, StringComparison.Ordinal);
        Assert.Contains("[18.0,19.0)", preparacion, StringComparison.Ordinal);
        Assert.Contains("'/a'", compilacion, StringComparison.Ordinal);
        Assert.Contains("MsiAdminImage", compilacion, StringComparison.Ordinal);
        Assert.Contains("$codigoMsiOtraInstalacionEnCurso = 1618", compilacion, StringComparison.Ordinal);
        Assert.Contains("$intentosExtraccionMsi = 12", compilacion, StringComparison.Ordinal);
        Assert.Contains("Start-Sleep -Seconds $esperaExtraccionMsiSegundos", compilacion, StringComparison.Ordinal);
        Assert.Contains("LanzadorScripts-Msi-WebView2-", compilacion, StringComparison.Ordinal);
        Assert.Contains("LanzadorScripts-Msi-Validacion-", compilacion, StringComparison.Ordinal);
        Assert.Contains("[System.IO.Directory]::Delete($validacionMsi, $true)", compilacion, StringComparison.Ordinal);
        Assert.Contains("[void]$vista.Execute()", compilacion, StringComparison.Ordinal);
        Assert.Contains("FinalReleaseComObject($fila)", compilacion, StringComparison.Ordinal);
        Assert.Contains("FinalReleaseComObject($vista)", compilacion, StringComparison.Ordinal);
        Assert.Contains("FileAttributes]::ReparsePoint", compilacion, StringComparison.Ordinal);
        Assert.Contains("$env:InstalledWebView2RuntimeSource = $runtimeMsi", compilacion, StringComparison.Ordinal);
        Assert.Contains("[System.IO.Directory]::Delete($runtimeMsi, $true)", compilacion, StringComparison.Ordinal);
        Assert.Contains("El ejecutable incluido en el MSI no conserva una firma Authenticode valida", compilacion, StringComparison.Ordinal);
        Assert.DoesNotContain("Product.Community", preparacion, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Product.Community", compilacion, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-products *", publicacion, StringComparison.Ordinal);

        var helper = File.ReadAllText(ObtenerRutaProyecto(
            "Instalador",
            "LanzadorScripts.Instalador.cpp"));
        Assert.Contains("RutaSinPuntosReanalisis", helper, StringComparison.Ordinal);
        Assert.Contains("EliminarArbolSeguro(perfiles, lanzador)", helper, StringComparison.Ordinal);
        Assert.Contains("NombrePipeInstalado", helper, StringComparison.Ordinal);
        Assert.Contains("NombreMensajeCierre", helper, StringComparison.Ordinal);
        Assert.Contains("EnumWindows(EnviarMensajeCierreVentana", helper, StringComparison.Ordinal);
        Assert.Contains("CerrarParaMantenimiento", helper, StringComparison.Ordinal);
        Assert.Contains("PrepararRutaWin32", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("lanzadorscripts_portable-", helper, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MigracionAceptaRutasHeredadasAusentes()
    {
        var helper = File.ReadAllText(ObtenerRutaProyecto(
            "Instalador",
            "LanzadorScripts.Instalador.cpp"));
        var compilacion = File.ReadAllText(ObtenerRutaProyecto(
            "Herramientas",
            "CompilarMsi.ps1"));

        Assert.Contains("atributosDestino == INVALID_FILE_ATTRIBUTES", helper, StringComparison.Ordinal);
        Assert.Contains("error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND", helper, StringComparison.Ordinal);
        Assert.Contains("--validar-ruta-ausente", helper, StringComparison.Ordinal);
        Assert.Contains("-ArgumentList '--validar-ruta-ausente'", compilacion, StringComparison.Ordinal);
        Assert.Contains("El helper del MSI no acepta como correcto", compilacion, StringComparison.Ordinal);
        Assert.Contains("--validar-limpieza-ruta-larga", helper, StringComparison.Ordinal);
        Assert.Contains("-ArgumentList '--validar-limpieza-ruta-larga'", compilacion, StringComparison.Ordinal);
        Assert.Contains("El helper del MSI no pudo eliminar una ruta superior a MAX_PATH", compilacion, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicacionFinalContieneSoloMsiYPortableVersionados()
    {
        var publicacion = File.ReadAllText(ObtenerRutaProyecto(
            "Herramientas",
            "PublicarPortable.ps1"));

        Assert.Contains("LanzadorScripts-1.8.4-x64.msi", publicacion, StringComparison.Ordinal);
        Assert.Contains("LanzadorScripts_Portable-1.8.4-x64.exe", publicacion, StringComparison.Ordinal);
        Assert.Contains("$rutasEsperadas = @($msiPublicado, $exePortable)", publicacion, StringComparison.Ordinal);
        Assert.Contains("$archivosPublicados.Count -ne 2", publicacion, StringComparison.Ordinal);
        Assert.DoesNotContain("$exeNormal", publicacion, StringComparison.Ordinal);
        Assert.DoesNotContain("-Variante normal", publicacion, StringComparison.Ordinal);
    }

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
