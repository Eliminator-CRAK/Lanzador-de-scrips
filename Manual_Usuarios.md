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

1. Busque el script en la lista.
2. Revise si aparece bloqueado y lea el motivo.
3. Pulse ejecutar solo si el script esta permitido.
4. Mantenga la consola visible hasta ver el evento final.
5. Revise el codigo de salida cuando termine.

## Entrada Interactiva

Algunos scripts pueden pedir datos. Escriba solo la informacion solicitada por el procedimiento operativo. No introduzca contrasenas, tokens o claves si el script no esta aprobado para ello.

## Cancelar Una Ejecucion

Use cancelar solo si el script se ha quedado bloqueado o si el procedimiento lo exige. La cancelacion queda auditada con usuario, equipo, script y hora.

## Estados Habituales

| Estado | Accion |
|---|---|
| Script bloqueado por permisos | Solicitar autorizacion al administrador. |
| Script bloqueado por firma/hash | No ejecutar. El administrador debe revisar integridad. |
| No se puede conectar al servidor | Esperar recuperacion de red o avisar a soporte. |
| Permisos no disponibles | No ejecutar. Solo administracion puede activar emergencia temporal. |
| WebView2 no disponible | Reiniciar la app. Si se repite, avisar a soporte con la ruta de logs. |

## Logs

Los logs de ejecucion se guardan en `%LocalAppData%\LanzadorScripts\Logs`. No modifique ni borre logs salvo indicacion del administrador.

## Buenas Practicas

- Ejecute solo scripts necesarios para su tarea.
- No comparta capturas con tokens, rutas internas o datos sensibles.
- No cierre la aplicacion hasta que el script muestre resultado final.
- Informe cualquier mensaje inesperado o bloqueo repetido.
