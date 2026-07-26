# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Cos'è questo progetto

`OsmExtractor` è un tool console .NET 8 che estrae Point of Interest (POI) da estratti planetari OpenStreetMap in formato `.osm.pbf` (scaricati da Geofabrik) e li converte in file CSV, uno per ogni categoria di tag OSM definita in `OsmExtractor/CategoriePOI.txt`.

Il flusso completo è: `download_continenti.bat` scarica gli `.osm.pbf` per continente in `osm_data/continents/`, poi `OsmExtractor` (vedi `estrattore.txt` per i comandi di riferimento) processa ciascun file e scrive i risultati in `osm_data/csv/`.

## Comandi

Da dentro `OsmExtractor/`:

```bash
# Ripristino pacchetti / build
dotnet restore
dotnet build

# Esecuzione: richiede il path a un file .osm.pbf come argomento
dotnet run <path_al_file.osm.pbf>

# Esempio reale (dati in osm_data/continents relativo alla root del repo)
dotnet run ../osm_data/continents/europe-260721.osm.pbf

# Elaborazione in loop di tutti i continenti
for f in ../osm_data/continents/*.osm.pbf; do
    dotnet run "$f"
done
```

Non esiste una suite di test in questo repo.

## Architettura

Tutta la logica sta in `OsmExtractor/Program.cs` (single-file, ~215 righe), organizzata in un'unica pipeline sequenziale dentro `Main`:

1. **Caricamento categorie**: legge `OsmExtractor/CategoriePOI.txt` (percorso relativo alla working directory di esecuzione, non al file .pbf) e popola `TargetTags`, un `HashSet<string>` di coppie `chiave=valore` (es. `amenity=pharmacy`). Righe vuote o che iniziano con `#` sono ignorate.
2. **Determinazione output**: la cartella CSV di destinazione viene calcolata risalendo di due livelli dalla directory del file `.pbf` in input e aggiungendo `csv/` (quindi per `osm_data/continents/xxx.osm.pbf` l'output finisce in `osm_data/csv/`). Attenzione: se si passa un path con una struttura di directory diversa, l'output finisce altrove.
3. **Un CsvWriter per categoria**: per ogni tag in `TargetTags` viene aperto un file CSV separato (nome derivato sostituendo `=` con `_`, es. `amenity_pharmacy.csv`) in modalità append; l'header viene scritto solo se il file non esiste o è vuoto. Questo permette di rilanciare l'estrazione su continenti diversi accumulando i risultati nelle stesse categorie.
4. **Streaming del PBF**: usa `OsmSharp.Streams.PBFOsmStreamSource` per iterare gli elementi senza caricare l'intero file in memoria (i file di input sono fino a ~32 GB). Per ogni elemento cerca il *primo* tag che matcha una categoria in `TargetTags` e scarta l'elemento se non c'è match o se non è un `Node` (way/relation non sono gestiti, quindi POI espressi come poligoni/aree non vengono estratti).
5. **Scrittura riga**: id, lat/lon (6 decimali), `name` (se presente), e il resto dei tag dell'elemento (esclusa la tag che ha fatto match) serializzati come stringa `chiave=valore` separata da `;`.
6. **Progress reporting**: stampa a video percentuale di avanzamento (basata su `bytesRead / fileSize`, non sul numero di elementi), throughput in MB/s ed ETA stimata.

### Punti da tenere a mente quando si modifica il codice

- `CategoriePOI.txt` deve essere presente nella working directory da cui si lancia `dotnet run` (viene copiato accanto al progetto, non referenziato dalla root del repo).
- Aggiungere nuove categorie POI si fa semplicemente aggiungendo righe `chiave=valore` a `CategoriePOI.txt`; il codice non richiede modifiche.
- Il matching è "primo tag che corrisponde vince": un nodo con più tag rilevanti finisce solo nel CSV della prima categoria trovata iterando `element.Tags`, non in tutte le categorie applicabili.
- Way e relation vengono scartati esplicitamente (`element is Node` altrimenti `continue`): estendere il supporto ad aree richiederebbe calcolare un centroide/geometria, non presente oggi.
