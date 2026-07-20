<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Manual de uso para usuarios normales del lanzador. -->

# Manual De Usuario

## Objetivo

LanzadorScripts permite ejecutar scripts autorizados desde una interfaz local. La aplicacion valida permisos, integridad y disponibilidad antes de permitir una ejecucion.

## Inicio

1. Abra `LanzadorScripts.exe`.
2. Acepte el aviso de administrador de Windows si aparece.
3. Espere a que finalice la pantalla de preparacion.
4. Si aparece un aviso de permisos, WebView2 o conexion, no ejecute scripts y contacte con el administrador.

## Ejecutar Un Script

1. Abra la carpeta autorizada que contiene el script.
2. Use `← Volver` para subir una carpeta o `Principal` para regresar al inicio.
3. Busque el script en la lista.
4. Revise si aparece bloqueado y lea el motivo.
5. Pulse ejecutar solo si el script esta permitido.
6. Mantenga la consola visible hasta ver el evento final.
7. Revise el codigo de salida cuando termine.

## Entrada Interactiva

Algunos scripts pueden pedir datos. Escriba solo la informacion solicitada por el procedimiento operativo. No introduzca contrasenas, tokens o claves si el script no esta aprobado para ello.

## Cancelar Una Ejecucion

Use cancelar solo si el script se ha quedado bloqueado o si el procedimiento lo exige. La cancelacion queda auditada con usuario, equipo, script y hora.

## Estados Habituales

| Estado | Accion |
|---|---|
| Script bloqueado por permisos | Solicitar autorizacion al administrador. |
| Script bloqueado por firma/hash | No ejecutar. El administrador debe revisar integridad. |
| Backend local no disponible | Reiniciar la app y avisar a soporte si se repite. |
| Carpeta remota de scripts no disponible | Esperar recuperacion de red; la interfaz seguira visible pero los scripts quedan bloqueados. |
| Carpeta remota de permisos no disponible | No ejecutar. Solo administracion puede activar emergencia temporal; los cambios de permisos quedan bloqueados mientras la carpeta siga inaccesible. |
| Permisos ausentes o no validos | No ejecutar y comunicar el mensaje exacto al administrador. |
| WebView2 no disponible | Reiniciar la app. Si se repite, avisar a soporte con la ruta de logs. |
| El error muestra una ruta dentro de AppData | El EXE no corresponde a la version 1.4.3 o se ha abierto el componente interno. Cierre la app y use el `LanzadorScripts.exe` distribuido. |

## Logs

Los logs de ejecucion se guardan en `%ProgramData%\LanzadorScripts\Usuarios\<id-SID>\Logs`. No modifique ni borre logs salvo indicacion del administrador.

## Buenas Practicas

- Ejecute solo scripts necesarios para su tarea.
- No comparta capturas con tokens, rutas internas o datos sensibles.
- No cierre la aplicacion hasta que el script muestre resultado final.
- Informe cualquier mensaje inesperado o bloqueo repetido.
