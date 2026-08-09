<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Presenta la arquitectura, compilacion y operacion de LanzadorScripts. -->

# LanzadorScripts

Aplicacion WPF para autorizar y ejecutar scripts PowerShell, BAT y CMD desde una interfaz WebView2 local. La version actual es `1.7.0`.

## Distribuciones

La publicacion genera exactamente dos opciones x64:

```text
LanzadorScripts-1.7.0-x64.msi
LanzadorScripts_Portable-1.7.0-x64.exe
```

- El MSI instala para todos los usuarios en `C:\Program Files\LanzadorScripts` y conserva configuracion y runtimes entre sesiones.
- La portable usa una sesion privada bajo `%TEMP%\LanzadorScripts\Portable\Sesion-<guid>` y elimina todos sus datos locales al cerrar.
- El siguiente arranque portable retira sesiones abandonadas por un cierre forzado sin seguir enlaces ni puntos de reanalisis.
- Ninguna variante configura inicio automatico con Windows.

El boton de cerrar oculta la ventana. El cierre definitivo se realiza desde `Cerrar LanzadorScripts` en el icono de la bandeja. La confirmacion solo aparece cuando hay scripts activos.

## Estado Del Codigo

- Aplicacion: .NET 10, WPF, `win-x64` y autocontenida.
- Instalador: proyecto `.vdproj` con `Publish Items` y Visual Studio Installer Projects.
- Portable: lanzador nativo C++ con payload .NET firmado.
- Interfaz: bundle compilado incluido en `ClienteWeb`.
- Pruebas: xUnit en `Pruebas`.
- Repositorio principal: GitLab.
- Replica exacta: GitHub.

El proyecto frontend original que genero `ClienteWeb` no esta disponible. El repositorio compila, prueba y publica la aplicacion, pero no reconstruye ese bundle desde fuentes frontend.

Los 37 scripts operativos permanecen fuera del repositorio.

## Artefactos Firmados V3

La carpeta central predeterminada es:

```text
\\MAD002MICROPRU.mad.ae.aena.es\R$\PERMISOS
```

Contiene los dos artefactos activos:

```text
permisos.json
catalogo-scripts.json
```

Son JSON legible firmado con RSA-PSS/SHA-256. No usan AES, DPAPI, SID ni `artefactos.key`. Ambos comparten un `ConjuntoId` firmado y la aplicacion falla de forma cerrada si se modifica el contenido, los metadatos, la firma o la pareja.

`ServicioCifradoAplicacion` solo protege `configuracion.dat` y paquetes `.lanzadorconfig`.

## Auditoria Remota

Cada ejecucion escribe eventos inmutables bajo:

```text
\\MAD002MICROPRU.mad.ae.aena.es\R$\PERMISOS\Auditoria\<usuario__sid-hash>
```

El evento `ejecucion.inicio` debe quedar confirmado antes de crear el proceso. Si el servidor no esta disponible, la API responde `503` y bloquea la ejecucion. Un resultado final no confirmado se reintenta solo en memoria y bloquea nuevos inicios. El cierre comparte un limite total de 30 segundos para procesos y auditoria.

Prepare una vez la carpeta y sus ACL desde una consola administrativa:

```powershell
pwsh -NoProfile -File .\Herramientas\PrepararAuditoriaServidor.ps1
```

La aplicacion, la portable y el desinstalador nunca eliminan auditorias remotas.

## Generar Los Dos JSON

Requisitos:

- Certificado privado de artefactos con huella `500266A64E574889370D92E5CE0D65D55CC963B7`.
- .NET SDK 10.x.
- Carpeta externa con los 37 scripts.

```powershell
pwsh -NoProfile -File .\PrepararArtefactosFirmados.ps1 `
  -RutaScripts "C:\Ruta\ACTUALES" `
  -TotalScriptsEsperado 37
```

La salida queda en `ArtefactosGenerados\conjunto-firmado-*` y contiene solo los dos JSON. Los administradores iniciales son `MAD00\aroperez_micro` y `PCERA\alero`.

## Compilar Y Probar

Requisitos para el MSI:

- Visual Studio Professional 2026.
- Carga de trabajo de escritorio administrado.
- Herramientas C++ x64.
- Microsoft Visual Studio Installer Projects 3.0.0 o posterior.

La preparacion se verifica sin instalar otra edicion:

```powershell
pwsh -NoProfile -File .\Herramientas\PrepararVisualStudioInstalador.ps1
```

Compilacion administrada:

```powershell
dotnet restore .\Pruebas\LanzadorScripts.Pruebas.csproj
dotnet build .\LanzadorScripts.csproj -c Release --no-restore
dotnet test .\Pruebas\LanzadorScripts.Pruebas.csproj -c Release --no-restore
dotnet list .\Pruebas\LanzadorScripts.Pruebas.csproj package --vulnerable --include-transitive --no-restore
```

MSI firmado:

```powershell
pwsh -NoProfile -File .\Herramientas\CompilarMsi.ps1 `
  -CertThumbprint "HUELLA_AUTHENTICODE"
```

El proyecto de instalacion sigue el flujo de [Microsoft Visual Studio Installer Projects](https://learn.microsoft.com/es-es/visualstudio/deployment/installer-projects-net-core?view=visualstudio).

## Publicar 1.7.0

La publicacion final requiere PowerShell 7.6, un arbol Git limpio y el certificado Authenticode con clave privada en el almacen de certificados:

```powershell
pwsh -NoProfile -File .\Herramientas\PublicarPortable.ps1 `
  -CertThumbprint "HUELLA_AUTHENTICODE"
```

La carpeta ignorada `publicacion` recibe solo el MSI y la portable versionados. `-InicializarArtefactos` puede regenerar los dos JSON firmados y nunca busca `artefactos.key`.

## Instalar Y Mantener

Instalacion interactiva:

```powershell
msiexec /i LanzadorScripts-1.7.0-x64.msi
```

Instalacion silenciosa sin acceso de escritorio:

```powershell
msiexec /i LanzadorScripts-1.7.0-x64.msi /qn /norestart
```

Instalacion silenciosa con acceso de escritorio:

```powershell
msiexec /i LanzadorScripts-1.7.0-x64.msi CREATE_DESKTOP_SHORTCUT=1 /qn /norestart
```

Reparacion y desinstalacion:

```powershell
msiexec /fa LanzadorScripts-1.7.0-x64.msi /qn /norestart
msiexec /x LanzadorScripts-1.7.0-x64.msi /qn /norestart
```

El MSI crea siempre el acceso del menu Inicio y la asociacion `.lanzadorconfig`. El acceso de escritorio y la apertura al finalizar estan desmarcados. La apertura final solo existe en instalacion interactiva.

Las actualizaciones y reparaciones conservan configuracion. Una desinstalacion completa elimina solo las rutas locales conocidas y la asociacion. La operacion se bloquea mientras LanzadorScripts o la portable estan activos.

## Datos Locales

Instalada:

- Binarios, .NET y WebView2 fijo: `%ProgramFiles%\LanzadorScripts`.
- Configuracion, tokens, logs y staging: `%ProgramData%\LanzadorScripts\Usuarios\<perfil>`.
- Perfil WebView2 por sesion: `%LocalAppData%\LanzadorScripts\WebView2-v5\Sesiones`.
- El perfil WebView2 se elimina al cerrar; la configuracion y runtimes se conservan.

Portable:

- Todos los datos locales, WebView2, .NET extraido, tokens y logs: `%TEMP%\LanzadorScripts\Portable\Sesion-<guid>`.
- Solo sobreviven los archivos exportados expresamente por el usuario y los recursos remotos.
- No crea asociaciones ni modifica el Registro.

## Seguridad Y Calidad

- La API escucha solo en `127.0.0.1` y exige sesion local.
- Los scripts se validan por ruta, extension, tamano y SHA-256.
- Las rutas con navegacion o enlaces se rechazan; la limpieza no atraviesa puntos de reanalisis.
- Las claves privadas, perfiles, runtimes descargados, `bin`, `obj`, MSI, EXE y artefactos operativos quedan fuera de Git.
- La validacion incluye xUnit, auditoria NuGet, Semgrep estricto, Gitleaks sobre todo el historial y CodeRabbit. Aikido no se utiliza.

GitHub requiere un runner Windows corporativo etiquetado `vs-professional-2026` para construir las dos distribuciones. La firma solo se activa en `main` o etiquetas.

## Flujo Git

El desarrollo se realiza en ramas y se fusiona mediante una unica merge request de GitLab. `main` exige pipeline correcto, discusiones resueltas y merge por Maintainers. Despues se publica el mismo SHA en GitHub sin `force-push`.

Consulte `CONTRIBUTING.md`, `Manual_Usuarios.md` y `Manual_Administradores_Desarrolladores.md`.
