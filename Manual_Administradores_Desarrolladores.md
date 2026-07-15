<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Manual tecnico para administradores y desarrolladores del lanzador. -->

# Manual De Administradores Y Desarrolladores

## Objetivo

Este manual describe la operacion, configuracion, seguridad, pruebas y publicacion de LanzadorScripts en entorno Windows corporativo.

## Arquitectura

- WPF ejecuta la ventana principal con elevacion UAC mediante `requireAdministrator`.
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
| Perfil WebView2 | `%ProgramData%\LanzadorScripts\Usuarios\<id-SID>\WebView2\Perfil` |
| Runtime WebView2 principal | `%ProgramFiles%\LanzadorScripts\Runtimes\WebView2\<hash-version>` |
| Staging TOCTOU | `%ProgramFiles%\LanzadorScripts\Staging` |

## Permisos

`permisos.json` es un contenedor cifrado y firmado. El catalogo usa la misma proteccion en `catalogo-scripts.json`. Ambos emplean AES-256-GCM y RSA-PSS/SHA-256, con tipo autenticado para impedir el intercambio de archivos.

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

Durante la publicacion se descarga o reutiliza WebView2 Fixed Version Runtime x64 `150.0.4078.48`. Se validan los hashes del CAB, ZIP, ejecutable y contenido completo, la arquitectura x64 y la firma de Microsoft antes de embeber el recurso. Al arrancar se vuelve a comprobar la huella completa de la copia extraida, se reemplaza si fue alterada y se conceden los permisos de lectura y ejecucion requeridos por AppContainer. El runtime se ejecuta solo desde `Program Files`; un bloqueo explicito de WDAC o AppLocker requiere una regla corporativa. La publicacion exige `pwsh 7.6.x`; la cache queda en `Recursos\WebView2` y no se versiona.

Para inicializarlos expresamente:

```powershell
pwsh -NoProfile -File .\Herramientas\PublicarPortable.ps1 -CertThumbprint "<THUMBPRINT>" -InicializarArtefactos
```

No se instala ningun servicio, certificado, cuenta, tarea ni puerto. Las claves integradas permiten portabilidad completa, con el riesgo aceptado de extraccion mediante ingenieria inversa.

## CI

El workflow de GitHub ejecuta:

- Restore.
- Instalacion y validacion de PowerShell 7.6.0.
- Build Release.
- Tests xUnit.
- Publicacion x64 por `PublicarPortable.ps1`.
- Validacion de firma en `main`.
- Hash SHA-256 del artefacto.

Configurar secretos:

- `WINDOWS_SIGNING_CERT_BASE64`
- `WINDOWS_SIGNING_CERT_PASSWORD`

## Desarrollo

- Mantener encabezado `(Autor: Alex Roman)` y descripcion en archivos nuevos.
- Usar comentarios simples solo para funcionalidad.
- Mantener endpoints `/api/*` compatibles.
- No introducir permisos fail-open.
- No publicar tokens en variables globales.
- No permitir scripts elevados sin allowlist.
