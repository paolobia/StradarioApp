# Come creare file compatibili con StradarioApp

Guida alle regole dei formati che StradarioApp legge e scrive: il progetto
nativo `.stradario` e i file di scambio KML/KMZ, GPX e CSV per **POI** e
**percorsi**. Pensata sia per chi scrive i file a mano sia per script o AI che
li generano.

Fonte di verità: `Models/StradarioModels.cs`, `Services/PoiService.cs`,
`Services/PercorsoService.cs`, `Services/KmlIo.cs`, `Services/ProjectService.cs`.

---

## 1. Concetti base

| Entità | Cos'è | Contenitore |
|---|---|---|
| **Pagina** (`MapPage`) | Rettangolo geografico che diventa una pagina del PDF | lista piatta `Pages` |
| **Gruppo POI** (`PoiGroup`) | Cartella con nome e **colore** comuni | lista `PoiGroups` |
| **POI** (`PoiItem`) | Singolo punto con etichetta, descrizione, **icona propria**, date opzionali | dentro un gruppo |
| **Percorso** (`Percorso`) | Sequenza ordinata di punti (traccia/itinerario) con colore proprio | lista piatta `Percorsi` |
| **Punto-POI di percorso** (`GeoPoint.IsPoi`) | Vertice di un percorso marcato come POI (tappa), col **colore del percorso** | dentro un percorso |

Regole valide per tutti i formati:

- **Coordinate WGS84, ordine `lon,lat`** in KML (e JSON: campi `Lon`/`Lat`
  separati). In GPX e CSV sono attributi/colonne `lat` e `lon` distinti.
- Separatore decimale **sempre il punto** (`11.2558`), mai la virgola.
- Un percorso deve avere **almeno 2 punti**, altrimenti viene scartato
  all'import.
- Gli `Id` interni vengono **rinumerati** all'import da KML/GPX/CSV: non serve
  (e non conta) sceglierli.
- Tutto ciò che viene importato da KMZ/KML/GPX è **bloccato** (🔒) per evitare
  spostamenti accidentali; si sblocca dall'albero di navigazione.

---

## 2. Progetto nativo `.stradario` (JSON)

File JSON (Newtonsoft, nomi in PascalCase, UTF-8). Gli **enum sono interi**,
non stringhe. Esempio completo reale: `demo-full.stradario` nella root del repo.

### 2.1 Struttura radice

```json
{
  "Settings":      { ... },
  "Pages":         [ ... ],
  "PoiGroups":     [ ... ],
  "Percorsi":      [ ... ],
  "LastModified":  "2026-10-27T10:00:00",
  "ViewCenterLon": 12.4964,
  "ViewCenterLat": 41.9028,
  "ViewZoom":      10.0
}
```

Tutte le chiavi sono facoltative (mancante = valore di default). Chiavi
sconosciute (es. `ProjectName`) sono ignorate.

### 2.2 `Settings`

| Campo | Tipo | Default | Valori |
|---|---|---|---|
| `PageSize` | int | 1 | `0`=A5, `1`=A4, `2`=A3 |
| `Orientation` | int | 0 | `0`=Verticale, `1`=Orizzontale |
| `Dpi` | int | 150 | risoluzione di stampa |
| `Scale` | int | 3 | vedi tabella sotto |
| `TileServerUrl` | string | OSM Standard | template con `{z}/{x}/{y}` |
| `AutoLockSeconds` | int | 60 | `0` = blocco automatico disattivato |
| `PdfContrastMode` | int | 0 | `0`=Nessuno, `1`=Colore, `2`=B/N, `3`=Enfatizza strade, `4`=Contrasto adattivo |
| `PdfEdgeReinforcement` | bool | false | rinforza i contorni nel PDF |
| `PdfDitherBlackWhite` | bool | false | retinatura (solo con `PdfContrastMode`=2) |

Le chiavi API (tile server, Groq) **non** si scrivono nel file: sono
preferenze globali dell'app.

**`Scale` (indice = valore persistito, mai riordinare):**

| 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 |
|---|---|---|---|---|---|---|---|
| 1:1.000 | 1:5.000 | 1:10.000 | 1:100.000 | 1:200.000 | 1:15.000 | 1:20.000 | 1:25.000 |

| 8 | 9 | 10 | 11 | 12 | 13 | 14 | 15 |
|---|---|---|---|---|---|---|---|
| 1:50.000 | 1:150.000 | 1:250.000 | 1:300.000 | 1:400.000 | 1:500.000 | 1:800.000 | 1:1.000.000 |

### 2.3 `PoiGroups` e `Items`

```json
{
  "Id": 1,
  "Name": "Musei",
  "Description": "",
  "ColorHex": "#FF6B9D",
  "IsLocked": false,
  "Items": [
    {
      "Id": 1,
      "Label": "Uffizi",
      "Description": "Galleria dell'arte rinascimentale",
      "Lon": 11.2625,
      "Lat": 43.7625,
      "Icon": 5,
      "DateStart": "2026-08-10T09:30:00",
      "DateEnd": null
    }
  ]
}
```

- `Id` di gruppo: univoco fra i gruppi. `Id` di POI: univoco **dentro il
  gruppo**. Partire da 1 e incrementare.
- `ColorHex`: `#RRGGBB` (es. `#1E88E5`, default). È il colore di **tutti** i
  POI del gruppo.
- `Icon` sta **sul singolo POI**. Se manca (`null`), all'apertura eredita
  `Icon` del gruppo (retrocompatibilità coi vecchi file); in pratica scrivila
  sempre sul POI.
- `DateStart`/`DateEnd`: opzionali (`null` o assenti = nessuna data). Con solo
  `DateStart` è un istante; con entrambe un intervallo Da/A. Formato ISO 8601
  senza fuso: `yyyy-MM-ddTHH:mm:ss`.
- `Description` supporta **Markdown** (vedi §7).

**Icone (`PoiIconType`):**

| 0 Pin | 1 Star | 2 Flag | 3 Home | 4 Church | 5 Monument | 6 Restaurant | 7 Cafe |
|---|---|---|---|---|---|---|---|
| **8 Shop** | **9 Parking** | **10 Hospital** | **11 Hotel** | **12 Viewpoint** | **13 Camping** | **14 Fountain** | **15 Info** |

Il set è chiuso: non esistono icone personalizzate.

### 2.4 `Percorsi` e `Points`

```json
{
  "Id": 1,
  "Label": "Giro centro storico",
  "Description": "Percorso a piedi nel centro di Firenze",
  "ColorHex": "#E53935",
  "IsLocked": false,
  "StartDateTime": "2026-08-10T09:00:00",
  "EndDateTime": "2026-08-10T18:00:00",
  "Points": [
    { "Lon": 11.2558, "Lat": 43.7731,
      "IsPoi": true, "PoiLabel": "Duomo", "PoiDescription": "Cattedrale", "PoiIcon": 4 },
    { "Lon": 11.2520, "Lat": 43.7710 },
    { "Lon": 11.2528, "Lat": 43.7654 }
  ]
}
```

- **L'ordine dei `Points` è l'ordine del percorso.**
- Un punto semplice ha solo `Lon`/`Lat`. Per una **tappa** aggiungere
  `"IsPoi": true` + `PoiLabel` (+ `PoiDescription`, `PoiIcon` opzionali).
  Il colore della tappa è sempre quello del percorso.
- Le date stanno **solo sugli estremi del percorso** (`StartDateTime`,
  `EndDateTime`), mai sui punti intermedi.
- `ColorHex` default `#E53935`.

### 2.5 `Pages` (pagine del PDF)

```json
{ "Id": 1, "Label": "A1",
  "GeoBounds": { "MinLon": 11.15, "MinLat": 43.72, "MaxLon": 11.35, "MaxLat": 43.82 },
  "PageNumber": 1, "Description": "", "IsLocked": false,
  "OrientationOverride": null, "ScaleOverride": null, "ManualOrder": null }
```

- `GeoBounds` **non viene ricalcolato** al caricamento: deve essere coerente
  con formato e scala. Copre solo l'area stampata *dentro* i bordi
  (foglio meno 2×15 mm). Esempio: A4 verticale a 1:100.000 → 18 km × 26,7 km.
- `OrientationOverride`/`ScaleOverride` (stessi interi di `Settings`)
  sostituiscono il valore globale solo per quella pagina; `null` = usa il
  globale.
- **Consiglio:** se generi il file a mano o via script, lascia `"Pages": []`
  e posiziona le pagine dentro l'app (tasto destro sulla mappa). Il PDF si
  genera anche con zero pagine (gazetteer e panoramica restano).

---

## 3. KML / KMZ

- `.kml` = XML (namespace `http://www.opengis.net/kml/2.2`). `.kmz` = zip con
  `doc.kml` dentro (icone PNG opzionali in `files/`). L'import accetta anche
  file con BOM e codifiche diverse da UTF-8.
- L'import è **unico**: lo stesso file può contenere POI, percorsi o
  entrambi; ogni parser ignora le forme che non capisce.

### 3.1 POI

Una `<Folder>` = un **gruppo**; un `<Placemark>` con `<Point>` = un **POI**.

```xml
<?xml version="1.0" encoding="UTF-8"?>
<kml xmlns="http://www.opengis.net/kml/2.2">
  <Document>
    <Folder>
      <name>Musei</name>
      <description>Gruppo di musei</description>
      <Placemark>
        <name>Uffizi</name>
        <description>Galleria **rinascimentale**</description>
        <Point><coordinates>11.2625,43.7625,0</coordinates></Point>
        <ExtendedData>
          <Data name="stradarioDateStart"><value>2026-08-10T09:30:00</value></Data>
        </ExtendedData>
      </Placemark>
    </Folder>
  </Document>
</kml>
```

Cosa viene letto e cosa no:

| Elemento | Comportamento |
|---|---|
| `<Folder><name>` | nome del gruppo; se manca → nome del **file** |
| `<Placemark>` fuori da ogni Folder | gruppo con il nome del file |
| `<Placemark><name>` / `<description>` | etichetta / descrizione (Markdown) |
| `<coordinates>` | `lon,lat[,quota]` |
| **Icona** | **mai letta** da `<styleUrl>`: ogni POI importato parte come *Segnaposto*; si cambia nell'app |
| **Colore** | **non letto** per i POI: l'app assegna colori distinti dalla sua palette |
| `ExtendedData` `stradarioDateStart` / `stradarioDateEnd` | date Da/A (§6) |

Quando l'app **esporta** in KMZ, scrive le icone PNG (`files/icon_<gruppo>_<icona>.png`)
e uno `<Style>` per ogni coppia icona/colore; in `.kml` grezzo usa solo il
colore (`IconStyle/color`).

### 3.2 Percorsi

Un `<Placemark>` con `<LineString>` (dentro o fuori da una Folder) = un
**percorso**.

```xml
<Style id="style_1">
  <LineStyle><color>ff3935e5</color><width>3</width></LineStyle>
</Style>
<Placemark>
  <name>Giro centro storico</name>
  <description>A piedi</description>
  <styleUrl>#style_1</styleUrl>
  <LineString>
    <tessellate>1</tessellate>
    <coordinates>
      11.2558,43.7731,0 11.2520,43.7710,0 11.2528,43.7654,0
    </coordinates>
  </LineString>
  <ExtendedData>
    <Data name="stradarioDateStart"><value>2026-08-10T09:00:00</value></Data>
    <Data name="stradarioDateEnd"><value>2026-08-10T18:00:00</value></Data>
  </ExtendedData>
</Placemark>
```

- Le coordinate sono tuple `lon,lat,quota` separate da **spazio o a capo**.
- **Colore:** letto da `<Style id=…><LineStyle><color>` referenziato dal
  `<styleUrl>`. Formato KML `aabbggrr` (alpha, poi **B-G-R invertito**): il
  rosso `#E53935` diventa `ff3935e5`. Senza stile → colore di default.
- Nome mancante → `Percorso N`.

### 3.3 Tappe (punti-POI) di un percorso

Per marcare un vertice come tappa, aggiungere una `<Folder>` chiamata
**esattamente** `__StradarioRoutePoints__` con un `<Placemark><Point>` per
tappa:

```xml
<Folder>
  <name>__StradarioRoutePoints__</name>
  <Placemark>
    <name>Duomo</name>
    <description>Cattedrale</description>
    <Point><coordinates>11.2558,43.7731,0</coordinates></Point>
    <ExtendedData>
      <Data name="routeId"><value>1</value></Data>
      <Data name="pointIndex"><value>0</value></Data>
    </ExtendedData>
  </Placemark>
</Folder>
```

- Associazione esatta: `routeId` = numero nello `styleUrl` del percorso
  (`#style_<routeId>`), `pointIndex` = posizione (da 0) del vertice.
- Senza `ExtendedData` (o con id non corrispondenti) l'app associa la tappa al
  **vertice più vicino** entro una piccola tolleranza.
- Questa Folder non viene mai letta come gruppo POI.
- Un Placemark della Folder senza `<name>` viene ignorato.

---

## 4. GPX

- Radice `<gpx version="1.1">`. Riconosciuto dall'elemento radice, non
  dall'estensione.
- **POI:** ogni `<wpt lat=".." lon="..">` con `<name>` e `<desc>` (o `<cmt>`).
  Il GPX non ha cartelle → **un solo gruppo** con il nome del file.
- **Percorsi:** ogni `<trk>` (tutti i suoi `<trkseg>` uniti) e ogni `<rte>`
  è un percorso, col colore di default (GPX non ha colore).
- **Tappe:** un `<trkpt>`/`<rtept>` con `<name>` diventa punto-POI (con
  `<desc>` come descrizione).
- **Nessuna data, nessuna icona** in GPX.
- ⚠️ Per i file `.gpx` l'app chiede se le coordinate sono già WGS84 o in
  GCJ-02 (vedi §8). Per evitare la domanda, scrivere `wgs84` nel nome del file
  (es. `giro-wgs84.gpx`).

---

## 5. CSV

Due file piatti e distinti, apribili in Excel/LibreOffice. RFC 4180: separatore
**virgola**, campi con virgole/virgolette/a capo racchiusi tra `"..."`, virgolette
interne raddoppiate (`""`). Prima riga = intestazione, **nomi colonna
esatti** (maiuscole comprese). Sempre WGS84, nessuna domanda GCJ-02.

L'app distingue i due tipi dall'intestazione: se c'è `IndicePunto` è un CSV
percorsi, altrimenti se ci sono `Nome`+`Lat`+`Lon` è un CSV POI; altrimenti
errore.

### 5.1 CSV POI — una riga per POI

```csv
Gruppo,Icona,Colore,Nome,Lat,Lon,Descrizione
Musei,Monument,#FF6B9D,Uffizi,43.7625,11.2625,Galleria dell'arte
Musei,Church,#FF6B9D,Duomo,43.7731,11.2558,"Cattedrale, con cupola"
```

| Colonna | Obbligatoria | Note |
|---|---|---|
| `Nome` | **sì** | riga senza nome → saltata |
| `Lat`, `Lon` | **sì** | numeri con punto; riga non numerica → saltata |
| `Gruppo` | no | righe con lo stesso nome = stesso gruppo (ordine di prima comparsa); vuoto → nome del file |
| `Icona` | no | nome inglese dell'enum (§2.3: `Pin`, `Monument`, `Church`…), maiuscole indifferenti; per **ogni riga**; non valida → `Pin` |
| `Colore` | no | `#RRGGBB`, letto dalla **prima riga del gruppo** |
| `Descrizione` | no | Markdown |

Non esistono colonne per le date: per i POI datati usare JSON o KML.

### 5.2 CSV percorsi — una riga per punto

```csv
Percorso,Colore,Descrizione,IndicePunto,Lat,Lon,PuntoPOI,PuntoEtichetta,PuntoDescrizione,PuntoIcona
Giro centro,#E53935,A piedi,0,43.7731,11.2558,1,Duomo,Cattedrale,Church
Giro centro,#E53935,A piedi,1,43.7710,11.2520,,,,
Giro centro,#E53935,A piedi,2,43.7654,11.2528,,,,
```

| Colonna | Obbligatoria | Note |
|---|---|---|
| `Lat`, `Lon` | **sì** | |
| `Percorso` | no | righe con lo stesso nome = stesso percorso; vuoto → `Percorso N` |
| `Colore`, `Descrizione` | no | letti dalla **prima riga** del percorso |
| `IndicePunto` | no | i punti sono **riordinati per questo valore**; se assente vale l'ordine delle righe |
| `PuntoPOI` | no | `1` = il punto è una tappa |
| `PuntoEtichetta`, `PuntoDescrizione`, `PuntoIcona` | no | usate solo se `PuntoPOI` = `1`; icona non valida → `Pin` |

Un percorso con meno di 2 punti viene scartato. Niente date nel CSV.

---

## 6. Date e orari

Il KML nativo (`<TimeStamp>`/`<TimeSpan>`) **non è usato**. Le date vivono in
`<ExtendedData>` del `<Placemark>` (POI **o** percorso), sempre con queste
chiavi:

| Chiave | Significato |
|---|---|
| `stradarioDateStart` | data/ora di inizio (o istante unico) |
| `stradarioDateEnd` | fine; solo per intervalli Da/A |

- Formato valore: **`yyyy-MM-ddTHH:mm:ss`**, es. `2026-08-10T09:30:00`.
  **Niente `Z` né offset**: l'orario è «locale ingenuo».
- Il blocco `<ExtendedData>` è figlio diretto di `<Placemark>`, dopo
  `<Point>`/`<LineString>`. Se esiste già, aggiungere le `<Data>` nello stesso
  blocco, mai un secondo.
- Nessuna data = nessun `<ExtendedData>`.
- Solo data senza ora: usare `T00:00:00` (viene mostrata come «solo data»).
- Le date servono a ordinare POI e percorsi nel **piano di viaggio** del PDF.
- Per modificare un KML già esistente c'è la skill `kml-add-dates`.

In JSON `.stradario` i campi sono `DateStart`/`DateEnd` (POI) e
`StartDateTime`/`EndDateTime` (percorsi), stesso formato.

---

## 7. Testo, Markdown e caratteri

- **`Description`** (POI, gruppi, percorsi, tappe) accetta **Markdown**: titoli,
  grassetto, corsivo, `code`, liste anche annidate, link cliccabili e tabelle
  pipe. Nel PDF è reso ricco, nella mappa il tooltip mostra una versione
  essenziale. Le **etichette** (`Label`, `Name`, `PoiLabel`) restano testo
  semplice.
- Una tabella pipe deve essere preceduta da una riga vuota (l'app la aggiunge
  se la dimentichi, ma meglio metterla).
- ⚠️ **Import KML/GPX: i testi vengono resi ASCII.** Nomi e tag in alfabeti
  non latini (cinese, cirillico…) vengono sostituiti da una variante ASCII
  trovata in `<description>` (es. `name:en`) oppure ripuliti dai caratteri non
  ASCII; le lettere accentate perdono solo l'accento (`Pekín` → `Pekin`).
  Un `<name>` che sembra un percorso di file (`/mnt/nas/.../2026.03.11`) viene
  ridotto all'ultimo segmento. Per mantenere l'unicode intatto usare il
  **JSON `.stradario`** (che non applica questa pulizia) oppure fornire nomi
  già in ASCII/latino.

---

## 8. Cina: GCJ-02 vs WGS84

L'app usa WGS84 (come i tile OSM). Le mappe cinesi (Amap, Baidu, e Google Maps
per punti dentro la Cina continentale) usano GCJ-02, spostato anche di centinaia
di metri.

- **KML/KMZ e CSV**: sempre WGS84, nessuna correzione.
- **GPX**: l'app chiede se applicare la correzione GCJ-02→WGS84 (predefinito:
  «Già WGS84»). La correzione agisce solo sui punti dentro il rettangolo della
  Cina.
- Se generi tu il file con coordinate prese da OSM o da un GPS reale, sono già
  WGS84: rispondere «Già WGS84».

---

## 9. Checklist rapida per generare un file valido

1. File ben formato (XML/JSON/CSV valido), UTF-8, decimali col punto.
2. Coordinate in **ordine `lon,lat`** nel KML; `lat`,`lon` nel GPX/CSV.
3. Ogni percorso ≥ 2 punti.
4. Colori POI/percorsi in JSON/CSV come `#RRGGBB`; nel KML come `aabbggrr`.
5. Icone solo fra i 16 valori del §2.3; in KML/GPX non sono importabili.
6. Date solo come `ExtendedData` (`stradarioDateStart`/`End`) in KML, o campi
   dedicati in JSON; non in GPX/CSV.
7. Tappe di percorso: Folder `__StradarioRoutePoints__` (KML), `IsPoi` (JSON),
   `PuntoPOI` (CSV), `<name>` sul `<trkpt>` (GPX).
8. Per testi non latini usare JSON; in KML/GPX diventano ASCII.
9. Per sicurezza, provare l'import su un progetto vuoto prima di usarlo su
   uno vero.

## 10. Quale formato scegliere

| Esigenza | Formato |
|---|---|
| Progetto completo (pagine, impostazioni, icone, date, tappe) | `.stradario` |
| POI con gruppi, date, icone nel Google Earth | KMZ |
| POI + percorsi insieme, colori percorsi, date | KML/KMZ |
| Scambio con GPS/app outdoor | GPX (perde gruppi, colori, icone, date) |
| Editing massivo in foglio di calcolo | CSV (POI e percorsi in due file) |
