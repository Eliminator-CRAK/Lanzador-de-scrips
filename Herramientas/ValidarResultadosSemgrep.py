# (Autor: Alex Roman)
# Descripcion: Valida hallazgos Semgrep y limita las excepciones a artefactos revisados.

from __future__ import annotations

import hashlib
import json
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


# Esta excepcion corresponde a codigo de Framer Motion que ya usa Map y Set.
EXCEPCIONES = (
    ExcepcionSemgrep(
        regla=(
            "javascript.lang.security.audit.prototype-pollution."
            "prototype-pollution-loop.prototype-pollution-loop"
        ),
        ruta="ClienteWeb/assets/index-DgdNDMM1.js",
        linea=127,
        sha256="01AE1D96E4F7B99ED159E83781689A90AA67F07B5436E21BE7D096D554B69EE8",
        motivo=(
            "Falso positivo de baja confianza en AnimatePresence de Framer Motion; "
            "el codigo detectado usa Map y Set."
        ),
    ),
)

LONGITUD_MAXIMA_INFORME = 50 * 1024 * 1024


def cargar_informe(ruta_informe: Path, raiz: Path) -> dict[str, Any]:
    # Limita la lectura al informe generado dentro del repositorio.
    ruta_segura = ruta_informe.resolve(strict=True)
    ruta_segura.relative_to(raiz)
    if ruta_segura.stat().st_size > LONGITUD_MAXIMA_INFORME:
        raise ValueError("El informe Semgrep supera el tamano maximo permitido.")

    with ruta_segura.open("r", encoding="utf-8") as flujo:
        informe = json.load(flujo)

    if not isinstance(informe, dict) or not isinstance(informe.get("results"), list):
        raise ValueError("El informe Semgrep no contiene una lista de resultados valida.")

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
    # Impide que una ruta del informe salga de la raiz del repositorio.
    partes = Path(ruta_relativa).parts
    if not partes or Path(ruta_relativa).is_absolute() or ".." in partes:
        raise ValueError(f"Ruta de hallazgo no permitida: {ruta_relativa}")

    ruta = (raiz / Path(*partes)).resolve(strict=True)
    ruta.relative_to(raiz)

    resumen = hashlib.sha256()
    with ruta.open("rb") as flujo:
        for bloque in iter(lambda: flujo.read(1024 * 1024), b""):
            resumen.update(bloque)
    return resumen.hexdigest().upper()


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


def validar_resultados(informe: dict[str, Any], raiz: Path) -> int:
    # Semgrep --strict controla los errores del motor antes de este filtro.
    bloqueantes: list[str] = []
    aceptados = 0

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

        aceptados += 1
        print(
            f"[EXCEPCION VERIFICADA] {regla} en {ruta}:{linea}. "
            f"Motivo: {excepcion.motivo}"
        )

    if bloqueantes:
        print(f"Semgrep detecto {len(bloqueantes)} hallazgo(s) bloqueante(s):")
        for hallazgo in bloqueantes:
            print(f"  - {hallazgo}")
        return 1

    print(
        "Validacion Semgrep correcta: "
        f"{aceptados} excepcion(es) verificadas y ningun hallazgo nuevo."
    )
    return 0


def main(argumentos: list[str]) -> int:
    # Valida los argumentos y resuelve la raiz desde este archivo.
    if len(argumentos) != 2:
        print("Uso: ValidarResultadosSemgrep.py <informe-json>", file=sys.stderr)
        return 2

    raiz = Path(__file__).resolve().parent.parent
    try:
        informe = cargar_informe(Path(argumentos[1]), raiz)
        return validar_resultados(informe, raiz)
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"No se pudo validar el informe Semgrep: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
