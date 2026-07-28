<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Manual tecnico para administradores y desarrolladores del lanzador. -->

# Manual De Administradores Y Desarrolladores

## Objetivo

Este manual describe la operacion, configuracion, seguridad, pruebas y publicacion de LanzadorScripts en entorno Windows corporativo.

## Arquitectura

- Un lanzador nativo x64 solicita elevacion UAC mediante `requireAdministrator` y prepara las rutas antes de iniciar .NET.
- WPF se ejecuta desde el componente .NET firmado que lleva embebido el EXE distribuido.
- WebView2 muestra el cliente embebido y se comunica con el backend integrado.
- El backend se aloja en el mismo proceso y no requiere instalar servicios.
- El servidor local exige cookie de sesion y token interno por arranque para `/api/*`.
- Los endpoints de administracion requieren ademas autorizacion de administrador local.
- Los scripts se ejecutan con la identidad que abre la aplicacion.

## Rutas Operativas

| Recurso | Ruta |
|---|---|
| Configuracion usuario | `%ProgramData%\LanzadorScripts\Usuarios\<id-SID>\configuracion.dat` |
| Tokens de administrador | `%ProgramData%\LanzadorScripts\Usuarios\<id-SID>\Tokens` |
| Logs de ejecucion | `%ProgramData%\LanzadorScripts\Usuarios\<id-SID>\Logs` |
| Auditoria | `%ProgramData%\LanzadorScripts\Usuarios\<id-SID>\Auditoria` |
| Clave de artefactos | `%ProgramData%\LanzadorScripts\Seguridad\artefactos.key` |
| Perfil WebView2 | `%ProgramData%\LanzadorScripts\Usuarios\<id-SID>\WebView2\Perfil` |
| Temporales de proceso | `%ProgramData%\LanzadorScripts\Usuarios\<id-SID>\Temporales` |
| Aplicacion .NET interna | `%ProgramFiles%\LanzadorScripts\Aplicacion\runtime-<hash>` |
| Extraccion nativa .NET | `%ProgramFiles%\LanzadorScripts\Runtimes\DotNet\runtime-<hash>` |
| Runtime WebView2 principal | `%ProgramFiles%\LanzadorScripts\Runtimes\WebView2\<hash-version>` |
| Staging TOCTOU | `%ProgramFiles%\LanzadorScripts\Staging` |

## Permisos

`permisos.json` es un contenedor v2 cifrado y firmado. El catalogo usa la misma proteccion en `catalogo-scripts.json`. Ambos emplean AES-256-GCM y RSA-PSS/SHA-256, con tipo autenticado para impedir el intercambio de archivos.

La clave AES no forma parte del EXE. Se protege con DPAPI `LocalMachine` en `%ProgramData%\LanzadorScripts\Seguridad\artefactos.key`, con acceso exclusivo para `SYSTEM` y `Administrators`. La firma usa el certificado privado con huella `500266A64E574889370D92E5CE0D65D55CC963B7`; los equipos que solo verifican no necesitan la clave privada.

La configuracion predeterminada apunta a:

- Scripts: `\\MAD002MICROPRU.mad.ae.aena.es\R$\SCRIPS`
- Permisos: `\\MAD002MICROPRU.mad.ae.aena.es\R$\PERMISOS`

La ruta de permisos siempre representa una carpeta. La aplicacion busca dentro `permisos.json` y `catalogo-scripts.json`; no usa copias junto al EXE. Las configuraciones antiguas que incluian el nombre del archivo se migran a su carpeta.

La politica operativa vive en `seguridadScripts`:

```json
{
  "scriptsElevadosPermitidos": ["admin/script.ps1"],
  "permitirExecutionPolicyBypass": false
}
```

Reglas:

- `.ps1`, `.bat` y `.cmd` deben figurar en el catalogo publicado.
- Ruta relativa, extension, longitud y SHA-256 deben coincidir.
- Un archivo nuevo, editado, movido o renombrado queda bloqueado hasta una nueva publicacion.
- Solo un administrador puede seleccionar scripts y publicar el catalogo.
- `scriptsElevadosPermitidos` se conserva por compatibilidad, pero con la app elevada todos los scripts permitidos salen del proceso principal.
- Los permisos por defecto solo sirven para formularios vacios y nunca autorizan ejecucion.

## Migracion A La Version 1.4.6

1. Conserve una copia administrativa de `permisos.json` y `catalogo-scripts.json`.
2. Si los archivos proceden del formato v1, exporte la configuracion con la version anterior antes de sustituirlos.
3. Genere y custodie una unica clave AES de 32 bytes fuera de Git, historiales de consola y archivos compartidos.
4. Ejecute `Herramientas\AprovisionarClaveArtefactos.ps1` como administrador en cada equipo que deba leer o publicar los contenedores.
5. Importe el paquete exportado o use `-InicializarArtefactos` solo para una instalacion nueva.
6. Publique de nuevo `catalogo-scripts.json` despues de cambiar, mover, renombrar o sustituir cualquier script.
7. Verifique un equipo cliente antes de retirar la copia de seguridad.

No edite directamente los dos JSON: en v2 son contenedores cifrados y firmados. Los equipos cliente necesitan la clave AES protegida por DPAPI, pero no el certificado privado. El certificado privado de artefactos se instala solo en los equipos autorizados para guardar permisos o publicar catalogos.

La clave debe crearse una sola vez y custodiarse en el gestor de secretos corporativo. El aviso de clave ausente no se resuelve generando una clave nueva en ese cliente: ejecute el aprovisionador con la clave compartida. Si se rota la clave, aprovisionela en todos los equipos y regenere ambos contenedores desde un equipo con el certificado privado de artefactos.

En un equipo cliente que solo dispone del EXE, pulse `Instalar clave` en la pantalla principal e introduzca la clave Base64 corporativa. La entrada queda enmascarada, no pasa por JavaScript y se protege con DPAPI `LocalMachine`; el resultado y el `KeyId` quedan auditados. Si ya existe una clave, la aplicación exige confirmar el reemplazo.

La version 1.4.6 serializa el acceso a `configuracion.dat`, reintenta bloqueos transitorios y usa reemplazo atomico con copia `.bak`. Una carga ordinaria ya no reescribe el archivo. Si el archivo existente no se puede descifrar o validar, la aplicacion falla sin sustituirlo por rutas predeterminadas.

## Uso Manual Y Servicio Local

La aplicacion no registra tareas programadas ni configura la apertura con Windows.

Al abrir la ventana se inicia el backend integrado en el mismo proceso elevado. La cuenta debe tener acceso a la carpeta de scripts y a los dos contenedores protegidos.

## Emergencia

El token maestro esta firmado por el certificado privado autorizado de Alex Roman y permite abrir una sesion de emergencia cuando los permisos no estan disponibles.

- TTL del token: sin caducidad operativa en la aplicacion.
- Uso: reutilizable mientras se conserve protegido y la firma sea valida.
- Alcance: sesion de emergencia con rol administrador para poder abrir Ajustes.
- Proteccion: si la sesion se abrio porque la carpeta remota era inaccesible, Ajustes permite diagnosticar pero bloquea guardar permisos y cambiar rutas hasta reiniciar con la carpeta disponible.
- Auditoria: intento, resultado, emisor, usuario y equipo.

## Broker Elevado

La aplicacion se abre elevada y usa su backend integrado. El broker elevado se mantiene como compatibilidad interna para una ejecucion que necesite separarse del proceso principal.

Controles:

- Named pipe con nombre aleatorio por ejecucion.
- Token efimero de 256 bits para autenticar el canal.
- Restriccion `CurrentUserOnly` en el pipe.
- Staging local validado antes de ejecutar.
- Cancelacion controlada desde la app principal.

Limitacion operativa: la entrada interactiva no esta disponible para ejecuciones elevadas por broker. Los scripts elevados deben ser no interactivos.

## Mitigacion TOCTOU

Antes de ejecutar:

1. Se valida el catalogo cifrado y firmado.
2. Se valida ruta, extension, longitud y SHA-256 del script original.
3. Se copia a staging local.
4. Se aplica ACL restrictiva y atributo de solo lectura.
5. Se revalida la copia contra el mismo catalogo.
6. Se ejecuta la copia y se registra el hash final.

## Salud Y Diagnostico

`/api/salud` devuelve un estado resumido sin sesion y diagnostico completo solo con sesion interna valida.

El diagnostico completo incluye:

- Version.
- Rutas operativas.
- Estado de permisos.
- Estado de auditoria.
- WebView2, runtime extraido y perfil activo.
- Ejecuciones activas.
- Broker.
- Emergencia activa.
- Ultimo error critico.

## Pruebas

Ejecutar:

```powershell
dotnet test .\Pruebas\LanzadorScripts.Pruebas.csproj
```

Cobertura actual:

- Permisos ausentes y corruptos.
- Autorizacion admin.
- Firma de contenedores y manipulacion.
- Validacion de rutas y admin shares operativos.
- Cifrado, firma y separacion de tipos de los contenedores.
- Catalogo valido, script modificado y script no incluido.
- Ejecucion real con eventos finales.

## Publicacion

La publicacion final se hace con:

```powershell
pwsh -NoProfile -File .\Herramientas\PublicarPortable.ps1 -CertThumbprint "<THUMBPRINT>"
```

Para pruebas locales sin firma:

```powershell
pwsh -NoProfile -File .\Herramientas\PublicarPortable.ps1 -AllowUnsignedForDev
```

La carpeta `publicacion` debe contener unicamente `LanzadorScripts.exe`. Los dos contenedores protegidos permanecen en la carpeta operativa de permisos.

Durante la publicacion se descarga o reutiliza WebView2 Fixed Version Runtime x64 `150.0.4078.48`. Se validan los hashes del CAB, ZIP, ejecutable y contenido completo, la arquitectura x64 y la firma de Microsoft antes de embeber el recurso. Al arrancar se vuelve a comprobar la huella completa de la copia extraida, se reemplaza si fue alterada y se conceden los permisos de lectura y ejecucion requeridos por AppContainer. El runtime se ejecuta solo desde `Program Files`; un bloqueo explicito de WDAC o AppLocker requiere una regla corporativa.

El publicador firma primero el runtime .NET interno y despues lo incluye como recurso de un lanzador nativo x64. El EXE exterior valida la huella SHA-256 del recurso y establece `DOTNET_BUNDLE_EXTRACT_BASE_DIR`, `TEMP` y `TMP` antes de que .NET pueda extraer archivos. La aplicacion interna y la extraccion nativa quedan en `Program Files`; los temporales privados quedan en `ProgramData`. No se utiliza AppData.

La publicacion exige `pwsh 7.6.x` y las herramientas C++ x64 de Visual Studio; la cache de WebView2 queda en `Recursos\WebView2` y no se versiona.

Para inicializarlos expresamente:

```powershell
pwsh -NoProfile -File .\Herramientas\PublicarPortable.ps1 -CertThumbprint "<THUMBPRINT>" -InicializarArtefactos
```

Antes de ejecutar `-InicializarArtefactos`, aprovisione la misma clave de 32 bytes en cada equipo autorizado:

```powershell
powershell.exe -NoProfile -File .\Herramientas\AprovisionarClaveArtefactos.ps1
```

La entrada es interactiva y segura. No introduzca la clave en argumentos, archivos de texto, Git ni historiales de consola. Los contenedores v1 no son compatibles con la version 1.4.4 o posteriores: exporte primero la configuracion con la version anterior, haga copia de seguridad de los dos JSON, aprovisione la clave v2, importe el paquete y vuelva a publicar el catalogo.

No se instala ningun servicio, cuenta, tarea ni puerto. El certificado privado de artefactos solo debe instalarse en los equipos autorizados para publicar cambios.

## CI y analisis continuo

Los repositorios `micro2822131/Lanzador-de-scrips` en GitLab y `Eliminator-CRAK/Lanzador-de-scrips` en GitHub mantienen el mismo historial de `main`. Todo cambio debe publicarse en ambos remotos y comprobarse con:

```powershell
git push origin main
git fetch --all --prune
git rev-list --left-right --count origin/main...github/main
```

El ultimo comando debe devolver `0 0`. El remoto local `origin` tiene dos destinos de escritura para que un unico `git push origin main` actualice GitLab y GitHub.

Semgrep esta conectado al grupo `micro2822131`:

- Managed Scans ejecuta Code y Supply Chain sobre `main`.
- El webhook del grupo activa el analisis de nuevas merge requests.
- El proyecto antiguo pendiente de eliminacion no tiene escaneos habilitados.
- El token dedicado `semgrep-managed-scanning-micro` usa el alcance `api` y caduca el `2027-07-28`.

Antes de la caducidad, rote el token en GitLab y actualicelo en `Semgrep > Settings > Source code managers > micro2822131 > Update access token`. Pruebe la conexion y confirme que el webhook `https://semgrep.dev/api/webhook/v2/gitlab` sigue activo. No guarde el valor del token en archivos, variables del repositorio ni historiales.

Semgrep tambien esta conectado a la cuenta personal `Eliminator-CRAK` mediante la aplicacion privada `semgrep-code-eliminator-crak`. Managed Scans y los analisis de PR estan activos para `Eliminator-CRAK/Lanzador-de-scrips`; los demas repositorios de la cuenta no se escanean automaticamente.

La politica de maxima cobertura usa:

- Las 2944 reglas de Code habilitadas y ninguna regla deshabilitada.
- Analisis entre archivos.
- Code, Supply Chain, busqueda de dependencias y avisos de dependencias maliciosas.
- Deteccion con IA para fallos de logica de negocio.
- Notificaciones de hallazgos aunque el filtro de ruido los considere posibles falsos positivos.
- Sugerencias con umbral de confianza bajo.
- Ninguna ruta ignorada globalmente.
- Modo fail-closed para los Managed Scans.
- Autofix PR desactivado para impedir cambios automaticos de codigo.

Semgrep Multimodal envia fragmentos necesarios a OpenAI o AWS Bedrock bajo Zero Data Retention del proveedor. Semgrep puede conservar esos fragmentos durante seis meses para prestar sus funciones de analisis y remediacion.

`.gitlab-ci.yml` y `.github/workflows/semgrep.yml` ejecutan `auto`, `p/security-audit` y `p/secrets`. Semgrep Secrets no esta incluido en el plan actual, por lo que Gitleaks 8.30.1 complementa el analisis revisando todo el historial Git.

- En cada `push`.
- En cada PR o MR.
- Bajo demanda.
- Una vez al dia.
- Incluyendo archivos comprimidos y valores codificados al buscar secretos.
- Sin aplicar exclusiones de `.gitignore`.
- Sin limite de tamano por archivo.
- Sin omitir archivos tras timeouts de reglas.
- Con 60 segundos por regla y archivo.
- Ignorando supresiones `nosemgrep`.
- Fallando ante cualquier hallazgo nuevo o configuracion invalida.

La unica excepcion revisada corresponde a la regla de baja confianza `javascript.lang.security.audit.prototype-pollution.prototype-pollution-loop.prototype-pollution-loop` sobre `AnimatePresence` de Framer Motion. El codigo detectado usa `Map` y `Set`, que son la mitigacion indicada por la propia regla. `Herramientas/ValidarResultadosSemgrep.py` solo la admite cuando coinciden regla, ruta, linea y SHA-256 del bundle completo. No se deshabilita la regla ni se excluye el archivo; cualquier cambio del bundle invalida automaticamente la excepcion.

Las etapas PowerShell del workflow se mantienen en `Herramientas/EjecutarEtapaCi.ps1`. Esto permite que Semgrep analice el workflow completo con `--strict` y evita duplicar la logica de preparacion, publicacion y comprobacion del artefacto.

El workflow de publicacion en GitHub actua como respaldo y ejecuta:

- Restore.
- Instalacion y validacion de PowerShell 7.6.0.
- Build Release.
- Tests xUnit.
- Publicacion x64 por `PublicarPortable.ps1`.
- Validacion de firma en `main`.
- Hash SHA-256 del artefacto.

Sus referencias externas estan fijadas a commits SHA completos. Para usar la publicacion de respaldo, configure estos secretos en GitHub:

- `WINDOWS_SIGNING_CERT_BASE64`
- `WINDOWS_SIGNING_CERT_PASSWORD`

## Desarrollo

- Mantener encabezado `(Autor: Alex Roman)` y descripcion en archivos nuevos.
- Usar comentarios simples solo para funcionalidad.
- Mantener endpoints `/api/*` compatibles.
- No introducir permisos fail-open.
- No publicar tokens en variables globales.
- No permitir scripts elevados sin allowlist.
