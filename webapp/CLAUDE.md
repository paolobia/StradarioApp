# webapp/StradarioViewer

App accessoria, **progetto .NET indipendente** (nessuna project reference verso
`StradarioApp.csproj` — stesso pattern isolato di `osm/OsmExtractor`, vedi
`osm/CLAUDE.md`): Blazor WebAssembly (.NET 8), 100% client-side, installabile
come PWA. Serve a consultare "in tasca" (telefono/tablet) il piano di viaggio
di un progetto StradarioApp: percorsi e POI datati, navigabili giorno per
giorno, su una mappa OSM.

## Cosa fa

- L'utente carica un file `.stradario` (lo stesso JSON prodotto da
  StradarioApp) tramite file picker.
- Il JSON viene salvato per intero nel `localStorage` del browser — resta
  disponibile alle visite successive senza dover ricaricare il file.
- I percorsi (`Percorso.StartDateTime`/`EndDateTime`) e i POI datati
  (`PoiItem.DateStart`/`DateEnd`) vengono raggruppati per giorno; frecce
  ◀/▶ navigano tra i giorni che hanno almeno un percorso/POI (i giorni vuoti
  vengono saltati). Se il progetto non ha **nessuna** data impostata da
  nessuna parte, il filtro per-giorno viene disattivato e si mostra tutto.
- Percorsi/POI del giorno corrente sono disegnati su una mappa Leaflet
  (tile OSM standard) e listati a fianco; click su un elemento (in lista o
  sulla mappa) apre un pannello con etichetta e descrizione.

## Build e avvio

```bash
cd webapp/StradarioViewer
dotnet run          # dev server, http://localhost:5xxx
dotnet build          # compilazione soltanto
dotnet publish -c Release -o ./publish   # output statico, servibile da un host qualsiasi
```

Nessuna solution file condivisa col progetto principale: si builda/lancia
sempre da dentro `webapp/StradarioViewer/`. In `StradarioApp.csproj`,
`DefaultItemExcludes` esclude `webapp/**` dal glob di compilazione del
progetto desktop (stessa ragione già documentata per `osm/**`).

## Formato dati: modelli copiati a mano

`Models/StradarioModels.cs` in questo progetto è un **sottoinsieme copiato**
(non referenziato) di `Models/StradarioModels.cs` del progetto desktop —
Blazor WASM non può fare project reference a un progetto Avalonia. Contiene
solo i campi che il viewer legge davvero (niente `MapPage`/`StradarioSettings`
ecc.). Deserializzazione con `System.Text.Json` + un
`JsonSerializerContext` source-generated (`StradarioJsonContext`), non
Newtonsoft — compatibile perché nessuna proprietà lato desktop usa
`[JsonProperty]` (i nomi JSON coincidono col nome C# in entrambi i progetti).

**Se il modello principale cambia forma** (nuovi campi data-correlati, nuovi
valori enum, ecc.), va risincronizzato **a mano** qui — nessun meccanismo
automatico di condivisione tra i due progetti. Gli enum eventualmente
copiati vanno mantenuti nello stesso ordine di dichiarazione del progetto
desktop (persistiti come interi posizionali nel file `.stradario`).

## Struttura

```
webapp/StradarioViewer/
  Models/StradarioModels.cs   # sottoinsieme dati (vedi sopra)
  Services/
    LocalStorageService.cs     # wrapper JS interop su window.localStorage
    TravelPlanService.cs       # carica/filtra il progetto per giorno
  Shared/
    MapView.razor               # mappa Leaflet via JS interop (wwwroot/js/leaflet-interop.js)
    RouteDetail.razor           # pannello etichetta+descrizione
  Pages/Home.razor              # pagina unica: import file, toolbar giorno, mappa+lista
  wwwroot/
    js/leaflet-interop.js       # init/draw/clear/fit sulla mappa Leaflet
    manifest.webmanifest        # manifest PWA (icone riprese da Resources/AppIcon/)
    service-worker*.js          # cache-first per l'app shell (i tile OSM restano da rete)
```

## Nota tecnica: JSInterop e array covariance

`IJSObjectReference.InvokeVoidAsync(id, params object?[]? args)` — se l'UNICO
argomento passato ha un tipo staticamente compatibile con `object?[]` (es. un
array di array, `double[][]`: gli array sono covarianti sui tipi riferimento
e `double[]` è un tipo riferimento), il compilatore lo passa così com'è come
"args" invece di avvolgerlo in un array a un elemento — l'array viene
"spalmato" come tanti argomenti JS separati invece che come un singolo
parametro. Visto dal vivo in `MapView.RedrawAsync`: `fitToPoints` riceveva
solo il primo punto invece dell'intero elenco. Fix: assegnare l'array a una
variabile tipata `object` (non `object[]`) prima di passarla, così il
compilatore lo avvolge normalmente in un array-argomenti a un elemento.

## Limiti noti / non fatto

- Nessun test automatico degli scenari mobile reali (solo verificato via
  Playwright headless + dev server).
- Le icone POI non sono le stesse icone vettoriali di `PoiIconRenderer`
  (desktop) — il viewer usa semplici marker colorati, per restare leggero e
  senza duplicare quel renderer.
- Import: solo file picker manuale, niente drag&drop.
