"""Restaura un respaldo lógico (.json.gz) en un contenedor de Azure Cosmos DB for NoSQL y lo verifica.

1. Comprueba la integridad del archivo (SHA-256).
2. Si el destino es un contenedor de prueba, lo vacía antes de restaurar.
3. Inserta los documentos con upsert (respeta la clave de partición de cada uno).
4. Verifica que la cantidad de documentos y cada id coincidan con el respaldo.
Variables de entorno: COSMOS_ENDPOINT, COSMOS_KEY, COSMOS_DATABASE.
Uso: python backup/restore.py <archivo.json.gz> <contenedor_destino>
"""
import gzip
import hashlib
import json
import os
import sys
from datetime import datetime, timezone

from azure.cosmos import CosmosClient

archivo, destino = sys.argv[1], sys.argv[2]
database = os.environ.get("COSMOS_DATABASE", "TacnaGo")

with gzip.open(archivo, "rt", encoding="utf-8") as f:
    respaldo = json.load(f)
items = respaldo["items"]
campo_pk = respaldo["claveParticion"].strip("/")

# 1. Integridad del archivo
canonico = json.dumps(items, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
sha256 = hashlib.sha256(canonico.encode("utf-8")).hexdigest()
if sha256 != respaldo["sha256"] or len(items) != respaldo["documentos"]:
    sys.exit("ERROR: el respaldo está dañado (el SHA-256 o la cantidad no coinciden).")
creado = datetime.fromisoformat(respaldo["creadoEn"])
antiguedad = datetime.now(timezone.utc) - creado
print(f"Respaldo:     {os.path.basename(archivo)}")
print(f"Creado:       {respaldo['creadoEn']} (antigüedad {antiguedad})")
print(f"Documentos:   {len(items)}  |  SHA-256 verificado")
print(f"Destino:      {database}/{destino}")

client = CosmosClient(os.environ["COSMOS_ENDPOINT"], credential=os.environ["COSMOS_KEY"])
container = client.get_database_client(database).get_container_client(destino)
if container.read()["partitionKey"]["paths"][0] != respaldo["claveParticion"]:
    sys.exit("ERROR: el contenedor destino tiene otra clave de partición.")

# 2. Un contenedor de prueba se vacía para que la verificación sea exacta
if destino != respaldo["contenedor"]:
    previos = list(container.query_items(f"SELECT c.id, c.{campo_pk} AS pk FROM c", enable_cross_partition_query=True))
    for d in previos:
        container.delete_item(d["id"], partition_key=d["pk"])
    print(f"Contenedor de prueba vaciado ({len(previos)} documentos anteriores)")

# 3. Restauración
inicio = datetime.now(timezone.utc)
for it in items:
    container.upsert_item(it)
duracion = (datetime.now(timezone.utc) - inicio).total_seconds()
print(f"Restaurados:  {len(items)} documentos en {duracion:.1f} s")

# 4. Verificación: cantidad total y lectura puntual de cada documento
total = list(container.query_items("SELECT VALUE COUNT(1) FROM c", enable_cross_partition_query=True))[0]
faltantes = []
for it in items:
    try:
        container.read_item(it["id"], partition_key=it[campo_pk])
    except Exception:
        faltantes.append(it["id"])

esperado = len(items) if destino != respaldo["contenedor"] else total
print(f"Verificación: {total} documentos en el destino (esperados {esperado}), faltantes: {len(faltantes)}")
if faltantes or total != esperado:
    sys.exit("ERROR: la restauración no coincide con el respaldo.")
print("RESTAURACIÓN VERIFICADA CORRECTAMENTE")

if os.environ.get("GITHUB_STEP_SUMMARY"):
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as s:
        s.write("## Restauración verificada\n\n| Dato | Valor |\n|---|---|\n")
        s.write(f"| Respaldo | `{os.path.basename(archivo)}` |\n| Creado | {respaldo['creadoEn']} |\n")
        s.write(f"| Documentos restaurados | {len(items)} |\n| Destino | `{database}/{destino}` |\n")
        s.write(f"| Duración | {duracion:.1f} s |\n| SHA-256 | `{sha256[:16]}…` |\n")
