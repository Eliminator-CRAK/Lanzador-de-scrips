<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Manual tecnico para administradores y desarrolladores del lanzador. -->

# Manual De Administradores Y Desarrolladores

## Objetivo

Este manual describe la operacion, configuracion, seguridad, pruebas y publicacion de LanzadorScripts en entorno Windows corporativo.

## Arquitectura

- WPF ejecuta la ventana principal sin elevacion global mediante `asInvoker`.
- WebView2 muestra el cliente embebido y se comunica con un servidor HTTP local.
- El servidor local exige cookie de sesion y token interno por arranque para `/api/*`.
- Los endpoints de administracion requieren ademas autorizacion de administrador local.
- Los scripts allowlistados como elevados se ejecutan mediante broker minimo bajo demanda.

## Rutas Operativas

| Recurso | Ruta |
|---|---|
| Configuracion usuario | `%AppData%\LanzadorScripts\configuracion.dat` |
| Configuracion equipo | `C:\ProgramData\LanzadorScripts\configuracion.dat` |
| Tokens de administrador | `%AppData%\LanzadorScripts\Tokens` |
| Tokens maestro usados | `%AppData%\LanzadorScripts\Tokens\tokens-maestros-usados.json` |
| Logs de ejecucion | `%LocalAppData%\LanzadorScripts\Logs` |
| Auditoria | `%LocalAppData%\LanzadorScripts\Auditoria` |
| Perfil WebView2 | `%LocalAppData%\LanzadorScripts\WebView2` |
| Staging TOCTOU | `%LocalAppData%\LanzadorScripts\Staging` |

## Permisos

`permissions.json` debe estar firmado por el certificado corporativo configurado en la aplicacion. Si falta, esta corrupto o no es accesible, la aplicacion bloquea la ejecucion por defecto.

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
- Los scripts elevados deben estar en `scriptsElevadosPermitidos`.
- Los permisos por defecto solo sirven para formularios vacios y nunca autorizan ejecucion.

## Emergencia

El token maestro es temporal, de un solo uso, vinculado a usuario y equipo, y requiere motivo operativo.

- TTL: 10 minutos.
- Uso: una sola vez, persistido localmente.
- Alcance: sesion de emergencia sin rol administrador total.
- Auditoria: intento, resultado, motivo, usuario y equipo.

## Broker Elevado

La aplicacion principal no exige administrador. Cuando un script esta allowlistado como elevado, la app lanza el mismo ejecutable en modo interno `--broker-elevado` mediante UAC.

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
- Validacion de rutas y admin shares.
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
