<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Explica el uso diario de LanzadorScripts para usuarios finales. -->

# Manual De Usuario

## Elegir Distribucion

El administrador puede entregar una de estas opciones:

```text
LanzadorScripts-1.7.1-x64.msi
LanzadorScripts_Portable-1.7.1-x64.exe
```

- La instalada aparece en el menu Inicio y conserva su configuracion.
- La portable se abre directamente y elimina sus datos locales cuando se cierra de forma definitiva.
- Los archivos exportados por el usuario no se eliminan.

La version `1.7.1` no instala ni solicita claves AES. Si aparece un aviso sobre `artefactos.key`, se esta usando una version anterior.

## Ejecutar Un Script

1. Busque el script por nombre.
2. Compruebe que no muestra un bloqueo de permisos o firma.
3. Pulse el script.
4. Siga la salida en la consola de la aplicacion.
5. Responda cuando el script solicite datos.

La aplicacion confirma primero el inicio en la auditoria corporativa. Si esa carpeta no esta disponible, el script no se ejecuta.

Un candado indica que la cuenta no esta autorizada, que el script no pertenece al catalogo o que sus bytes han cambiado.

## Cancelar Y Cerrar

- Cancelar detiene solo la ejecucion seleccionada.
- El boton de cerrar de la ventana mantiene la aplicacion en segundo plano.
- El icono de la bandeja permite restaurar, maximizar, minimizar o cerrar definitivamente.
- La advertencia de cancelacion solo aparece cuando existen scripts en ejecucion.
- El cierre puede esperar hasta 30 segundos para confirmar resultados de auditoria.

## Estados Habituales

| Estado | Accion |
|---|---|
| Auditoria no disponible | Compruebe la red corporativa. La ejecucion queda bloqueada por seguridad. |
| Permisos no encontrados | Compruebe la red o VPN y avise al administrador. |
| Permisos o catalogo no validos | No edite los JSON. Debe desplegarse de nuevo la pareja firmada. |
| ConjuntoId distinto | Se han mezclado dos publicaciones. Cierre la aplicacion y avise al administrador. |
| Script modificado | Solicite una nueva publicacion del catalogo. |
| Carpeta de scripts no disponible | Compruebe la red o VPN. |
| WebView2 no puede escribir | Cierre todas las instancias y entregue el log al administrador. |
| Archivo bloqueado por Windows | Verifique que usa la distribucion corporativa firmada. |

## Datos Y Logs

La version instalada conserva datos en:

```text
C:\ProgramData\LanzadorScripts\Usuarios\<perfil>\
```

La portable mantiene sus datos solo durante la sesion bajo `%TEMP%\LanzadorScripts\Portable`. La auditoria de ejecucion se almacena en el servidor y nunca se borra al cerrar o desinstalar.

## Buenas Practicas

- Use solo el MSI o la portable publicados por el administrador.
- No copie `permisos.json` y `catalogo-scripts.json` por separado.
- No modifique un script autorizado sin volver a publicar el catalogo.
- Cierre definitivamente desde la bandeja antes de actualizar o desinstalar.
