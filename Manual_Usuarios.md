<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Explica el uso diario de LanzadorScripts para usuarios finales. -->

# Manual De Usuario

## Inicio

Abra `LanzadorScripts.exe` o `LanzadorScripts_Portable.exe`. La aplicacion aparece tambien en el area de notificacion de Windows.

La version `1.6.0` no instala ni solicita claves AES. Si aparece un aviso sobre `artefactos.key`, el EXE es anterior a `1.6.0`.

## Ejecutar Un Script

1. Busque el script por nombre o abra su carpeta.
2. Compruebe que no muestra un bloqueo de permisos o de firma.
3. Pulse el script para iniciar la ejecucion.
4. Siga la salida en la consola de la aplicacion.
5. Responda en el campo interactivo cuando el script solicite datos.

Un candado indica que la cuenta no esta autorizada, que el script no pertenece al catalogo o que sus bytes cambiaron despues de publicarlo.

## Cancelar Y Cerrar

- La accion de cancelar detiene solo la ejecucion seleccionada.
- El boton de cerrar de la ventana mantiene la aplicacion en segundo plano.
- Desde el icono de la bandeja puede restaurar, maximizar, minimizar o cerrar definitivamente.
- La advertencia sobre cancelacion aparece solo cuando existen scripts en ejecucion.

## Estados Habituales

| Estado | Accion |
|---|---|
| Permisos no encontrados | Compruebe la red corporativa y comunique la ruta mostrada al administrador. |
| Permisos o catalogo no validos | No edite los JSON. El administrador debe desplegar de nuevo la pareja firmada. |
| ConjuntoId distinto | Se han mezclado dos publicaciones. Cierre la aplicacion y avise al administrador. |
| Script modificado | Solicite que el administrador vuelva a publicar el catalogo. |
| Carpeta de scripts no disponible | Compruebe la red o VPN. |
| WebView2 no puede escribir | Cierre todas las instancias y entregue al administrador el log de inicio. |
| EXE bloqueado por Windows | Verifique que usa el EXE corporativo firmado. |

## Logs

Los logs y la auditoria se guardan bajo:

```text
C:\ProgramData\LanzadorScripts\Usuarios\<perfil>\
```

No modifique `configuracion.dat`, los archivos de auditoria ni el perfil WebView2 mientras la aplicacion esta abierta.

## Buenas Practicas

- Use solo los ejecutables publicados por el administrador.
- No copie `permisos.json` o `catalogo-scripts.json` por separado.
- No modifique un script autorizado sin solicitar una nueva publicacion del catalogo.
- Cierre definitivamente desde la bandeja antes de sustituir el EXE.
