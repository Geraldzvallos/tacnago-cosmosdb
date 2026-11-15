"""Carga los lugares de data/lugares.json en Azure Cosmos DB for NoSQL.

Usa upsert (insertar o actualizar), así que se puede ejecutar varias veces sin duplicar datos.
Variables de entorno: COSMOS_ENDPOINT, COSMOS_KEY, COSMOS_DATABASE (TacnaGo), COSMOS_CONTAINER (Lugares).
"""
import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path

from azure.cosmos import CosmosClient

endpoint = os.environ["COSMOS_ENDPOINT"]
key = os.environ["COSMOS_KEY"]
database = os.environ.get("COSMOS_DATABASE", "TacnaGo")
container_name = os.environ.get("COSMOS_CONTAINER", "Lugares")

archivo = Path(sys.argv[1] if len(sys.argv) > 1 else Path(__file__).with_name("lugares.json"))
lugares = json.loads(archivo.read_text(encoding="utf-8"))

client = CosmosClient(endpoint, credential=key)
container = client.get_database_client(database).get_container_client(container_name)


def ru() -> float:
    """Costo en RU de la última operación (encabezado x-ms-request-charge)."""
    return float(container.client_connection.last_response_headers.get("x-ms-request-charge", 0))


total_ru = 0.0
ahora = datetime.now(timezone.utc).isoformat()
for lugar in lugares:
    lugar.setdefault("creadoEn", ahora)
    container.upsert_item(lugar)
    total_ru += ru()
    print(f"  upsert  {lugar['categoria']:<12} {lugar['id']:<28} {ru():6.2f} RU")

print(f"\n{len(lugares)} documentos cargados ({total_ru:.2f} RU en total)\n")

# Verificación: cantidad de documentos por partición (consulta dentro de cada partición)
for categoria in sorted({l["categoria"] for l in lugares}):
    cantidad = list(container.query_items(
        "SELECT VALUE COUNT(1) FROM c", partition_key=categoria))[0]
    print(f"  partición {categoria:<12} -> {cantidad} documentos")

# Lectura puntual (id + clave de partición): la operación más barata de Cosmos DB
doc = container.read_item("museo-ferroviario", partition_key="museos")
print(f"\nLectura puntual: {doc['nombre']} ({ru():.2f} RU)")
