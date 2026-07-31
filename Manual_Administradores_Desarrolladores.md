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
| Paquete central de clave | `<RutaPermisos>\clave-artefactos.dpng.json` |
| Perfiles WebView2 | `%LocalAppData%\LanzadorScripts\WebView2-v4\Sesiones\Sesion-<GUID>` |
| Temporales de proceso | `%ProgramData%\LanzadorScripts\Usuarios\<id-SID>\Temporales` |
| Aplicacion .NET interna | `%ProgramFiles%\LanzadorScripts\Aplicacion\runtime-<hash>` |
| Extraccion nativa .NET | `%ProgramFiles%\LanzadorScripts\Runtimes\DotNet\runtime-<hash>` |
| Runtime WebView2 principal | `%ProgramFiles%\LanzadorScripts\Runtimes\WebView2\<hash-version>` |
| Staging TOCTOU | `%ProgramFiles%\LanzadorScripts\Staging` |

## Permisos

`permisos.json` y `catalogo-scripts.json` son contenedores cifrados y firmados. Ambos emplean la misma AES-256-GCM y la misma identidad de firma RSA-PSS/SHA-256, con tipo autenticado para impedir el intercambio de archivos. La version 1.5.0 conserva lectura verificada de los dos v1 corporativos cuyas huellas estan fijadas para la migracion y escribe exclusivamente v2.

La clave AES no forma parte del EXE. Se distribuye en `clave-artefactos.dpng.json`, cifrada con DPAPI-NG para un grupo de Active Directory o para el mismo perfil con `LOCAL=user`, y firmada con RSA-PSS/SHA-256. El paquete local solo puede abrirse en el usuario y equipo que lo genero. En el primer arranque autorizado se guarda con DPAPI `LocalMachine` en `%ProgramData%\LanzadorScripts\Seguridad\artefactos.key`, con acceso exclusivo para `SYSTEM` y `Administrators`. La firma usa el certificado privado con huella `500266A64E574889370D92E5CE0D65D55CC963B7`; los equipos que solo verifican no necesitan la clave privada.

La configuracion predeterminada apunta a:

- Scripts: `\\MAD002MICROPRU.mad.ae.aena.es\R$\SCRIPS`
- Permisos: `\\MAD002MICROPRU.mad.ae.aena.es\R$\PERMISOS`

La ruta de permisos siempre representa una carpeta. La aplicacion busca dentro `permisos.json`, `catalogo-scripts.json` y `clave-artefactos.dpng.json`; no usa copias junto al EXE. Los tres archivos son obligatorios en equipos nuevos y deben compartir el mismo `KeyId`. Las configuraciones antiguas que incluian el nombre del archivo se migran a su carpeta.

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

## Migracion A La Version 1.5.6

1. Ejecute `GenerarConjuntoArtefactos.ps1` en un equipo conectado al dominio y con el certificado privado de artefactos.
2. Indique la cuenta administradora, su SID de Active Directory, la carpeta de scripts y una carpeta de salida vacia.
3. La herramienta crea una AES en memoria y genera `permisos.json`, `catalogo-scripts.json` y `clave-artefactos.dpng.json` con el mismo `KeyId`.
4. Conserve una copia de seguridad del conjunto anterior y sustituya siempre los tres archivos a la vez.
5. No copie `artefactos.key` al servidor ni entre equipos; cada cliente autorizado lo aprovisiona automaticamente.
6. Sustituya ambos ejecutables por la version 1.5.6.

La herramienta comprueba que la cuenta resuelve exactamente al SID indicado y que el catalogo contiene el numero esperado de scripts. Sin acceso a un controlador del dominio, DPAPI-NG no puede crear el paquete y no se genera ningun conjunto parcial.

Para generar un conjunto exclusivamente local desde una descarga de GitLab extraida como `Lanzador-de-scrips-main`, ejecute como `PCERA\alero` desde la propia raiz:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\PrepararArtefactosDominio.ps1
```

El script detecta la carpeta corporativa `ACTUALES`, valida los 37 scripts y genera los tres archivos bajo `ArtefactosGenerados\conjunto-<fecha>-<id>`. No consulta Active Directory, no usa la ruta central y no copia nada al servidor. Use `-RutaScripts` cuando la carpeta se encuentre en otra ubicacion.

El conjunto local autoriza a `PCERA\alero` y protege la AES con `LOCAL=user`. Puede copiar los tres archivos juntos al servidor, pero el paquete solo se descifrara cuando la aplicacion se ejecute como ese usuario en el mismo equipo PCERA. Para aprovisionar otros equipos sigue siendo necesario generar un paquete con SID de dominio desde un equipo conectado al controlador de dominio.

## Migracion A La Version 1.5.5

1. Sustituya ambos ejecutables por la version 1.5.5.
2. Compruebe que `permisos.json`, `catalogo-scripts.json` y `clave-artefactos.dpng.json` estan juntos en la carpeta central.
3. No distribuya `artefactos.key`: cada cliente autorizado lo crea automaticamente con DPAPI local.
4. La aplicacion realiza doce intentos separados por quince segundos y recarga la interfaz si consigue la clave.
5. Ya no existe entrada manual de AES ni boton de instalacion de clave.

## Migracion A La Version 1.5.4

1. Sustituya ambos ejecutables por la version 1.5.4; no cambie `configuracion.dat`, `permisos.json`, `catalogo-scripts.json` ni las claves.
2. WebView2 deja de usar la carpeta antigua de `ProgramData` y crea una UDF nueva por sesion bajo `LocalAppData`.
3. No precree la carpeta `Sesion-<GUID>` ni modifique sus ACL; WebView2 debe aplicar sus permisos LowIL/AppContainer.
4. La carpeta antigua `%ProgramData%\LanzadorScripts\Usuarios\<id-SID>\WebView2` queda sin uso y puede archivarse con la aplicacion cerrada.

## Migracion A La Version 1.5.0

1. No cambie `configuracion.dat`, `permisos.json`, `catalogo-scripts.json`, `clave-artefactos.dpng.json` ni `artefactos.key`.
2. Distribuya `LanzadorScripts.exe` para conservar solo los runtimes actuales o `LanzadorScripts_Portable.exe` para eliminar toda la raiz de `Program Files` al salir.
3. Explique que cerrar la ventana deja la aplicacion en segundo plano y que el cierre definitivo se realiza desde la bandeja.
4. Verifique en un equipo piloto el progreso nativo, la restauracion de segunda instancia y el cierre con scripts activos.

Los dos EXE contienen el mismo payload firmado. `ProgramData` se conserva con ambas variantes.

## Migracion A La Version 1.5.0

1. Conserve juntos los `permisos.json` y `catalogo-scripts.json` v1 existentes; sus firmas historicas y su `KeyId` deben coincidir.
2. En el equipo publicador, restaure la AES original en `%ProgramData%\LanzadorScripts\Seguridad\artefactos.key` una sola vez. No genere otra clave.
3. Cree `clave-artefactos.dpng.json` para el grupo de Active Directory autorizado mediante `CrearPaqueteAprovisionamientoClave.ps1`.
4. Sustituya el EXE por la version 1.5.0. Cada cliente autorizado recuperara la AES automaticamente al arrancar y podra leer los dos artefactos v1.
5. Cuando un administrador vuelva a guardar permisos o publique el catalogo, los archivos nuevos se escribiran como v2 y se firmaran con el certificado actual.
6. No mezcle un archivo v1 con otro v2 que tenga un `KeyId` distinto. El aprovisionamiento y la ejecucion fallan de forma cerrada.

La compatibilidad v1 contiene solo la clave publica historica de verificacion y las huellas SHA-256 exactas de los dos archivos autorizados. Un v1 distinto se bloquea aunque presente una firma historica valida. El EXE no recupera ni incorpora la clave AES antigua ni ninguna clave RSA privada.

## Migracion A La Version 1.4.8

1. Conserve una copia administrativa de `permisos.json`, `catalogo-scripts.json` y de la clave AES custodiada.
2. Cree o seleccione un grupo de seguridad de Active Directory para los usuarios autorizados.
3. En el equipo publicador que ya tiene `artefactos.key` y el certificado privado, ejecute con acceso al dominio:

```powershell
pwsh -NoProfile -File .\Herramientas\CrearPaqueteAprovisionamientoClave.ps1 `
  -GrupoDominio 'MAD00\<GRUPO_SEGURIDAD>'
```

4. Verifique que `clave-artefactos.dpng.json` queda junto a los dos artefactos y que el grupo dispone de lectura sobre la carpeta.
5. Sustituya el EXE por la version 1.4.8. No copie `artefactos.key` entre equipos.
6. En el primer arranque, la aplicacion valida las firmas y los `KeyId`, usa la identidad de dominio para abrir DPAPI-NG y crea automaticamente la copia local.
7. Pruebe primero un equipo piloto conectado al dominio. Si Windows no autoriza el grupo o no hay acceso al controlador de dominio, el sistema permanece bloqueado y no instala una clave distinta.

La AES no se introduce en los clientes. El paquete central firmado es el unico mecanismo de aprovisionamiento admitido.

## Migracion A La Version 1.4.7

1. Sustituya el EXE por la version 1.4.7; no cambie `permisos.json`, `catalogo-scripts.json` ni `artefactos.key`.
2. Esta migracion queda superada por la version 1.5.4, que usa perfiles por sesion en `LocalAppData` y conserva las ACL predeterminadas.
3. No conceda acceso a `Everyone` ni modifique manualmente las ACL de `ProgramData`.
4. Tras validar el arranque, un administrador puede archivar el perfil antiguo con la aplicacion cerrada.

## Migracion A La Version 1.4.6

1. Conserve una copia administrativa de `permisos.json` y `catalogo-scripts.json`.
2. Si los archivos proceden del formato v1, exporte la configuracion con la version anterior antes de sustituirlos.
3. Genere y custodie una unica clave AES de 32 bytes fuera de Git, historiales de consola y archivos compartidos.
4. En esa version, ejecute `Herramientas\AprovisionarClaveArtefactos.ps1` como administrador en cada equipo que deba leer o publicar los contenedores.
5. Importe el paquete exportado o use `-InicializarArtefactos` solo para una instalacion nueva.
6. Publique de nuevo `catalogo-scripts.json` despues de cambiar, mover, renombrar o sustituir cualquier script.
7. Verifique un equipo cliente antes de retirar la copia de seguridad.

No edite directamente los dos JSON: en v2 son contenedores cifrados y firmados. Los equipos cliente necesitan la clave AES protegida por DPAPI, pero no el certificado privado. El certificado privado de artefactos se instala solo en los equipos autorizados para guardar permisos o publicar catalogos.

La clave debe crearse una sola vez y custodiarse en el gestor de secretos corporativo. El aviso de clave ausente no se resuelve generando una clave nueva en ese cliente. Si se rota la AES, regenere y firme los dos contenedores y el paquete DPAPI-NG desde el equipo publicador; cada cliente reemplazara la copia local solo cuando las tres firmas y los `KeyId` coincidan.

En la version actual, los clientes aprovisionan o rotan la clave automaticamente desde el paquete DPAPI-NG firmado. No existe entrada manual de AES. Si el paquete no esta disponible al arrancar, la aplicacion reintenta durante tres minutos y mantiene la ejecucion bloqueada hasta validar el conjunto completo.

La version 1.4.6 serializa el acceso a `configuracion.dat`, reintenta bloqueos transitorios y usa reemplazo atomico con copia `.bak`. Una carga ordinaria ya no reescribe el archivo. Si el archivo existente no se puede descifrar o validar, la aplicacion falla sin sustituirlo por rutas predeterminadas.

## Uso Manual Y Servicio Local

La aplicacion no registra tareas programadas ni configura la apertura con Windows.

Al abrir la ventana se inicia el backend integrado en el mismo proceso elevado. La cuenta debe tener acceso a la carpeta de scripts y a los dos contenedores protegidos.

## Emergencia

El token maestro esta firmado por el certificado privado autorizado de Alex Roman y permite abrir una sesion de emergencia cuando los permisos no estan disponibles. Solo puede generarlo una sesion de aplicacion que ya tenga rol administrador, un Bearer valido y acceso al certificado privado.

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

La carpeta `publicacion` debe contener unicamente `LanzadorScripts.exe` y `LanzadorScripts_Portable.exe`. Los dos contenedores protegidos y `clave-artefactos.dpng.json` permanecen juntos en la carpeta operativa de permisos.

Durante la publicacion se descarga o reutiliza WebView2 Fixed Version Runtime x64 `150.0.4078.48`. Se validan los hashes del CAB, ZIP, ejecutable y contenido completo, la arquitectura x64 y la firma de Microsoft antes de embeber el recurso. Al arrancar se vuelve a comprobar la huella completa de la copia extraida, se reemplaza si fue alterada y se conceden los permisos de lectura y ejecucion requeridos por AppContainer. El runtime se ejecuta solo desde `Program Files`; un bloqueo explicito de WDAC o AppLocker requiere una regla corporativa.

El publicador firma primero el runtime .NET interno y despues lo incluye como recurso de dos lanzadores nativos x64. Ambos validan la misma huella SHA-256 y establecen `DOTNET_BUNDLE_EXTRACT_BASE_DIR`, `TEMP` y `TMP` antes de que .NET pueda extraer archivos. La variante normal conserva solo las versiones actuales; la portable elimina la raiz completa al recibir el cierre definitivo. La aplicacion interna y la extraccion nativa quedan en `Program Files`; los temporales privados, la configuracion y los logs permanecen en `ProgramData`. Solo los perfiles temporales de WebView2 usan `LocalAppData`.

La publicacion exige `pwsh 7.6.x` y las herramientas C++ x64 de Visual Studio; la cache de WebView2 queda en `Recursos\WebView2` y no se versiona.

Para inicializarlos expresamente:

```powershell
pwsh -NoProfile -File .\Herramientas\PublicarPortable.ps1 -CertThumbprint "<THUMBPRINT>" -InicializarArtefactos
```

Antes de ejecutar `-InicializarArtefactos`, aprovisione la clave de 32 bytes en el equipo publicador:

```powershell
powershell.exe -NoProfile -File .\Herramientas\AprovisionarClaveArtefactos.ps1
```

La entrada se usa solo en el equipo publicador y es interactiva y segura. No introduzca la clave en argumentos, archivos de texto, Git ni historiales de consola. Despues cree `clave-artefactos.dpng.json` una sola vez con `CrearPaqueteAprovisionamientoClave.ps1`; los clientes no reciben la AES manualmente. Los contenedores v1 no son compatibles con la version 1.4.4 o posteriores: exporte primero la configuracion con la version anterior, haga copia de seguridad de los dos JSON, aprovisione la clave v2, importe el paquete y vuelva a publicar el catalogo.

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

La unica excepcion de hallazgo revisada corresponde a la regla de baja confianza `javascript.lang.security.audit.prototype-pollution.prototype-pollution-loop.prototype-pollution-loop` sobre `AnimatePresence` de Framer Motion. El codigo detectado usa `Map` y `Set`, que son la mitigacion indicada por la propia regla.

Semgrep tampoco puede analizar por completo cuatro zonas con sintaxis que su parser actual no admite: los literales raw de `VentanaPrincipal.xaml.cs` y `ServicioFirmaAuthenticode.cs`, el constructor primario de la clase interna de `GestorEjecucionesWeb.cs` y dos expresiones del bundle JavaScript minificado. `Herramientas/ValidarResultadosSemgrep.py` solo acepta cada hallazgo, error y omision cuando coinciden tipo, ruta, lineas y SHA-256 del contenido completo con finales de linea normalizados. No se deshabilitan reglas ni se excluyen archivos; cualquier cambio invalida automaticamente la excepcion correspondiente.

Los workflows llaman a `Herramientas/EjecutarSemgrepEstricto.sh`, que conserva `--strict` y entrega al validador el informe y el codigo real del motor. Las etapas PowerShell se mantienen en `Herramientas/EjecutarEtapaCi.ps1` para evitar duplicar la logica de preparacion, publicacion y comprobacion del artefacto.

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
