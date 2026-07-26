#!/bin/bash
# Esegue l'estrazione per tutti i continenti, dal più piccolo al più grande
# (così i risultati/eventuali errori si vedono presto senza aspettare Europa).
set -e
cd "$(dirname "$0")"

FILES=(
  ../osm_data/continents/antarctica-260721.osm.pbf
  ../osm_data/continents/central-america-260721.osm.pbf
  ../osm_data/continents/australia-oceania-260721.osm.pbf
  ../osm_data/continents/south-america-260721.osm.pbf
  ../osm_data/continents/africa-260721.osm.pbf
  ../osm_data/continents/asia-260721.osm.pbf
  ../osm_data/continents/north-america-260721.osm.pbf
  ../osm_data/continents/europe-260721.osm.pbf
)

for f in "${FILES[@]}"; do
  echo "=============================================="
  echo "=== $(date '+%Y-%m-%d %H:%M:%S') INIZIO: $f"
  echo "=============================================="
  dotnet bin/Debug/net8.0/OsmExtractor.dll "$f"
  echo "=== $(date '+%Y-%m-%d %H:%M:%S') FINE: $f"
  echo
done

echo "=============================================="
echo "TUTTI I CONTINENTI COMPLETATI: $(date '+%Y-%m-%d %H:%M:%S')"
echo "=============================================="
