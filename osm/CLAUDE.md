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
4. **Streaming del PBF**: usa `OsmSharp.Streams.PBFOsmStreamSource` per iterare gli elementi senza caricare l'intero file in memoria (i file di input sono fino a ~32 GB). Per ogni elemento cerca il *primo* tag che matcha una categoria in `TargetTags` (`TryFindMatchingTag`, condivisa tra `Node` e `Way`) e scarta l'elemento se non c'è match; i `Node` vengono scritti subito nel CSV, le `Way` vengono raccolte per una risoluzione successiva (punto 7), le `Relation` restano scartate.
5. **Scrittura riga**: id, lat/lon (6 decimali), `name` (se presente), e il resto dei tag dell'elemento (esclusa la tag che ha fatto match) serializzati come stringa `chiave=valore` separata da `;`.
6. **Progress reporting**: stampa a video percentuale di avanzamento (basata su `bytesRead / fileSize`, non sul numero di elementi), throughput in MB/s ed ETA stimata.
7. **Way (poligoni) — `matchedWays`/`neededNodeIds` raccolti nello STESSO loop principale, risolti da `ResolveWayCentroids` dopo**: molti POI reali (grandi edifici/monumenti, aeroporti, aree) sono mappati in OSM come `way` (il contorno di un poligono), non come singolo `Node` — scartarli del tutto (comportamento originale, prima di questa modifica) li rendeva invisibili nel database offline indipendentemente da area/tag, non un problema di copertura geografica. Un `way` referenzia però solo ID di nodi che lo compongono, senza le loro coordinate — e i `Node` a cui si riferisce si trovano in un blocco del PBF che *precede* sempre quello delle `Way` (convenzione Geofabrik/osmium: Node, poi Way, poi Relation), quindi sono già stati superati dallo streaming quando si incontra una `Way` matchata. Non si tengono in cache le coordinate di TUTTI i Node "per sicurezza" (per un continente intero sarebbero miliardi, troppa RAM): si accumulano invece, nello stesso loop principale che già scrive i Node, solo id/nome/tag/lista-id-nodi delle Way che matchano una categoria (in `matchedWays`) e l'unione di tutti gli id-nodo richiesti (in `neededNodeIds`, un unico `HashSet<long>`) — **una sola passata aggiuntiva** sul file basta quindi a chiudere il cerchio:
   - `ResolveWayCentroids` rilegge il file (`element is Node`, con uscita anticipata appena si esce dalla sezione Node) e per ogni nodo il cui id è in `neededNodeIds` salva lat/lon in un `Dictionary<long,(float,float)>` — mai l'intero file di Node, solo quelli effettivamente referenziati da una Way di categoria (tipicamente una frazione minuscola del continente).
   - Il centroide finale è il **centro del bounding box** dei nodi risolti per quella way (stessa convenzione di Overpass `out center`, per restare coerenti con quel che la ricerca live mostra per lo stesso elemento). Una way con zero nodi risolvibili (tipico ai bordi di un estratto continentale, dove un nodo del perimetro può ricadere nel continente adiacente) viene scartata; un bbox parziale (almeno un nodo risolto) viene comunque scritto.
   - Relation ancora non gestite (geometria multipolygon con ruoli "outer"/"inner", assemblaggio via `CompleteRelation` più complesso da fare correttamente): stesso limite di prima, ora ristretto alle sole relation invece che a way+relation.
   - Costo: **due letture totali del file invece di una sola** (quella principale, che include già l'individuazione delle Way, più l'unica passata di `ResolveWayCentroids` per le coordinate — non tre: una prima versione faceva un'ulteriore passata dedicata solo a trovare le Way, ridondante dato che il loop principale le attraversa comunque). Verificato su un caso reale: la Torre della Campana di Xi'an (`historic=monument`, way OSM `254488435`) era assente dal CSV offline per questo solo motivo — non un problema di zoom/area come inizialmente sospettato — ed è stata trovata subito dopo aver aggiunto questo supporto.

### Punti da tenere a mente quando si modifica il codice

- `CategoriePOI.txt` deve essere presente nella working directory da cui si lancia `dotnet run` (viene copiato accanto al progetto, non referenziato dalla root del repo).
- Aggiungere nuove categorie POI si fa semplicemente aggiungendo righe `chiave=valore` a `CategoriePOI.txt`; il codice non richiede modifiche.
- Il matching è "primo tag che corrisponde vince": un nodo (o way) con più tag rilevanti finisce solo nel CSV della prima categoria trovata iterando i suoi tag, non in tutte le categorie applicabili.
- Le way estratte finiscono negli stessi CSV per categoria dei node (stesso formato riga id,lat,lon,name,tags) — chi legge il CSV (`Services/PoiOfflineDatabase.cs`) non distingue la provenienza, non serve alcuna modifica lato app.
