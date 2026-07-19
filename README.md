# StradarioApp

Applicazione desktop C# per la creazione di stradari cartografici con dati OpenStreetMap.
Portabile su **Linux** e **Windows** grazie a Avalonia UI.

---

## Funzionalità

1. **Impostazioni** – Formato pagina (A4/A3, Portrait/Landscape), DPI, scala (1:100.000 / 1:200.000)
2. **Mappa interattiva** – Visualizzazione OSM con pan (drag) e zoom (rotella)
3. **Gestione pagine** – Click destro sulla mappa per aggiungere una pagina; lista con modifica/cancellazione
4. **Generazione PDF** – Stradario completo con pagine ordinate e bordi con indicazione pagine adiacenti
5. **Salvataggio progetto** – File `.stradario` (JSON) con tutte le impostazioni e pagine

---

## Dipendenze NuGet

| Pacchetto               | Uso                                       |
|-------------------------|-------------------------------------------|
| Avalonia                | UI cross-platform (Windows/Linux/macOS)  |
| Avalonia.Desktop        | Lifecycle desktop                         |
| Avalonia.Themes.Fluent  | Tema visuale                              |
| Avalonia.Fonts.Inter    | Font Inter                                |
| Avalonia.Controls.Skia  | SKCanvasView per Avalonia 11              |
| SkiaSharp               | Rendering 2D su canvas                    |
| BruTile                 | Schema tile OSM (TileIndex)               |
| PdfSharpCore            | Generazione PDF                           |
| Newtonsoft.Json         | Serializzazione progetto                  |

---

## Build e avvio

### Prerequisiti
- [.NET 8 SDK](https://dotnet.microsoft.com/download)

### Linux
```bash
cd StradarioApp
dotnet restore
dotnet run
```

### Windows
```cmd
cd StradarioApp
dotnet restore
dotnet run
```

### Pubblicazione self-contained
```bash
# Linux x64
dotnet publish -c Release -r linux-x64 --self-contained true -o ./publish/linux

# Windows x64
dotnet publish -c Release -r win-x64 --self-contained true -o ./publish/win
```

---

## Struttura del progetto

```
StradarioApp/
├── Program.cs                  # Entry point Avalonia
├── StradarioApp.csproj         # Progetto .NET
├── Models/
│   └── StradarioModels.cs      # Modelli dati (pagine, impostazioni, progetto)
├── Services/
│   ├── GeoUtils.cs             # Conversioni geografiche e calcoli cartografici
│   ├── MapRenderer.cs          # Rendering mappa con BruTile + SkiaSharp
│   ├── PdfGenerator.cs         # Generazione PDF stradario
│   └── ProjectService.cs       # Salvataggio/caricamento progetto JSON
└── UI/
    ├── MainWindow.cs           # Finestra principale
    ├── SettingsWindow.cs       # Dialog impostazioni
    └── ProgressWindow.cs       # Dialog avanzamento PDF
```

---

## Uso rapido

1. Avvia l'app
2. *(Opzionale)* Clicca **⚙️ Impostazioni** per scegliere formato, DPI e scala
3. Naviga la mappa con **drag** (pan) e **rotella** (zoom)
4. Clicca **➕ Aggiungi pagina**, poi clicca sulla mappa dove vuoi posizionare la pagina
5. Ripeti per tutte le zone da includere
6. Clicca **📄 Genera PDF** per produrre lo stradario
7. Clicca **💾 Salva** per conservare il progetto

---

## Note tecniche

- I tile OSM vengono scaricati da `tile.openstreetmap.org` e tenuti in cache in memoria
- Il PDF include: pagina indice + una pagina per ogni quadrante definito
- Le pagine nel PDF sono ordinate da sinistra a destra, dall'alto verso il basso
- Ogni pagina PDF mostra i riferimenti alle pagine adiacenti (N, S, E, O) nei bordi
- Il file `.stradario` è JSON leggibile e modificabile manualmente

---

## Limiti noti / Sviluppi futuri

- La cache tile è solo in memoria (non persistente tra sessioni)
- Nessun supporto per tile offline/MBTiles (ma BruTile lo supporta facilmente)
- La modifica dell'etichetta pagina è attualmente solo tramite il file JSON
- Aggiungere dialog di modifica pagina (etichetta, descrizione) è il prossimo passo

---

## Licenza

Uso educativo/personale. I dati cartografici sono © OpenStreetMap contributors (ODbL).
