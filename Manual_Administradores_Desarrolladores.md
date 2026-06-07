<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Manual tecnico para administradores y desarrolladores del lanzador. -->

# Manual De Administradores Y Desarrolladores

## Objetivo

Este manual describe la operacion, configuracion, seguridad, pruebas y publicacion de LanzadorScripts en entorno Windows corporativo.

## Arquitectura

- WPF ejecuta la ventana principal con elevacion global mediante `requireAdministrator`.
- WebView2 muestra el cliente embebido y se comunica con un servidor HTTP local.
- El servidor local exige cookie de sesion y token interno por arranque para `/api/*`.
- Los endpoints de administracion requieren ademas autorizacion de administrador local.
- Los scripts se ejecutan desde el proceso principal elevado. El broker queda como compatibilidad interna si se arranca sin elevacion.

## Rutas Operativas

| Recurso | Ruta |
|---|---|
| Configuracion usuario | `%AppData%\LanzadorScripts\configuracion.dat` |
| Configuracion equipo | `C:\ProgramData\LanzadorScripts\configuracion.dat` |
| Tokens de administrador | `%AppData%\LanzadorScripts\Tokens` |
| Logs de ejecucion | `%LocalAppData%\LanzadorScripts\Logs` |
| Auditoria | `%LocalAppData%\LanzadorScripts\Auditoria` |
| Perfil WebView2 | `%LocalAppData%\LanzadorScripts\WebView2` |
| Staging TOCTOU | `%LocalAppData%\LanzadorScripts\Staging` |

## Permisos

`permissions.json` debe estar firmado por el certificado corporativo configurado en la aplicacion. Si falta, esta corrupto o no es accesible, la aplicacion bloquea la ejecucion por defecto.

La configuracion predeterminada embebida apunta a `\\MAD002MICROPRU\C$\REPO` y `\\MAD002MICROPRU\C$\REPO\PERMISOS\permisos.json`. Las instalaciones que hayan guardado la ruta anterior sin `C$` se migran automaticamente al arrancar.

La politica de seguridad vive en `seguridadScripts`:

```json
{
  "certificadosPowerShellPermitidos": ["THUMBPRINT"],
  "hashesBatchPermitidos": [
    { "scriptId": "carpeta/script.cmd", "sha256": "HASH_SHA256" }
  ],
  "scriptsElevadosPermitidos": ["admin/script.ps1"],
  "permitirExecutionPolicyBypass": false
}
```

Reglas:

- `.ps1` requiere firma Authenticode valida de certificado permitido.
- `.bat` y `.cmd` requieren SHA-256 permitido.
- `scriptsElevadosPermitidos` se conserva por compatibilidad, pero con la app elevada todos los scripts permitidos salen del proceso principal.
- Los permisos por defecto solo sirven para formularios vacios y nunca autorizan ejecucion.

## Emergencia

El token maestro esta firmado por el certificado privado autorizado de Alex Roman y permite abrir una sesion de emergencia cuando los permisos no estan disponibles.

- TTL del token: sin caducidad operativa en la aplicacion.
- Uso: reutilizable mientras se conserve protegido y la firma sea valida.
- Alcance: sesion de emergencia con rol administrador para poder abrir Ajustes.
- Auditoria: intento, resultado, emisor, usuario y equipo.

## Broker Elevado

La aplicacion principal exige administrador. El broker elevado se mantiene como mecanismo interno de compatibilidad para ejecuciones iniciadas sin elevacion, pero no es la ruta normal de operacion.

Controles:

- Named pipe con nombre aleatorio por ejecucion.
- Token efimero de 256 bits para autenticar el canal.
- Restriccion `CurrentUserOnly` en el pipe.
- Staging local validado antes de ejecutar.
- Cancelacion controlada desde la app principal.

Limitacion operativa: la entrada interactiva no esta disponible para ejecuciones elevadas por broker. Los scripts elevados deben ser no interactivos.

## Mitigacion TOCTOU

Antes de ejecutar:

1. Se valida firma o hash del script original.
2. Se copia a staging local.
3. Se aplica ACL restrictiva y atributo de solo lectura.
4. Se revalida firma o hash de la copia.
5. Se ejecuta la copia y se registra el hash final.

## Salud Y Diagnostico

`/api/salud` devuelve un estado resumido sin sesion y diagnostico completo solo con sesion interna valida.

El diagnostico completo incluye:

- Version.
- Rutas operativas.
- Estado de permisos.
- Estado de auditoria.
- WebView2.
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
- Firma/hash de scripts.
- Ejecucion real con eventos finales.

## Publicacion

La publicacion final se hace con:

```powershell
.\Herramientas\PublicarPortable.ps1 -CertThumbprint "<THUMBPRINT>"
```

Para pruebas locales sin firma:

```powershell
.\Herramientas\PublicarPortable.ps1 -AllowUnsignedForDev
```

No distribuir binarios de `bin`, `obj` ni `publicacion` versionados manualmente. El artefacto final debe salir del pipeline.

## CI

El workflow de GitHub ejecuta:

- Restore.
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
