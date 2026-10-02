// (Autor: Alex Roman)
// Descripcion: Exporta trazas, metricas y avisos tecnicos acotados a GitLab mediante OTLP.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace LanzadorScripts.Monitorizacion;

public sealed class MonitorizacionAplicacion : IDisposable
{
    internal const string NombreInstrumentacion = "LanzadorScripts.Monitorizacion";
    private static readonly MonitorizacionAplicacion Desactivada = new();
    public static MonitorizacionAplicacion Actual { get; private set; } = Desactivada;
    private readonly ActivitySource _actividades = new(NombreInstrumentacion);
    private readonly Meter _medidor = new(NombreInstrumentacion);
    private TracerProvider? _trazas;
    private MeterProvider? _metricas;
    private ILoggerFactory? _fabricaLogs;
    private BatchLogRecordExportProcessor? _procesadorLogs;
    private ILogger? _log;
    private Counter<long>? _operaciones;
    private Histogram<double>? _duracion;
    private int _cerrada;

    private MonitorizacionAplicacion() { }

    public static MonitorizacionAplicacion Iniciar(ComponenteMonitorizado componente)
    {
        var sesion = Crear(componente, ConfiguracionMonitorizacion.Leer(Environment.GetEnvironmentVariable));
        Actual = sesion;
        return sesion;
    }

    internal static MonitorizacionAplicacion Crear(
        ComponenteMonitorizado componente,
        ConfiguracionMonitorizacion configuracion,
        Func<HttpClient>? crearHttp = null)
    {
        var sesion = new MonitorizacionAplicacion();
        if (!configuracion.Habilitada)
        {
            return sesion;
        }

        try
        {
            sesion.Configurar(componente, configuracion, crearHttp);
        }
        catch
        {
            // Un fallo del monitor nunca sustituye el resultado de la aplicacion.
            sesion.Dispose();
            return new MonitorizacionAplicacion();
        }
        return sesion;
    }

    internal bool Habilitada => _trazas is not null && Volatile.Read(ref _cerrada) == 0;

    private void Configurar(ComponenteMonitorizado componente,
        ConfiguracionMonitorizacion configuracion, Func<HttpClient>? crearHttp)
    {
        var servicio = componente switch
        {
            ComponenteMonitorizado.ClientePortable => "LanzadorScripts.Portable",
            ComponenteMonitorizado.ConsolaServidor => "LanzadorScripts.Consola",
            ComponenteMonitorizado.ServicioServidor => "LanzadorScripts.Servidor",
            _ => "LanzadorScripts.Cliente"
        };
        var version = typeof(MonitorizacionAplicacion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        // No usar CreateDefault: podria incorporar host, usuario o atributos del entorno.
        var recurso = ResourceBuilder.CreateEmpty()
            .AddService(servicio, serviceVersion: version, autoGenerateServiceInstanceId: false)
            .AddAttributes(new Dictionary<string, object>
            {
                ["gitlab.project.id"] = "84894342",
                ["gitlab.project.name"] = "Lanzador-de-scrips",
                ["deployment.environment.name"] = configuracion.Entorno
            });

        void Exportador(OtlpExporterOptions opciones, string senal)
        {
            opciones.Endpoint = new Uri(configuracion.Destino, $"v1/{senal}");
            opciones.Protocol = OtlpExportProtocol.HttpProtobuf;
            opciones.Headers = string.Empty;
            opciones.TimeoutMilliseconds = 1500;
            opciones.HttpClientFactory = crearHttp ?? (() => new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseDefaultCredentials = false
            }) { Timeout = TimeSpan.FromMilliseconds(1500) });
            opciones.ExportProcessorType = ExportProcessorType.Batch;
            opciones.BatchExportProcessorOptions = new BatchExportProcessorOptions<Activity>
            {
                MaxQueueSize = 512,
                MaxExportBatchSize = 64,
                ScheduledDelayMilliseconds = 5000,
                ExporterTimeoutMilliseconds = 1500
            };
        }

        _trazas = Sdk.CreateTracerProviderBuilder().SetResourceBuilder(recurso)
            .AddSource(NombreInstrumentacion).SetSampler(new AlwaysOnSampler())
            .AddOtlpExporter(o => Exportador(o, "traces")).Build();
        _operaciones = _medidor.CreateCounter<long>("lanzador.operaciones", "{operation}");
        _duracion = _medidor.CreateHistogram<double>("lanzador.operacion.duracion", "s");
        _medidor.CreateObservableGauge("lanzador.proceso.memoria", () => Environment.WorkingSet, "By");
        _medidor.CreateObservableGauge("lanzador.gc.memoria", () => GC.GetTotalMemory(false), "By");
        _metricas = Sdk.CreateMeterProviderBuilder().SetResourceBuilder(recurso)
            .AddMeter(NombreInstrumentacion)
            .AddOtlpExporter((o, lector) =>
            {
                Exportador(o, "metrics");
                lector.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 30000;
                lector.PeriodicExportingMetricReaderOptions.ExportTimeoutMilliseconds = 1500;
            }).Build();
        var opcionesLogs = new OtlpExporterOptions();
        Exportador(opcionesLogs, "logs");
        _procesadorLogs = new BatchLogRecordExportProcessor(new OtlpLogExporter(opcionesLogs),
            maxQueueSize: 512, scheduledDelayMilliseconds: 5000,
            exporterTimeoutMilliseconds: 1500, maxExportBatchSize: 64);
        _fabricaLogs = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
        {
            o.SetResourceBuilder(recurso);
            o.IncludeScopes = false;
            o.IncludeFormattedMessage = true;
            o.AddProcessor(_procesadorLogs);
        }));
        _log = _fabricaLogs.CreateLogger(NombreInstrumentacion);
    }

    public MedicionOperacion Medir(string operacion, ActivityKind clase = ActivityKind.Internal) =>
        new(this, NormalizarOperacion(operacion), clase);

    internal static string NormalizarOperacion(string? operacion) => operacion switch
    {
        "aplicacion.inicio" or "webview2.inicio" or "servicio.inicio" or "servicio.cierre"
        or "backend.solicitud" or "script.ejecucion" or "servidor.conexion" or "monitorizacion.prueba"
        or "consola.inicio" or "salud" or "permisos.obtener" or "permisos.guardar"
        or "catalogo.obtener" or "catalogo.guardar" or "auditoria.registrar" or "auditoria.consultar"
        or "usuarios.listar" or "usuarios.guardar" or "usuarios.eliminar"
        or "mantenimiento.copia" or "mantenimiento.integridad"
        or "actualizacion.obtener" or "actualizacion.estado" or "actualizacion.publicar" or "actualizacion.gestionar"
        or "configuracion.obtener" or "configuracion.guardar" => operacion,
        _ => "operacion.desconocida"
    };

    internal bool Vaciar(int tiempoMs = 1000) =>
        (_trazas?.ForceFlush(tiempoMs) ?? true)
        & (_metricas?.ForceFlush(tiempoMs) ?? true)
        & (_procesadorLogs?.ForceFlush(tiempoMs) ?? true);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _cerrada, 1) != 0) return;
        if (ReferenceEquals(Actual, this)) Actual = Desactivada;
        // Cada proveedor tiene un cierre acotado y no mantiene una cola en disco.
        try { _trazas?.Shutdown(1000); } catch { }
        try { _metricas?.Shutdown(1000); } catch { }
        try { _procesadorLogs?.Shutdown(1000); } catch { }
        try { _fabricaLogs?.Dispose(); } catch { }
        try { _metricas?.Dispose(); } catch { }
        try { _trazas?.Dispose(); } catch { }
        _actividades.Dispose();
        _medidor.Dispose();
    }

    public sealed class MedicionOperacion : IDisposable
    {
        private readonly MonitorizacionAplicacion _sesion;
        private readonly string _operacion;
        private readonly Activity? _actividad;
        private readonly long _inicio = Stopwatch.GetTimestamp();
        private bool _exito;
        private int _finalizada;

        internal MedicionOperacion(MonitorizacionAplicacion sesion, string operacion, ActivityKind clase)
        {
            _sesion = sesion;
            _operacion = operacion;
            try
            {
                if (sesion.Habilitada)
                    _actividad = sesion._actividades.StartActivity(operacion, clase);
            }
            catch { }
        }

        public void Completar(bool exito = true) => _exito = exito;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _finalizada, 1) != 0) return;
            try
            {
                if (!_sesion.Habilitada) return;
                var resultado = _exito ? "ok" : "error";
                var etiquetas = new TagList { { "operacion", _operacion }, { "resultado", resultado } };
                _actividad?.SetTag("resultado", resultado);
                _actividad?.SetStatus(_exito ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
                _sesion._operaciones?.Add(1, etiquetas);
                _sesion._duracion?.Record(Stopwatch.GetElapsedTime(_inicio).TotalSeconds, etiquetas);
                if (!_exito)
                    _sesion._log?.LogWarning("Operacion tecnica fallida: {operacion}", _operacion);
            }
            catch
            {
                // La telemetria nunca altera el resultado funcional ni la auditoria.
            }
            finally { _actividad?.Dispose(); }
        }
    }
}
