# (Autor: Alex Roman)
# Descripcion: Valida hallazgos Semgrep y limita las excepciones a artefactos revisados.

from __future__ import annotations

import hashlib
import json
import re
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any


@dataclass(frozen=True)
class ExcepcionSemgrep:
    regla: str
    ruta: str
    linea: int
    sha256: str
    motivo: str


@dataclass(frozen=True)
class ExcepcionErrorSemgrep:
    ruta: str
    tipo: str
    lineas: tuple[int, ...]
    sha256: str
    motivo: str


# Esta excepcion corresponde a codigo de Framer Motion que ya usa Map y Set.
EXCEPCIONES = (
    ExcepcionSemgrep(
        regla=(
            "javascript.lang.security.audit.prototype-pollution."
            "prototype-pollution-loop.prototype-pollution-loop"
        ),
        ruta="ClienteWeb/assets/index-DgdNDMM1.js",
        linea=127,
        sha256="3997C2C52228F40537FE579431E6369785EE1BD4E48786F8CFE4D04EA95BCF0F",
        motivo=(
            "Falso positivo de baja confianza en AnimatePresence de Framer Motion; "
            "el codigo detectado usa Map y Set."
        ),
    ),
)

# Estas excepciones identifican limitaciones conocidas del parser por huella completa.
EXCEPCIONES_ERRORES = (
    ExcepcionErrorSemgrep(
        ruta="VentanaPrincipal.xaml.cs",
        tipo="Syntax error",
        lineas=(1,),
        sha256="AEC977D55ADD39EE7828F7F4D4F30CD1F610D3AB47F4BBF8EAD7D5875443966D",
        motivo="El parser C# de Semgrep no admite los literales raw con JavaScript embebido.",
    ),
    ExcepcionErrorSemgrep(
        ruta="Servicios/ServicioFirmaAuthenticode.cs",
        tipo="Syntax error",
        lineas=(1,),
        sha256="F65A3E68D23CF2FF0C8C182ADF2B9339A9A4FBAB15FEF193ACAA17F16621FBBC",
        motivo="El parser C# de Semgrep no admite los literales raw interpolados.",
    ),
    ExcepcionErrorSemgrep(
        ruta="ClienteWeb/assets/index-DgdNDMM1.js",
        tipo="PartialParsing",
        lineas=(119, 119),
        sha256="3997C2C52228F40537FE579431E6369785EE1BD4E48786F8CFE4D04EA95BCF0F",
        motivo="El parser JavaScript omite dos expresiones del bundle minificado.",
    ),
)

LONGITUD_MAXIMA_INFORME = 50 * 1024 * 1024
LONGITUD_MAXIMA_ARCHIVO_EXCEPCION = 50 * 1024 * 1024


def cargar_informe(ruta_informe: Path, raiz: Path) -> dict[str, Any]:
    # Limita la lectura al informe generado dentro del repositorio.
    ruta_segura = ruta_informe.resolve(strict=True)
    ruta_segura.relative_to(raiz)
    if ruta_segura.stat().st_size > LONGITUD_MAXIMA_INFORME:
        raise ValueError("El informe Semgrep supera el tamano maximo permitido.")

    with ruta_segura.open("r", encoding="utf-8") as flujo:
        informe = json.load(flujo)

    if (
        not isinstance(informe, dict)
        or not isinstance(informe.get("results"), list)
        or not isinstance(informe.get("errors"), list)
    ):
        raise ValueError("El informe Semgrep no contiene resultados y errores validos.")

    return informe


def normalizar_ruta(ruta: object) -> str:
    # Unifica separadores sin alterar segmentos relativos.
    if not isinstance(ruta, str):
        return ""

    resultado = ruta.replace("\\", "/")
    while resultado.startswith("./"):
        resultado = resultado[2:]
    return resultado


def obtener_linea(resultado: dict[str, Any]) -> int:
    # Extrae la linea inicial del hallazgo.
    inicio = resultado.get("start")
    if not isinstance(inicio, dict):
        return 0

    linea = inicio.get("line")
    return linea if isinstance(linea, int) else 0


def calcular_sha256(raiz: Path, ruta_relativa: str) -> str:
    # Impide salir de la raiz y normaliza finales de linea entre sistemas.
    partes = Path(ruta_relativa).parts
    if not partes or Path(ruta_relativa).is_absolute() or ".." in partes:
        raise ValueError(f"Ruta de hallazgo no permitida: {ruta_relativa}")

    ruta = (raiz / Path(*partes)).resolve(strict=True)
    ruta.relative_to(raiz)
    if ruta.stat().st_size > LONGITUD_MAXIMA_ARCHIVO_EXCEPCION:
        raise ValueError(f"El archivo de excepcion es demasiado grande: {ruta_relativa}")

    datos = ruta.read_bytes()
    datos_normalizados = datos.replace(b"\r\n", b"\n").replace(b"\r", b"\n")
    return hashlib.sha256(datos_normalizados).hexdigest().upper()


def buscar_excepcion(
    regla: str,
    ruta: str,
    linea: int,
) -> ExcepcionSemgrep | None:
    # Exige coincidencia exacta de regla, ruta y linea.
    for excepcion in EXCEPCIONES:
        if (
            excepcion.regla == regla
            and excepcion.ruta == ruta
            and excepcion.linea == linea
        ):
            return excepcion
    return None


def obtener_tipo_y_lineas_error(
    error: dict[str, Any],
) -> tuple[str, tuple[int, ...]] | None:
    # Normaliza los dos formatos de error emitidos por Semgrep.
    tipo = error.get("type")
    if isinstance(tipo, str):
        mensaje = error.get("message")
        if not isinstance(mensaje, str):
            return None

        coincidencia = re.match(r"^Syntax error at line .+:(\d+):\r?\n", mensaje)
        if coincidencia is None:
            return None
        return tipo, (int(coincidencia.group(1)),)

    if (
        not isinstance(tipo, list)
        or len(tipo) != 2
        or not isinstance(tipo[0], str)
        or not isinstance(tipo[1], list)
    ):
        return None

    ruta_error = normalizar_ruta(error.get("path"))
    lineas: list[int] = []
    for ubicacion in tipo[1]:
        if not isinstance(ubicacion, dict):
            return None
        if normalizar_ruta(ubicacion.get("path")) != ruta_error:
            return None

        inicio = ubicacion.get("start")
        if not isinstance(inicio, dict) or not isinstance(inicio.get("line"), int):
            return None
        lineas.append(inicio["line"])

    return tipo[0], tuple(lineas)


def buscar_excepcion_error(
    ruta: str,
    tipo: str,
    lineas: tuple[int, ...],
) -> ExcepcionErrorSemgrep | None:
    # Exige coincidencia exacta de ruta, tipo y lineas omitidas.
    for excepcion in EXCEPCIONES_ERRORES:
        if (
            excepcion.ruta == ruta
            and excepcion.tipo == tipo
            and excepcion.lineas == lineas
        ):
            return excepcion
    return None


def validar_errores(
    informe: dict[str, Any],
    raiz: Path,
) -> tuple[list[str], int, set[str]]:
    # Admite solo limitaciones conocidas del parser con archivo inmutable.
    bloqueantes: list[str] = []
    aceptados = 0
    rutas_aceptadas: set[str] = set()

    for error_bruto in informe["errors"]:
        if not isinstance(error_bruto, dict):
            bloqueantes.append("Error Semgrep con formato no valido.")
            continue

        ruta = normalizar_ruta(error_bruto.get("path"))
        detalle = obtener_tipo_y_lineas_error(error_bruto)
        if (
            error_bruto.get("code") != 3
            or error_bruto.get("level") != "warn"
            or detalle is None
        ):
            bloqueantes.append(f"Error Semgrep no permitido en {ruta or '<sin-ruta>'}.")
            continue

        tipo, lineas = detalle
        excepcion = buscar_excepcion_error(ruta, tipo, lineas)
        if excepcion is None:
            bloqueantes.append(
                f"Error {tipo} no permitido en {ruta or '<sin-ruta>'}:"
                f"{','.join(map(str, lineas)) or 'sin-linea'}."
            )
            continue

        try:
            sha256 = calcular_sha256(raiz, excepcion.ruta)
        except (OSError, ValueError) as error:
            bloqueantes.append(f"No se pudo verificar {excepcion.ruta}: {error}")
            continue

        if sha256 != excepcion.sha256:
            bloqueantes.append(
                f"La huella de {excepcion.ruta} cambio: {sha256}. "
                "La excepcion de parser requiere una revision nueva."
            )
            continue

        aceptados += 1
        rutas_aceptadas.add(ruta)
        print(
            f"[ERROR DE PARSER VERIFICADO] {tipo} en {ruta}:"
            f"{','.join(map(str, lineas))}. Motivo: {excepcion.motivo}"
        )

    return bloqueantes, aceptados, rutas_aceptadas


def validar_omisiones(
    informe: dict[str, Any],
    rutas_errores_aceptadas: set[str],
) -> list[str]:
    # Rechaza cualquier archivo omitido que no corresponda a un error aceptado.
    rutas = informe.get("paths")
    if not isinstance(rutas, dict):
        return ["El informe Semgrep no contiene informacion de rutas."]

    omisiones = rutas.get("skipped")
    if not isinstance(omisiones, list):
        return ["El informe Semgrep no contiene una lista de omisiones valida."]

    bloqueantes: list[str] = []
    for omision in omisiones:
        if not isinstance(omision, dict):
            bloqueantes.append("Omision Semgrep con formato no valido.")
            continue

        ruta = normalizar_ruta(omision.get("path"))
        if (
            omision.get("reason") != "analysis_failed_parser_or_internal_error"
            or ruta not in rutas_errores_aceptadas
        ):
            bloqueantes.append(
                f"Archivo omitido sin excepcion valida: {ruta or '<sin-ruta>'}."
            )

    if rutas_errores_aceptadas != {
        normalizar_ruta(omision.get("path"))
        for omision in omisiones
        if isinstance(omision, dict)
    }:
        bloqueantes.append("Las omisiones no coinciden con los errores de parser aceptados.")

    return bloqueantes


def validar_resultados(
    informe: dict[str, Any],
    raiz: Path,
    codigo_semgrep: int,
) -> int:
    # Valida hallazgos, errores, omisiones y codigo de salida del motor.
    bloqueantes: list[str] = []
    hallazgos_aceptados = 0

    for resultado_bruto in informe["results"]:
        if not isinstance(resultado_bruto, dict):
            bloqueantes.append("Resultado Semgrep con formato no valido.")
            continue

        regla_obj = resultado_bruto.get("check_id")
        regla = regla_obj if isinstance(regla_obj, str) else ""
        ruta = normalizar_ruta(resultado_bruto.get("path"))
        linea = obtener_linea(resultado_bruto)
        excepcion = buscar_excepcion(regla, ruta, linea)

        if excepcion is None:
            bloqueantes.append(f"{regla or '<sin-regla>'} en {ruta or '<sin-ruta>'}:{linea}")
            continue

        try:
            sha256 = calcular_sha256(raiz, excepcion.ruta)
        except (OSError, ValueError) as error:
            bloqueantes.append(f"No se pudo verificar {excepcion.ruta}: {error}")
            continue

        if sha256 != excepcion.sha256:
            bloqueantes.append(
                f"La huella de {excepcion.ruta} cambio: {sha256}. "
                "La excepcion requiere una revision nueva."
            )
            continue

        hallazgos_aceptados += 1
        print(
            f"[EXCEPCION VERIFICADA] {regla} en {ruta}:{linea}. "
            f"Motivo: {excepcion.motivo}"
        )

    errores_bloqueantes, errores_aceptados, rutas_errores_aceptadas = validar_errores(
        informe,
        raiz,
    )
    bloqueantes.extend(errores_bloqueantes)
    bloqueantes.extend(validar_omisiones(informe, rutas_errores_aceptadas))

    codigos_esperados = {0}
    if informe["errors"]:
        codigos_esperados = {3}
    if informe["results"]:
        codigos_esperados.add(1)
    if codigo_semgrep not in codigos_esperados:
        bloqueantes.append(
            "Semgrep termino con codigo "
            f"{codigo_semgrep}; se esperaba uno de {sorted(codigos_esperados)}."
        )

    if bloqueantes:
        print(f"Semgrep detecto {len(bloqueantes)} problema(s) bloqueante(s):")
        for problema in bloqueantes:
            print(f"  - {problema}")
        return 1

    print(
        "Validacion Semgrep correcta: "
        f"{hallazgos_aceptados} hallazgo(s) y {errores_aceptados} error(es) "
        "de parser verificados; ningun problema nuevo."
    )
    return 0


def main(argumentos: list[str]) -> int:
    # Valida los argumentos y resuelve la raiz desde este archivo.
    if len(argumentos) != 3:
        print(
            "Uso: ValidarResultadosSemgrep.py <informe-json> <codigo-semgrep>",
            file=sys.stderr,
        )
        return 2

    raiz = Path(__file__).resolve().parent.parent
    try:
        codigo_semgrep = int(argumentos[2])
        informe = cargar_informe(Path(argumentos[1]), raiz)
        return validar_resultados(informe, raiz, codigo_semgrep)
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"No se pudo validar el informe Semgrep: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
