"""Respaldo lógico de un contenedor de Azure Cosmos DB for NoSQL.

Exporta todos los documentos a un archivo JSON comprimido (.json.gz) con metadatos:
cantidad de documentos, clave de partición y un hash SHA-256 para verificar la integridad.
Variables de entorno: COSMOS_ENDPOINT, COSMOS_KEY, COSMOS_DATABASE, COSMOS_CONTAINER.
Uso: python backup/backup.py <carpeta_de_salida>
"""
import gzip
import hashlib
import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path

from azure.cosmos import CosmosClient

database = os.environ.get("COSMOS_DATABASE", "TacnaGo")
container_name = os.environ.get("COSMOS_CONTAINER", "Lugares")
salida = Path(sys.argv[1] if len(sys.argv) > 1 else "respaldo")
salida.mkdir(parents=True, exist_ok=True)

client = CosmosClient(os.environ["COSMOS_ENDPOINT"], credential=os.environ["COSMOS_KEY"])
container = client.get_database_client(database).get_container_client(container_name)
props = container.read()
clave_particion = props["partitionKey"]["paths"][0]

# Lectura de todos los documentos (consulta entre particiones)
items = list(container.query_items("SELECT * FROM c", enable_cross_partition_query=True))
ru = float(container.client_connection.last_response_headers.get("x-ms-request-charge", 0))

# Se quitan las propiedades del sistema (_rid, _etag, _ts...): Cosmos DB las vuelve a generar al restaurar
items = [{k: v for k, v in it.items() if not k.startswith("_")} for it in items]
items.sort(key=lambda d: (str(d.get(clave_particion.strip("/"), "")), d["id"]))

canonico = json.dumps(items, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
sha256 = hashlib.sha256(canonico.encode("utf-8")).hexdigest()
ahora = datetime.now(timezone.utc)

respaldo = {
    "formato": "tacnago-backup/1",
    "baseDeDatos": database,
    "contenedor": container_name,
    "claveParticion": clave_particion,
    "creadoEn": ahora.isoformat(),
    "documentos": len(items),
    "sha256": sha256,
    "items": items,
}

nombre = f"tacnago-{container_name.lower()}-{ahora:%Y%m%dT%H%M%SZ}.json.gz"
archivo = salida / nombre
with gzip.open(archivo, "wt", encoding="utf-8") as f:
    json.dump(respaldo, f, ensure_ascii=False, indent=2)

particiones = {}
for it in items:
    particiones[it.get(clave_particion.strip("/"))] = particiones.get(it.get(clave_particion.strip("/")), 0) + 1

print(f"Contenedor:        {database}/{container_name} (clave de partición {clave_particion})")
print(f"Documentos:        {len(items)}")
for p, n in sorted(particiones.items()):
    print(f"  partición {p:<12} {n}")
print(f"SHA-256:           {sha256}")
print(f"Archivo:           {archivo} ({archivo.stat().st_size} bytes)")
print(f"Costo de lectura:  {ru:.2f} RU (última página)")

# Datos para los siguientes pasos del workflow
if os.environ.get("GITHUB_OUTPUT"):
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as out:
        out.write(f"archivo={archivo}\nnombre={nombre}\ndocumentos={len(items)}\nsha256={sha256}\n")
