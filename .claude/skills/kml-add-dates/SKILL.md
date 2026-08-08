---
name: kml-add-dates
description: Aggiunge date/orari opzionali (Da/A) ai Placemark di un file KML/KMZ esistente nel formato esatto che StradarioApp si aspetta, così che riaprendolo nell'app le date vengano rilette correttamente su POI e percorsi.
---

# Aggiungere date a un KML per StradarioApp

## Quando usare questa skill

L'utente vuole modificare (o generare da zero) un file `.kml`/`.kmz` in modo
che, riaperto in StradarioApp, i POI e/o i percorsi risultino datati
(campi "Da"/"A" dell'app). Vale sia per file già esportati dall'app sia per
KML arbitrari da importare per la prima volta.

## Regole del formato (non negoziabili)

StradarioApp non usa i tag data nativi di KML (`<TimeStamp>`/`<TimeSpan>`):
legge le date da `<ExtendedData>` con chiavi precise (v.
`Services/KmlIo.cs`, `BuildDateExtendedData`/`ParseDateExtendedData`).

- Chiavi esatte: `stradarioDateStart` (obbligatoria se si vuole impostare
  una data) e `stradarioDateEnd` (opzionale, solo per un intervallo Da/A —
  senza, l'app tratta la data come un singolo istante).
- Formato valore: `yyyy-MM-ddTHH:mm:ss` (es. `2026-08-10T09:30:00`).
  **Niente `Z` finale né offset di fuso orario** — l'app tratta l'orario
  come ora locale "ingenua", un suffisso di fuso può essere riletto in
  modo inatteso.
- Il blocco `<ExtendedData>` va dentro `<Placemark>`, come fratello di
  `<Point>` (per un POI) o `<LineString>` (per un percorso) — subito dopo
  la chiusura di quel tag, prima di `</Placemark>`.
- Un POI/percorso senza data si lascia semplicemente senza
  `<ExtendedData>` — non esiste una convenzione per "nessuna data
  esplicita", l'assenza del blocco È la codifica di "nessuna data".
- Se un `<Placemark>` ha già un `<ExtendedData>` per altri scopi (es. i
  punti-POI inline di un percorso, che usano le chiavi `routeId`/
  `pointIndex`), aggiungere le nuove `<Data>` allo stesso blocco esistente
  — mai crearne uno duplicato nello stesso Placemark.
- Solo data, senza orario: usare comunque `T00:00:00` (mezzanotte) — v.
  nota sotto.
- Non toccare nient'altro nel file (coordinate, styleUrl, nomi, struttura
  delle Folder) a meno che non sia esplicitamente richiesto.

## Nota sulla mezzanotte

Nell'editor di StradarioApp "00:00" e "nessun orario" sono ora due stati
distinti a livello di interfaccia, ma il valore effettivo salvato è lo
stesso (`DateTime` con `TimeOfDay == 00:00:00`) in entrambi i casi — quindi
scrivere `T00:00:00` in un KML equivale, una volta importato, a "nessun
orario specificato" (la data verrà mostrata senza orario nell'albero/PDF).
Se serve davvero mezzanotte come orario significativo, sappi che verrà
comunque visualizzata come "solo data" nell'app — è un limite noto del
formato dati attuale, non un errore di questa skill.

## Esempio — un POI con data singola

```xml
<Placemark>
  <name>Torre Tamburo</name>
  <styleUrl>#style_3</styleUrl>
  <Point>
    <coordinates>116.397,39.907,0</coordinates>
  </Point>
  <ExtendedData>
    <Data name="stradarioDateStart">
      <value>2026-08-10T09:30:00</value>
    </Data>
  </ExtendedData>
</Placemark>
```

## Esempio — un percorso con range Da/A

```xml
<Placemark>
  <name>Percorso Giorno 1</name>
  <styleUrl>#style_1</styleUrl>
  <LineString>
    <tessellate>1</tessellate>
    <coordinates>116.39,39.91,0 116.40,39.92,0</coordinates>
  </LineString>
  <ExtendedData>
    <Data name="stradarioDateStart"><value>2026-08-10T09:00:00</value></Data>
    <Data name="stradarioDateEnd"><value>2026-08-10T18:00:00</value></Data>
  </ExtendedData>
</Placemark>
```

## Verifica dopo la modifica

1. Il file resta XML ben formato (nessun tag orfano/non chiuso).
2. Ogni `<ExtendedData>` aggiunto è dentro un `<Placemark>` valido, non a
   livello di `<Folder>`/`<Document>`.
3. Se possibile, riaprire il file in StradarioApp (Importa) e controllare
   che le date compaiano nell'editor del POI/percorso corrispondente.
