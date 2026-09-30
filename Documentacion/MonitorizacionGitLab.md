# Monitorizacion tecnica de LanzadorScripts

Autor: Alex Roman

Descripcion: Configuracion, privacidad y comprobacion de OpenTelemetry en GitLab.

## Funcionamiento

La aplicacion incorpora OpenTelemetry .NET 1.18.0. La monitorizacion se inicia una
sola vez en el proceso principal; no se inicia en el broker elevado, comprobaciones
del instalador ni al restaurar una instancia existente. No requiere modificar la
base de datos, los permisos, el catalogo ni el protocolo de autenticacion.

Los componentes se distinguen mediante `service.name`:

- `LanzadorScripts.Cliente`: cliente instalado.
- `LanzadorScripts.Portable`: cliente portable.
- `LanzadorScripts.Consola`: consola administrativa.
- `LanzadorScripts.Servidor`: servicio Windows central.

Se exportan duraciones y resultados de arranque, operaciones del cliente nativo,
operaciones del servicio, conexiones y ejecuciones. Las metricas incluyen
`lanzador.operaciones`, `lanzador.operacion.duracion`, `lanzador.proceso.memoria`
y `lanzador.gc.memoria`. Los logs remotos contienen exclusivamente el nombre
tecnico de una operacion fallida, sin la excepcion original. Los fallos previos al
inicio de .NET, del lanzador nativo o cierres forzados pueden no llegar a GitLab.

Las trazas son locales a cada proceso; no se transmite contexto por el protocolo
Windows existente y no se presenta un mapa de dependencias distribuido completo.
`service.version` incluye version y SHA de compilacion; `gitlab.project.id` es
`84894342`. La integracion no cambia la auditoria operativa de la base central.

## Privacidad y disponibilidad

No se envian cuentas, SID, nombres de equipo, direcciones IP internas, rutas,
argumentos, nombres o contenido de scripts, salida de consola, excepciones,
consultas SQL, permisos, tokens ni eventos completos de auditoria. Los nombres
de operaciones y entornos se limitan a una lista fija; valores desconocidos se
sustituyen por una etiqueta generica. El servicio receptor si puede observar la
IP publica de salida de la conexion HTTPS.

GitLab Observability es experimental. La telemetria es diagnostica y de mejor
esfuerzo: una caida de GitLab nunca bloquea el trabajo, ni lo hace la perdida de
eventos. Hay colas en memoria de 512 entradas y lotes de 64; no hay cola en disco.
Se exportan trazas y logs cada 5 segundos y metricas cada 30 segundos. El timeout
HTTP es 1,5 segundos y el cierre solicita como maximo 1 segundo por proveedor.
La portable no guarda datos de monitorizacion. Si se configura el reintento OTLP
experimental en disco, esta integracion se desactiva para no crear esos archivos.

No se exportan los logs existentes de inicio ni los mensajes de `ILogger` del host.
No se desactiva la validacion TLS, no se siguen redirecciones y no se envian
credenciales Windows ni cookies. El destino proporcionado por GitLab no exige
un token de ingestion; no se ha incrustado una clave privada ni un token de API.
La API de consultas/MCP de Observability tiene una autenticacion independiente.

## Configuracion de despliegue

El grupo ya tiene habilitada la recepcion en:

`https://138431379.otel.gitlab-o11y.com:14318`

Se utiliza OTLP HTTP/protobuf, rutas `/v1/traces`, `/v1/metrics` y `/v1/logs`.
Es necesaria salida HTTPS TCP 14318 desde cada equipo que se monitorice. No se
abre ningun puerto entrante ni se modifican las reglas del firewall.

Variables opcionales, leidas al iniciar el proceso:

| Variable | Valores |
| --- | --- |
| `LANZADOR_MONITORIZACION_HABILITADA` | Por defecto activa. `false` o `0` desactiva; solo `true` o `1` activa explicitamente. |
| `LANZADOR_MONITORIZACION_ENDPOINT` | URL HTTPS raiz de un colector aprobado, sin ruta, credenciales, consulta ni fragmento. Por defecto el destino anterior. |
| `LANZADOR_MONITORIZACION_ENTORNO` | `production` (predeterminado), `staging`, `development` o `test`. |

Las configuraciones invalidas desactivan el envio, no impiden abrir la app. Las
variables OTLP genericas de endpoint, headers y atributos no sustituyen estos
valores. Las directivas corporativas pueden desactivar la monitorizacion antes
de desplegar; para un servicio Windows configure su entorno mediante el mecanismo
de despliegue corporativo y reinicie el servicio para aplicar los cambios.

No hay que instalar un agente ni aprovisionar certificados nuevos. Para observar
el servidor se debe desplegar tambien la compilacion nueva del servicio; cambiar
solo los clientes no instrumenta los servicios antiguos. Esta modificacion de
codigo no instala ni reinicia el servicio del aeropuerto.

## Comprobacion

Desde la raiz del repositorio, esta prueba manual envia exclusivamente datos
sinteticos; no abre la base de datos ni ejecuta scripts:

```powershell
dotnet run --project Compartido/LanzadorScripts.Monitorizacion.Comprobacion -c Release -- --enviar-prueba
```

Abra [Observe > Services](https://gitlab.com/groups/micro2822131/-/observability/services)
y busque `LanzadorScripts.Cliente` en el entorno `test`. Compruebe la operacion
`monitorizacion.prueba` en Traces, las cuatro metricas y el aviso sintetico en Logs.
Finalizar la utilidad no prueba por si mismo que GitLab haya recibido los datos.
Las pruebas automaticas usan un receptor en memoria y no envian datos a Internet.

## Referencias

- [Configuracion del grupo](https://gitlab.com/groups/micro2822131/-/observability/setup).
- [Instrumentar y enviar telemetria a GitLab](https://docs.gitlab.com/operations/observability/send/).
- [OpenTelemetry .NET](https://opentelemetry.io/docs/languages/dotnet/).
