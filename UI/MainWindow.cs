// =============================================================================
// UI/MainWindow.axaml.cs
//
// SINOSSI: Finestra principale dell'applicazione stradario.
//   - Pannello sinistro: impostazioni (formato, DPI, scala) e lista pagine
//   - Pannello destro: canvas mappa SkiaSharp con pan/zoom interattivi
//   - Barra degli strumenti: apri/salva progetto, aggiungi pagina, genera PDF
//   - Interazione: click sx = sposta vista, click dx = aggiunge pagina centrata
//     sulla posizione cliccata, drag = pan, scroll = zoom
//   - Modifica/cancellazione pagine dalla lista nel pannello sinistro
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
// MapCanvas: controllo custom che espone SKCanvas tramite Avalonia.Skia
// (non esiste un pacchetto "SkiaSharp.Views.Avalonia" separato)
using StradarioApp.Models;
using StradarioApp.Resources;
using StradarioApp.Services;

namespace StradarioApp.UI
{
    public partial class MainWindow : Window
    {
        // ---------------------------------------------------------------
        // Stato applicazione
        // ---------------------------------------------------------------
        private StradarioProject _project   = new StradarioProject();
        private MapRenderer      _renderer  = new MapRenderer();
        private ProjectService   _projSvc   = new ProjectService();
        private string?          _currentFilePath = null;

        // Vista mappa
        private double _viewCenterLon;
        private double _viewCenterLat;
        private double _viewZoom = 10.0;

        // Drag mappa (pan)
        private bool   _isDragging    = false;
        private Point  _dragStart;
        private double _dragCenterLon;
        private double _dragCenterLat;

        // Drag pagina selezionata (sposta pagina)
        private bool   _isDraggingPage   = false;
        private double _pageDragStartLon; // lon centro pagina all'inizio del drag
        private double _pageDragStartLat;

        // Drag di un POI esistente (trascinato direttamente, senza bisogno di
        // selezionarlo prima: clic e trascina sopra il marker)
        private bool     _isDraggingPoi      = false;
        private PoiItem? _draggingPoiItem    = null;
        private int      _draggingPoiGroupId = -1;

        // Drag di un vertice esistente di un percorso
        private bool      _isDraggingRoutePoint = false;
        private Percorso? _draggingRoute        = null;
        private int        _draggingPointIndex  = -1;

        // Raggio di hit-test (in pixel schermo) per agganciare il trascinamento
        // di un marker POI o di un vertice di un percorso
        private const double PoiHitRadiusPx        = 14.0;
        private const double RoutePointHitRadiusPx = 10.0;

        // Pagina selezionata nella lista
        private int? _selectedPageId = null;

        // Stato di espansione dell'albero di navigazione nel pannello sinistro
        // (rami "Pagine" / "Gruppi POI" e i singoli gruppi al loro interno)
        private bool _navPagesExpanded    = true;
        private bool _navPoiExpanded      = true;
        private bool _navPercorsiExpanded = true;
        private readonly HashSet<int> _navCollapsedGroupIds = new();

        // Visibilità sulla mappa (icona "occhio" nell'albero): non tocca il
        // progetto, è solo uno stato di visualizzazione della sessione corrente
        private bool _pagesVisible    = true;
        private bool _poiVisible      = true;
        private bool _percorsiVisible = true;
        private readonly HashSet<int> _hiddenPoiGroupIds  = new();
        private readonly HashSet<int> _hiddenPercorsoIds  = new();

        private readonly PoiService       _poiSvc      = new PoiService();
        private readonly PercorsoService  _percorsoSvc = new PercorsoService();

        // Modalità: "addPage" = prossimo click aggiunge pagina
        private bool _addPageMode = false;

        // Modalità: "addRoute" = ogni click aggiunge un punto al percorso in
        // disegno (_drawingRoute); shift+click conferma (aggiunge l'ultimo
        // punto e chiude), tasto destro annulla l'ultimo punto (o l'intera
        // modalità se non ci sono punti)
        private bool      _addRouteMode  = false;
        private Percorso? _drawingRoute  = null;

        // Modalità: "addPoi" = il prossimo click sulla mappa apre il dialog
        // di creazione POI, precompilato con le coordinate cliccate, nel
        // gruppo indicato
        private bool      _addPoiMode        = false;
        private PoiGroup? _addPoiTargetGroup = null;

        // Modalità: "addRoutePoints" = estende graficamente un percorso già
        // esistente cliccando sulla mappa (in aggiunta al disegno di un nuovo
        // percorso da zero). Ogni click aggiunge un punto a una delle due
        // estremità del percorso (quella più vicina al primo punto cliccato
        // nella sessione corrente, decisa una volta sola); shift+click
        // conferma e chiude, tasto destro annulla l'ultimo punto aggiunto in
        // questa sessione (o l'intera modalità se non ne ha ancora aggiunti).
        private bool      _addRoutePointsMode         = false;
        private Percorso? _addRoutePointsTarget       = null;
        private int       _addRoutePointsSessionCount = 0;
        private bool      _addRoutePointsPrepend      = false;

        // Modalità: "righello" = ogni click aggiunge un punto alla spezzata di
        // misurazione (non salvata nel progetto); tasto destro annulla l'ultimo
        // punto (o esce dalla modalità se non ce ne sono), ESC esce e azzera
        private bool                 _rulerMode   = false;
        private readonly List<GeoPoint> _rulerPoints = new();

        // Modalità: "ricerca POI online" = risultati candidati mostrati come
        // marker sulla mappa; click su un marker lo conferma e lo aggiunge al
        // gruppo scelto, ESC annulla
        private bool                          _poiSearchMode      = false;
        private List<PoiSearchService.Result> _poiSearchResults   = new();
        // Risultato attualmente sotto il cursore (tooltip con più dettagli in OnPaintMapSurface)
        private PoiSearchService.Result? _hoveredPoiSearchResult;
        // POI già presente in un gruppo del progetto (non un risultato di
        // ricerca) attualmente sotto il cursore: stesso tooltip, vedi
        // DrawPlacedPoiTooltip. Attivo solo fuori dalla modalità ricerca (che
        // ha già il suo overlay/tooltip dedicato) e non durante un drag.
        private (PoiGroup group, PoiItem item)? _hoveredPoi;
        // Testo con cui è stata avviata la ricerca in corso (o "Ricerca GPS"
        // per la ricerca inversa): usato per intitolare un gruppo POI creato
        // automaticamente se il progetto non ne ha ancora nessuno
        private string _poiSearchQueryLabel = "";
        // Vero quando l'ultima ricerca per categoria ha dovuto restringere
        // l'area (vista troppo ampia, vedi RunCategorySearchAsync/
        // MaxCategorySearchDegrees): l'hint persistente disegnato sulla
        // mappa mentre la ricerca è attiva (sotto, nel PaintSkia) lo include
        // sempre, a differenza del messaggio transitorio della barra di
        // stato che scompare dopo pochi secondi.
        private bool _poiSearchAreaClamped = false;
        // Vero quando i risultati correnti vengono dalla ricerca per
        // indirizzo (RunAddressSearchAsync): lì Result.DisplayName è già
        // l'indirizzo completo formattato da Nominatim, quindi
        // ConfirmPoiSearchResult non deve aggiungere anche Result.Address
        // nella descrizione del POI creato (sarebbe la stessa informazione
        // ripetuta due volte). Per categoria/città Address è invece
        // un'informazione distinta dal nome (indirizzo del POI / popolazione
        // della città) e va tenuta.
        private bool _poiSearchResultsAreAddresses = false;
        // Ultimo testo cercato con successo (nessuna eccezione di rete): proposto
        // precompilato la prossima volta che si apre il campo di ricerca
        private string _lastPoiSearchQuery = "";
        // Vero mentre ConfirmPoiSearchResult chiama RefreshNavigationTree per
        // aggiornare l'albero dopo aver aggiunto un POI: evita che l'uscita
        // automatica dalla ricerca (vedi RefreshNavigationTree) chiuda la
        // modalità subito dopo un'aggiunta riuscita, quando l'utente potrebbe
        // volerne confermare altri dagli stessi risultati
        private bool _suppressPoiSearchAutoExit = false;

        // Modalità: "identifica" (❓📍 in toolbar) = il prossimo clic sulla
        // mappa avvia la ricerca inversa "cosa c'è qui" (la stessa di shift +
        // tasto destro, tenuta anche come scorciatoia per chi la conosce già,
        // ma poco scopribile da sola: questo bottone la rende visibile)
        private bool _identifyMode = false;

        // "Localizza dove sono" (📍 in toolbar, toggle): un click avvia il
        // servizio di posizione del sistema operativo (GeoClue2 su Linux,
        // Windows Location API su Windows — vedi Services/GeolocationService)
        // e centra la mappa sul primo fix ricevuto; i fix successivi
        // aggiornano solo il marker, senza ricentrare di nuovo (per non
        // "strappare via" la mappa da sotto l'utente se nel frattempo ha
        // pannato/zoomato altrove). Un secondo click ferma il servizio e
        // rimuove il marker. Se il sistema non riesce a fornire una
        // posizione, l'errore appare nella barra di stato invece del marker.
        private readonly GeolocationService _geoLocationSvc = new();
        private bool                        _myLocationActive        = false;
        private bool                        _myLocationCenteredOnce  = false;
        private GeoPoint?                   _myLocation              = null;
        private double?                     _myLocationAccuracyM     = null;

        // Solo una modalità di inserimento (pagina/percorso/POI/punti/righello/
        // ricerca/identifica) può essere attiva alla volta: iniziarne una
        // annulla le altre
        private void CancelAllAddModes()
        {
            _addPageMode       = false;
            _addRouteMode      = false;
            _drawingRoute      = null;
            _addPoiMode        = false;
            _addPoiTargetGroup = null;

            _addRoutePointsMode         = false;
            _addRoutePointsTarget       = null;
            _addRoutePointsSessionCount = 0;

            _rulerMode = false;
            _rulerPoints.Clear();

            _identifyMode = false;

            _poiSearchMode    = false;
            _poiSearchResults = new List<PoiSearchService.Result>();
            _hoveredPoiSearchResult = null;
            _hoveredPoi = null;
            HidePoiSearchBox();
        }

        // ---------------------------------------------------------------
        // Blocco automatico per inattività (pagine/gruppi POI/percorsi
        // sbloccati): timestamp UTC dell'ultima interazione, solo per la
        // sessione corrente (non salvati nel progetto). Se un oggetto non
        // compare nel dizionario la prima volta che il timer lo controlla,
        // viene considerato "toccato ora" (non blocca istantaneamente
        // elementi preesistenti mai più toccati in questa sessione).
        private readonly Dictionary<int, DateTime> _pageLastTouchUtc     = new();
        private readonly Dictionary<int, DateTime> _poiGroupLastTouchUtc = new();
        private readonly Dictionary<int, DateTime> _percorsoLastTouchUtc = new();
        private DispatcherTimer? _autoLockTimer;

        // Autosalvataggio periodico: salva su un file "sidecar" separato
        // (<file>.autosave, o un file temporaneo se il progetto non è mai
        // stato salvato) senza toccare il file/percorso scelto dall'utente,
        // né lo stato "modifiche non salvate" (_isDirty resta invariato)
        private DispatcherTimer? _autosaveTimer;
        private static readonly TimeSpan AutosaveInterval = TimeSpan.FromMinutes(3);

        // Barra di stato in basso: riepilogo permanente + posizione cursore + messaggio temporaneo
        private TextBlock?       _statusBarSummaryText;
        private TextBlock?       _statusBarPositionText;
        private TextBlock?       _statusBarUpdateText;
        private TextBlock?       _statusBarMessageText;
        private DispatcherTimer? _statusMessageTimer;
        private string?          _availableUpdateUrl;

        // Campo di filtro dell'albero di navigazione (Pagine/Gruppi POI/Percorsi)
        private TextBox? _navFilterBox;
        private string   _navFilterText = "";

        // Controlli toolbar per la ricerca POI online: casella di testo libero
        // + combo per restringere a una categoria/tag OSM nota (vedi
        // PoiSearchService.AllCategories) — indice 0 = "qualsiasi categoria"
        // (nessun filtro). Compaiono/scompaiono insieme, entrambi attivati dal
        // pulsante "lente" della toolbar; la ricerca parte SOLO premendo
        // Invio nella casella di testo (o ricliccando la lente), mai alla sola
        // selezione di una categoria — vedi OnPoiSearchAsync.
        private TextBox?  _poiSearchTextBox;
        private ComboBox? _categoryFilterComboBox;
        private readonly PoiSearchService _poiSearchSvc = new();

        // Elenco file recenti, persistito su disco
        private readonly RecentFilesService _recentFilesSvc = new();

        // Chiave API Groq e chiave API tile server: credenziali dell'utente,
        // non del progetto — persistite a parte (vedi AppPreferencesService)
        // e riapplicate a ogni progetto nuovo/aperto che non ne abbia già una
        // propria, così non vanno reinserite a ogni volta
        private readonly AppPreferencesService _appPrefsSvc = new();

        // Applica le preferenze globali (chiavi API) alle impostazioni del
        // progetto corrente, SENZA sovrascrivere un valore che il progetto ha
        // già (es. un .stradario salvato in precedenza con una propria chiave)
        private void ApplyGlobalPreferences()
        {
            var (groqKey, tileKey) = _appPrefsSvc.Load();
            if (string.IsNullOrWhiteSpace(_project.Settings.GroqApiKey))
                _project.Settings.GroqApiKey = groqKey;
            if (string.IsNullOrWhiteSpace(_project.Settings.TileServerApiKey))
                _project.Settings.TileServerApiKey = tileKey;
        }

        // Undo/redo: cattura almeno lo spostamento (drag) di pagine, POI e
        // vertici di percorsi. Ogni voce ha un'azione di annullamento e una
        // di ripetizione; una nuova azione svuota lo stack di redo.
        private class UndoEntry
        {
            public Action Undo = () => { };
            public Action Redo = () => { };
        }
        private readonly List<UndoEntry> _undoStack = new();
        private readonly List<UndoEntry> _redoStack = new();
        private const int MaxUndoEntries = 60;

        private void PushUndo(Action undo, Action redo)
        {
            _undoStack.Add(new UndoEntry { Undo = undo, Redo = redo });
            if (_undoStack.Count > MaxUndoEntries) _undoStack.RemoveAt(0);
            _redoStack.Clear();
        }

        private void Undo()
        {
            if (_undoStack.Count == 0) return;
            var entry = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            entry.Undo();
            _redoStack.Add(entry);
            _isDirty = true;
            RefreshNavigationTree();
            _mapCanvas?.InvalidateVisual();
            ShowStatusMessage(Strings.Get("MainWindow_Annullato"), seconds: 2);
        }

        private void Redo()
        {
            if (_redoStack.Count == 0) return;
            var entry = _redoStack[^1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            entry.Redo();
            _undoStack.Add(entry);
            _isDirty = true;
            RefreshNavigationTree();
            _mapCanvas?.InvalidateVisual();
            ShowStatusMessage(Strings.Get("MainWindow_Ripetuto"), seconds: 2);
        }

        // Stato geografico all'inizio del drag della pagina/POI/vertice
        // attualmente in corso, per poter costruire l'azione di undo al rilascio
        private GeoRect? _pageDragOrigBounds;
        private double   _poiDragOrigLon, _poiDragOrigLat;
        private double   _routePointDragOrigLon, _routePointDragOrigLat;

        // Selezione multipla di pagine (Ctrl+clic nell'albero), per eliminazione in blocco
        private readonly HashSet<int> _multiSelectedPageIds = new();

        // Selezione multipla di POI (Ctrl+clic nell'albero), per spostarli in
        // blocco in un altro gruppo. Coppia (GroupId, ItemId): gli ID dei POI
        // sono assegnati per gruppo (PoiService.GetNextItemId), quindi da soli
        // non sono univoci nel progetto
        private readonly HashSet<(int GroupId, int ItemId)> _multiSelectedPoiKeys = new();

        private void TouchPage(int id)     => _pageLastTouchUtc[id]     = DateTime.UtcNow;
        private void TouchPoiGroup(int id) => _poiGroupLastTouchUtc[id] = DateTime.UtcNow;
        private void TouchPercorso(int id) => _percorsoLastTouchUtc[id] = DateTime.UtcNow;

        // Blocca automaticamente pagine/gruppi/percorsi sbloccati che non
        // vengono toccati da più di Settings.AutoLockSeconds (0 = disabilitato)
        private void OnAutoLockTimerTick(object? sender, EventArgs e)
        {
            int seconds = _project.Settings.AutoLockSeconds;
            if (seconds <= 0) return;

            var now = DateTime.UtcNow;
            var timeout = TimeSpan.FromSeconds(seconds);
            bool changed = false;

            foreach (var page in _project.Pages)
            {
                if (page.IsLocked) continue;
                if (!_pageLastTouchUtc.TryGetValue(page.Id, out var t)) { _pageLastTouchUtc[page.Id] = now; continue; }
                if (now - t >= timeout) { page.IsLocked = true; changed = true; }
            }
            foreach (var group in _project.PoiGroups)
            {
                if (group.IsLocked) continue;
                if (!_poiGroupLastTouchUtc.TryGetValue(group.Id, out var t)) { _poiGroupLastTouchUtc[group.Id] = now; continue; }
                if (now - t >= timeout) { group.IsLocked = true; changed = true; }
            }
            foreach (var route in _project.Percorsi)
            {
                if (route.IsLocked) continue;
                if (!_percorsoLastTouchUtc.TryGetValue(route.Id, out var t)) { _percorsoLastTouchUtc[route.Id] = now; continue; }
                if (now - t >= timeout) { route.IsLocked = true; changed = true; }
            }

            if (changed)
            {
                _isDirty = true;
                RefreshNavigationTree();
                _mapCanvas?.InvalidateVisual();
            }
        }

        // Canvas mappa custom (usa Avalonia.Skia internamente)
        private MapCanvas? _mapCanvas;

        // Indica se il progetto ha modifiche non salvate
        private bool _isDirty = false;

        // ---------------------------------------------------------------
        // Costruttore
        // ---------------------------------------------------------------
        public MainWindow()
        {
            // Categorie POI personalizzate (Impostazioni -> tab "Categorie
            // POI"): vanno caricate PRIMA di InitializeComponent, che
            // costruisce subito il combo di ricerca leggendo
            // PoiSearchService.AllCategories.
            PoiSearchService.SetCustomCategories(_appPrefsSvc.LoadCustomPoiCategories());

            InitializeComponent();
            InitializeView();

            // Intercetta la chiusura della finestra per chiedere di salvare
            Closing += OnWindowClosing;
            KeyDown += OnMainWindowKeyDown;
            Closed  += (_, _) => _geoLocationSvc.Stop();

            // Drag&drop di file KMZ/KML/GPX su tutta la finestra (non solo
            // sulla mappa): stessa importazione del pulsante toolbar
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
            AddHandler(DragDrop.DropEvent,     OnWindowDrop);

            _geoLocationSvc.Started         += OnMyLocationStarted;
            _geoLocationSvc.PositionUpdated += OnMyLocationUpdated;
            _geoLocationSvc.ErrorOccurred   += OnMyLocationError;

            _autoLockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _autoLockTimer.Tick += OnAutoLockTimerTick;
            _autoLockTimer.Start();

            _autosaveTimer = new DispatcherTimer { Interval = AutosaveInterval };
            _autosaveTimer.Tick += OnAutosaveTimerTick;
            _autosaveTimer.Start();

            _ = CheckForUpdateOnStartupAsync();
        }

        // Controllo aggiornamenti in background all'avvio: silenzioso in caso
        // di esito negativo/errore (vedi UpdateChecker), mostra una notifica
        // persistente e cliccabile nella barra di stato solo se è disponibile
        // davvero una versione più recente.
        private async System.Threading.Tasks.Task CheckForUpdateOnStartupAsync()
        {
            var info = await UpdateChecker.CheckForNewerVersionAsync();
            if (info == null || _statusBarUpdateText == null) return;

            _availableUpdateUrl = info.ReleaseUrl;
            _statusBarUpdateText.Text      = string.Format(Strings.Get("MainWindow_NuovaVersioneDisponibile"), info.LatestVersion);
            _statusBarUpdateText.IsVisible = true;
        }

        // Percorso del file di autosalvataggio per il progetto corrente
        private string GetAutosavePath() =>
            _currentFilePath != null
                ? _currentFilePath + ".autosave"
                : Path.Combine(Path.GetTempPath(), "stradario_autosave.stradario");

        private async void OnAutosaveTimerTick(object? sender, EventArgs e)
        {
            if (!_isDirty) return;

            try
            {
                _project.ViewCenterLon = _viewCenterLon;
                _project.ViewCenterLat = _viewCenterLat;
                _project.ViewZoom      = _viewZoom;

                await _projSvc.SaveAsync(_project, GetAutosavePath());
                ShowStatusMessage(Strings.Get("MainWindow_SalvataggioAutomatico"), seconds: 3);
            }
            catch
            {
                // Autosalvataggio best-effort: un errore qui non deve interrompere il lavoro dell'utente
            }
        }

        // ESC annulla qualsiasi modalità di inserimento in corso (pagina/percorso/POI)
        private void OnMainWindowKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key == Avalonia.Input.Key.Escape &&
                (_addRouteMode || _addPageMode || _addPoiMode || _addRoutePointsMode || _rulerMode || _poiSearchMode || _identifyMode))
            {
                CancelAllAddModes();
                _mapCanvas?.InvalidateVisual();
            }

            // Ctrl+Z / Ctrl+Y (o Ctrl+Shift+Z) = undo/redo
            if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
            {
                if (e.Key == Avalonia.Input.Key.Z && !e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift))
                {
                    Undo();
                    e.Handled = true;
                }
                else if (e.Key == Avalonia.Input.Key.Y ||
                         (e.Key == Avalonia.Input.Key.Z && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)))
                {
                    Redo();
                    e.Handled = true;
                }
            }
        }

        // Chiede se salvare quando si chiude con modifiche non salvate
        private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isDirty) return;

            // Blocca la chiusura mentre aspettiamo la risposta dell'utente
            e.Cancel = true;

            bool save     = false;
            bool cancel   = false;

            var dlg = new Window
            {
                Title  = Strings.Get("MainWindow_ModificheNonSalvateTitolo"),
                Width  = 460,
                Height = 170,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new StackPanel
                {
                    Margin  = new Thickness(18),
                    Spacing = 16,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = Strings.Get("MainWindow_ModificheNonSalvateMessaggio"),
                            TextWrapping = TextWrapping.Wrap
                        },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                            Spacing = 10,
                            Children =
                            {
                                DialogUi.MakeDialogButton(Strings.Get("MainWindow_SalvaEdEsci"), primary: true),
                                DialogUi.MakeDialogButton(Strings.Get("MainWindow_EsciSenzaSalvare")),
                                DialogUi.MakeDialogButton(Strings.Get("MainWindow_Annulla"))
                            }
                        }
                    }
                }
            };

            var btns = ((StackPanel)((StackPanel)dlg.Content!).Children[1]);
            ((Button)btns.Children[0]).Click += (_, _) => { save   = true;  dlg.Close(); };
            ((Button)btns.Children[1]).Click += (_, _) => { save   = false; dlg.Close(); };
            ((Button)btns.Children[2]).Click += (_, _) => { cancel = true;  dlg.Close(); };

            await dlg.ShowDialog(this);

            if (cancel) return; // rimane aperta

            if (save)
            {
                if (_currentFilePath != null)
                    await SaveCurrentProject(_currentFilePath);
                else
                {
                    // Nessun file: apri "Salva come" e poi chiudi
                    var file = await StorageProvider.SaveFilePickerAsync(
                        new Avalonia.Platform.Storage.FilePickerSaveOptions
                        {
                            Title            = Strings.Get("MainWindow_SalvaProgettoTitolo"),
                            DefaultExtension = "stradario",
                            SuggestedFileName = _project.ProjectName,
                            FileTypeChoices  = new[]
                            {
                                new Avalonia.Platform.Storage.FilePickerFileType(Strings.Get("MainWindow_FiltroStradario"))
                                    { Patterns = new[] { "*.stradario" } }
                            }
                        });
                    if (file != null)
                    {
                        _currentFilePath = file.Path.LocalPath;
                        await SaveCurrentProject(_currentFilePath);
                    }
                    else
                    {
                        return; // utente ha annullato il salvataggio: non uscire
                    }
                }
            }

            // Chiudi senza più chiedere
            Closing -= OnWindowClosing;
            Close();
        }

        private void InitializeView()
        {
            // Imposta la vista iniziale dal progetto
            _viewCenterLon = _project.ViewCenterLon;
            _viewCenterLat = _project.ViewCenterLat;
            _viewZoom      = _project.ViewZoom;

            ApplyGlobalPreferences();
            UpdateTitle();
            RefreshNavigationTree();
            UpdateStatusBarSummary();
        }

        // ---------------------------------------------------------------
        // Costruzione UI programmatica (senza AXAML per portabilità)
        // ---------------------------------------------------------------
        private void InitializeComponent()
        {
            Title  = Strings.Get("MainWindow_TitoloApp");
            Width  = 1200;
            Height = 800;
            MinWidth  = 800;
            MinHeight = 600;

            // Layout principale: toolbar in alto, poi split orizzontale, poi status bar
            var mainGrid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto")
            };

            // ---- Toolbar ----
            var toolbar = BuildToolbar();
            Grid.SetRow(toolbar, 0);
            mainGrid.Children.Add(toolbar);

            // ---- Contenuto principale: pannello sx + mappa ----
            var splitPanel = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("280,4,*")
            };

            // Pannello sinistro
            var leftPanel = BuildLeftPanel();
            Grid.SetColumn(leftPanel, 0);
            splitPanel.Children.Add(leftPanel);

            // Separatore
            var splitter = new GridSplitter
            {
                Background      = Brushes.Gray,
                ResizeDirection = GridResizeDirection.Columns
            };
            Grid.SetColumn(splitter, 1);
            splitPanel.Children.Add(splitter);

            // Canvas mappa: controllo custom con SkiaSharp via Avalonia.Skia
            _mapCanvas = new MapCanvas();
            _mapCanvas.PaintSkia            += OnPaintMapSurface;
            _mapCanvas.PointerPressed       += OnMapPointerPressed;
            _mapCanvas.PointerMoved         += OnMapPointerMoved;
            _mapCanvas.PointerReleased      += OnMapPointerReleased;
            _mapCanvas.PointerWheelChanged  += OnMapWheelChanged;
            _mapCanvas.PointerExited        += (_, _) => { if (_statusBarPositionText != null) _statusBarPositionText.Text = ""; };
            Grid.SetColumn(_mapCanvas, 2);
            splitPanel.Children.Add(_mapCanvas);

            Grid.SetRow(splitPanel, 1);
            mainGrid.Children.Add(splitPanel);

            // ---- Status bar ----
            var statusBar = BuildStatusBar();
            Grid.SetRow(statusBar, 2);
            mainGrid.Children.Add(statusBar);

            Content = mainGrid;
        }

        // ---- Barra di stato: riepilogo progetto a sinistra, messaggi
        // temporanei (esiti di importazioni/esportazioni/salvataggi ecc.)
        // a destra, al posto dei message-box con solo "OK" ----
        private Control BuildStatusBar()
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };

            _statusBarSummaryText = new TextBlock
            {
                FontSize   = 11,
                Foreground = Brushes.DimGray,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(_statusBarSummaryText, 0);
            grid.Children.Add(_statusBarSummaryText);

            _statusBarPositionText = new TextBlock
            {
                FontSize   = 11,
                Foreground = Brushes.DimGray,
                Margin     = new Thickness(12, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(_statusBarPositionText, 1);
            grid.Children.Add(_statusBarPositionText);

            // Notifica persistente (non temporizzata, a differenza di
            // _statusBarMessageText) di un nuovo aggiornamento disponibile,
            // popolata da CheckForUpdateOnStartupAsync: resta visibile finché
            // non si chiude il progetto o non si clicca per aprire la pagina
            // della release.
            _statusBarUpdateText = new TextBlock
            {
                FontSize   = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.DarkOrange,
                Margin     = new Thickness(0, 0, 12, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Cursor     = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                IsVisible  = false
            };
            _statusBarUpdateText.PointerPressed += (_, _) =>
            {
                if (string.IsNullOrEmpty(_availableUpdateUrl)) return;
                try { Process.Start(new ProcessStartInfo(_availableUpdateUrl) { UseShellExecute = true }); }
                catch { /* nessun browser disponibile */ }
            };
            Grid.SetColumn(_statusBarUpdateText, 2);
            grid.Children.Add(_statusBarUpdateText);

            _statusBarMessageText = new TextBlock
            {
                FontSize   = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.SeaGreen,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth   = 480
            };
            Grid.SetColumn(_statusBarMessageText, 3);
            grid.Children.Add(_statusBarMessageText);

            return new Border
            {
                Background      = new SolidColorBrush(Color.Parse("#F0F0F0")),
                BorderBrush     = Brushes.LightGray,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding         = new Thickness(10, 4),
                Child           = grid
            };
        }

        // Aggiorna il riepilogo permanente (a sinistra) con i conteggi correnti
        private void UpdateStatusBarSummary()
        {
            if (_statusBarSummaryText == null) return;

            int poiCount = _project.PoiGroups.Sum(g => g.Items.Count);
            string file  = _currentFilePath != null ? Path.GetFileName(_currentFilePath) : Strings.Get("MainWindow_SenzaTitolo");

            _statusBarSummaryText.Text = string.Format(
                Strings.Get("MainWindow_StatusBarRiepilogo"),
                _project.Pages.Count, poiCount, _project.PoiGroups.Count,
                _project.Percorsi.Count, _project.ProjectName, file, (_isDirty ? " •" : ""));
        }

        // Mostra un messaggio temporaneo nella barra di stato (esito di
        // un'importazione/esportazione/salvataggio ecc.), al posto di un
        // message-box con solo "OK": scompare da solo dopo qualche secondo
        private void ShowStatusMessage(string message, bool isError = false, double seconds = 5)
        {
            if (_statusBarMessageText == null) return;

            // Un messaggio su una sola riga: più leggibile nella barra di stato
            _statusBarMessageText.Text       = message.Replace('\n', ' ').Replace("  ", " ");
            _statusBarMessageText.Foreground = isError ? Brushes.Crimson : Brushes.SeaGreen;

            _statusMessageTimer?.Stop();
            _statusMessageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _statusMessageTimer.Tick += (_, _) =>
            {
                _statusMessageTimer?.Stop();
                if (_statusBarMessageText != null) _statusBarMessageText.Text = "";
            };
            _statusMessageTimer.Start();
        }

        // ---- Barra degli strumenti ----
        // Bottone icona per la toolbar, con un'icona vettoriale Bootstrap Icons
        // (path SVG in UI/BootstrapIcons.cs) invece di un'emoji: molto più
        // nitida a schermo e coerente indipendentemente dal font di sistema
        private Button MakeToolbarIcon(string svgPathData, string tooltip, EventHandler<RoutedEventArgs> handler)
        {
            var icon = new Avalonia.Controls.Shapes.Path
            {
                Data    = Geometry.Parse(svgPathData),
                Fill    = new SolidColorBrush(Color.Parse("#3A3A3A")),
                Width   = 17,
                Height  = 17,
                Stretch = Stretch.Uniform
            };
            var btn = new Button
            {
                Content    = icon,
                Width      = 34,
                Height     = 30,
                Padding    = new Thickness(0),
                Background = Brushes.Transparent,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment   = Avalonia.Layout.VerticalAlignment.Center,
                Cursor     = new Cursor(StandardCursorType.Hand)
            };
            ToolTip.SetTip(btn, tooltip);
            btn.Click += handler;
            return btn;
        }

        // Separatore di toolbar "classico": due sottili linee verticali
        // adiacenti (una scura e una chiara) che danno il tipico effetto a
        // scanalatura, invece della singola barra piatta di prima
        private static Control ToolbarSeparator()
        {
            var grid = new Grid
            {
                Margin = new Thickness(5, 3),
                Height = 22,
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto")
            };
            var dark  = new Border { Width = 1, Background = new SolidColorBrush(Color.Parse("#A8A8A8")) };
            var light = new Border { Width = 1, Background = Brushes.White };
            Grid.SetColumn(dark, 0);
            Grid.SetColumn(light, 1);
            grid.Children.Add(dark);
            grid.Children.Add(light);
            return grid;
        }

        // Aggiunge una piccola "✕" dentro il campo (Avalonia TextBox.InnerRightContent)
        // per svuotarlo rapidamente; onClear è per eventuale logica aggiuntiva
        // (es. uscire anche dalla modalità di ricerca)
        private static void AttachClearButton(TextBox tb, Action? onClear = null)
        {
            var clearBtn = new Button
            {
                Content         = "✕",
                Width           = 20,
                Height          = 20,
                Padding         = new Thickness(0),
                FontSize        = 11,
                Background      = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor          = new Cursor(StandardCursorType.Hand),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin          = new Thickness(0, 0, 2, 0)
            };
            ToolTip.SetTip(clearBtn, Strings.Get("MainWindow_Svuota"));
            clearBtn.Click += (_, _) =>
            {
                tb.Text = "";
                onClear?.Invoke();
            };
            tb.InnerRightContent = clearBtn;
        }

        // Toolbar unica su una sola riga, a icone (con tooltip) invece che a
        // bottoni testuali, con gruppi per argomento separati da separatori
        private Control BuildToolbar()
        {
            var toolbar = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing     = 2,
                Margin      = new Thickness(4, 2)
            };

            var recentBtn = MakeToolbarIcon(BootstrapIcons.Recent, Strings.Get("MainWindow_FileRecenti"), (_, _) => { });
            recentBtn.Click += (_, _) => ShowRecentFilesFlyout(recentBtn);

            toolbar.Children.Add(MakeToolbarIcon(BootstrapIcons.New, Strings.Get("MainWindow_NuovoProgetto"), OnNewProject));
            toolbar.Children.Add(MakeToolbarIcon(BootstrapIcons.Open, Strings.Get("MainWindow_ApriProgetto"), OnOpenProject));
            toolbar.Children.Add(recentBtn);
            toolbar.Children.Add(MakeToolbarIcon(BootstrapIcons.Save, Strings.Get("MainWindow_Salva"), OnSaveProject));
            toolbar.Children.Add(MakeToolbarIcon(BootstrapIcons.SaveAs, Strings.Get("MainWindow_SalvaCome"), OnSaveProjectAs));

            toolbar.Children.Add(ToolbarSeparator());
            toolbar.Children.Add(MakeToolbarIcon(BootstrapIcons.Import, Strings.Get("MainWindow_ImportaTooltip"), OnImportKmzUnified));
            toolbar.Children.Add(MakeToolbarIcon(BootstrapIcons.ExportPdf, Strings.Get("MainWindow_GeneraPdf"), OnGeneratePdf));

            toolbar.Children.Add(ToolbarSeparator());
            toolbar.Children.Add(MakeToolbarIcon(BootstrapIcons.Refresh, Strings.Get("MainWindow_AggiornaMappa"), OnRefreshMap));
            toolbar.Children.Add(MakeToolbarIcon(BootstrapIcons.Undo, Strings.Get("MainWindow_AnnullaCtrlZ"), (_, _) => Undo()));
            toolbar.Children.Add(MakeToolbarIcon(BootstrapIcons.Redo, Strings.Get("MainWindow_RipetiCtrlY"), (_, _) => Redo()));
            toolbar.Children.Add(MakeToolbarIcon(BootstrapIcons.Ruler, Strings.Get("MainWindow_RighelloTooltip"), OnToggleRuler));
            toolbar.Children.Add(MakeToolbarIcon(BootstrapIcons.WhatsHere, Strings.Get("MainWindow_CosaCeQuiTooltip"), OnToggleIdentifyMode));
            toolbar.Children.Add(MakeToolbarIcon(BootstrapIcons.Locate, Strings.Get("MainWindow_LocalizzaTooltip"), OnToggleMyLocation));

            toolbar.Children.Add(ToolbarSeparator());

            // Il campo di ricerca e il combo categoria restano nascosti finché
            // non si preme la lente: primo clic = mostra entrambi e mette il
            // focus sul testo; secondo clic (testo non vuoto O una categoria
            // selezionata) = lancia la ricerca, come premere Invio. Scegliere
            // una categoria dal combo NON lancia nulla da sola: serve comunque
            // Invio (o un secondo clic sulla lente), esattamente come il testo.
            _poiSearchTextBox = new TextBox
            {
                Width       = 220,
                Watermark   = Strings.Get("MainWindow_TestoLiberoWatermark"),
                FontSize    = 12,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                IsVisible   = false
            };
            _poiSearchTextBox.KeyDown += async (s, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter) await OnPoiSearchAsync();
                else if (e.Key == Avalonia.Input.Key.Escape) HidePoiSearchBox();
            };
            AttachClearButton(_poiSearchTextBox, onClear: () =>
            {
                if (_poiSearchMode) CancelAllAddModes();
                HidePoiSearchBox();
                _mapCanvas?.InvalidateVisual();
            });
            toolbar.Children.Add(_poiSearchTextBox);
            toolbar.Children.Add(MakeToolbarIcon(BootstrapIcons.Search, Strings.Get("MainWindow_CercaPoiTooltip"),
                async (s, e) =>
                {
                    if (_poiSearchTextBox == null || _categoryFilterComboBox == null) return;
                    if (!_poiSearchTextBox.IsVisible)
                    {
                        _poiSearchTextBox.IsVisible       = true;
                        _categoryFilterComboBox.IsVisible = true;
                        // Propone l'ultima ricerca fatta (pronta per essere ripetuta
                        // con Invio, o sovrascritta semplicemente digitando)
                        if (string.IsNullOrEmpty(_poiSearchTextBox.Text) && !string.IsNullOrWhiteSpace(_lastPoiSearchQuery))
                        {
                            _poiSearchTextBox.Text = _lastPoiSearchQuery;
                            _poiSearchTextBox.SelectAll();
                        }
                        _poiSearchTextBox.Focus();
                    }
                    else
                    {
                        // Il combo ha sempre una categoria selezionata: un
                        // secondo click sulla lente cerca sempre (con o
                        // senza testo di raffinamento), non serve più
                        // decidere tra "cerca" e "nascondi" in base a cosa è
                        // stato scelto/digitato.
                        await OnPoiSearchAsync();
                    }
                }));

            // Combo categoria/tag OSM (vedi PoiSearchService.AllCategories):
            // appare a destra della lente insieme alla casella di testo.
            // Sempre valorizzato (nessuna voce "qualsiasi categoria": la
            // categoria si sceglie SOLO da qui, mai da testo libero — vedi
            // RunCategorySearchAsync). Default: l'ultima categoria
            // effettivamente usata in una ricerca (persistita tra sessioni,
            // vedi AppPreferencesService.LoadLastPoiCategory), altrimenti
            // "ristoranti" la primissima volta.
            var categoryLabels = PoiSearchService.AllCategories.Select(c => c.Label).ToList();
            int defaultCategoryIndex = 0;
            var lastCategory = _appPrefsSvc.LoadLastPoiCategory();
            if (lastCategory != null)
            {
                int idx = PoiSearchService.AllCategories.ToList()
                    .FindIndex(c => c.Key == lastCategory.Value.Key && c.Value == lastCategory.Value.Value);
                if (idx >= 0) defaultCategoryIndex = idx;
            }
            else
            {
                // Confronto sul tag OSM invariante (amenity=restaurant), non
                // sull'etichetta tradotta: "ristoranti" è solo il testo
                // mostrato e cambia con la lingua della UI.
                int idx = PoiSearchService.AllCategories.ToList()
                    .FindIndex(c => c.Key == "amenity" && c.Value == "restaurant");
                if (idx >= 0) defaultCategoryIndex = idx;
            }
            _categoryFilterComboBox = new ComboBox
            {
                Width         = 170,
                FontSize      = 12,
                ItemsSource   = categoryLabels,
                SelectedIndex = defaultCategoryIndex,
                IsVisible     = false
            };
            // Le due voci speciali in testa (indirizzo/città) richiedono
            // sempre del testo, a differenza delle categorie vere (dove il
            // testo è un filtro opzionale sul nome): aggiorna il watermark
            // per non far credere che si possa cercare a vuoto anche lì.
            _categoryFilterComboBox.SelectionChanged += (s, e) => UpdatePoiSearchWatermark();
            toolbar.Children.Add(_categoryFilterComboBox);
            UpdatePoiSearchWatermark();

            toolbar.Children.Add(ToolbarSeparator());
            toolbar.Children.Add(MakeToolbarIcon(BootstrapIcons.Settings, Strings.Get("MainWindow_ImpostazioniTooltip"), OnOpenSettings));
            toolbar.Children.Add(MakeToolbarIcon(BootstrapIcons.InfoCircle, Strings.Get("MainWindow_InfoTooltip"), OnOpenAbout));

            return new Border
            {
                Background = new SolidColorBrush(Color.Parse("#F0F0F0")),
                BorderBrush     = Brushes.LightGray,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child           = toolbar
            };
        }

        // ---- Pannello sinistro: impostazioni + lista pagine ----
        private Control BuildLeftPanel()
        {
            var panel = new DockPanel { LastChildFill = true };

            // Info impostazioni in alto
            var settingsInfo = new StackPanel { Margin = new Thickness(8), Spacing = 3 };
            settingsInfo.Children.Add(new TextBlock
            {
                Text     = Strings.Get("MainWindow_ImpostazioniCorrenti"),
                FontWeight = FontWeight.Bold,
                Margin   = new Thickness(0, 0, 0, 4)
            });
            settingsInfo.Children.Add(BuildSettingsInfoBlock());
            DockPanel.SetDock(settingsInfo, Dock.Top);
            panel.Children.Add(settingsInfo);

            // Titolo albero di navigazione
            var listHeader = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Margin      = new Thickness(8, 4, 8, 2),
                Spacing     = 8
            };
            listHeader.Children.Add(new TextBlock
            {
                Text       = Strings.Get("MainWindow_Navigazione"),
                FontWeight = FontWeight.Bold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            DockPanel.SetDock(listHeader, Dock.Top);
            panel.Children.Add(listHeader);

            // Campo di ricerca/filtro: filtra pagine/POI/percorsi per etichetta in
            // tempo reale (rimane fuori dall'albero ricostruito da
            // RefreshNavigationTree, così non perde il focus mentre si digita)
            _navFilterBox = new TextBox
            {
                Watermark = Strings.Get("MainWindow_FiltraPerEtichetta"),
                Margin    = new Thickness(8, 0, 8, 4),
                FontSize  = 12
            };
            _navFilterBox.TextChanged += (s, e) =>
            {
                _navFilterText = _navFilterBox!.Text ?? "";
                RefreshNavigationTree();
            };
            AttachClearButton(_navFilterBox);
            DockPanel.SetDock(_navFilterBox, Dock.Top);
            panel.Children.Add(_navFilterBox);

            // Albero: Pagine / Gruppi POI (scrollabile)
            var scroll = new ScrollViewer
            {
                Name    = "PageListScroll",
                Content = BuildNavigationTree()
            };
            panel.Children.Add(scroll);

            return new Border
            {
                BorderBrush     = Brushes.LightGray,
                BorderThickness = new Thickness(0, 0, 1, 0),
                Child           = panel
            };
        }

        // Blocco informativo impostazioni correnti
        private Control BuildSettingsInfoBlock()
        {
            var s    = _project.Settings;
            string scaleStr = s.GetScaleLabel();
            string pageStr  = $"{s.PageSize} {s.Orientation} @ {s.Dpi} DPI";

            var info = new StackPanel { Spacing = 2 };
            info.Children.Add(MakeInfoRow(Strings.Get("MainWindow_InfoFormato"), pageStr));
            info.Children.Add(MakeInfoRow(Strings.Get("MainWindow_InfoScala"), scaleStr));
            info.Children.Add(MakeInfoRow(Strings.Get("MainWindow_InfoCopertura"), $"{s.GetPageWidthKm():F1} × {s.GetPageHeightKm():F1} km"));
            return info;
        }

        private Control MakeInfoRow(string label, string value)
        {
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, FontSize = 11 });
            row.Children.Add(new TextBlock { Text = value, FontSize = 11 });
            return row;
        }

        // Costruisce l'albero di navigazione: ramo "Pagine" e ramo "Gruppi POI"
        // (con i singoli POI come foglie). Cliccando su una foglia (pagina o
        // POI) la mappa si centra sulle sue coordinate.
        private Control BuildNavigationTree()
        {
            var root = new StackPanel { Name = "NavTree", Spacing = 3, Margin = new Thickness(4) };

            string filter = (_navFilterText ?? "").Trim().ToLowerInvariant();
            bool   filtering = filter.Length > 0;
            bool Matches(string? s) => (s ?? "").ToLowerInvariant().Contains(filter);

            // ---- Ramo "Pagine" ----
            var visiblePages = _project.Pages.OrderBy(p => p.PageNumber)
                .Where(p => !filtering || Matches(p.Label) || Matches(p.Description))
                .ToList();

            bool allPagesLocked = _project.Pages.Count > 0 && _project.Pages.All(p => p.IsLocked);
            var pagesIcons = new List<Control>
            {
                DialogUi.MakeTreeIconButton(BootstrapIcons.Plus, Strings.Get("MainWindow_AggiungiPaginaTooltip"), Brushes.SteelBlue, () =>
                {
                    CancelAllAddModes();
                    _addPageMode = true;
                    _mapCanvas?.InvalidateVisual();
                })
            };
            if (_multiSelectedPageIds.Count > 0)
            {
                pagesIcons.Add(DialogUi.MakeTreeIconButton(BootstrapIcons.Trash,
                    string.Format(Strings.Get("MainWindow_EliminaPagineSelezionateTooltip"), _multiSelectedPageIds.Count),
                    Brushes.Crimson, async () => await DeleteSelectedPagesAsync()));
            }
            pagesIcons.AddRange(new List<Control>
            {
                DialogUi.MakeTreeIconButton(_pagesVisible ? BootstrapIcons.Eye : BootstrapIcons.EyeSlash,
                    _pagesVisible ? Strings.Get("MainWindow_NascondiPagineTooltip") : Strings.Get("MainWindow_MostraPagineTooltip"),
                    _pagesVisible ? Brushes.SteelBlue : Brushes.Gray, () =>
                {
                    _pagesVisible = !_pagesVisible;
                    RefreshNavigationTree();
                    _mapCanvas?.InvalidateVisual();
                }),
                DialogUi.MakeTreeIconButton(allPagesLocked ? BootstrapIcons.LockClosed : BootstrapIcons.LockOpen,
                    allPagesLocked ? Strings.Get("MainWindow_SbloccaTuttePagineTooltip") : Strings.Get("MainWindow_BloccaTuttePagineTooltip"),
                    allPagesLocked ? Brushes.Crimson : Brushes.SteelBlue, () =>
                {
                    bool lockAll = !allPagesLocked;
                    foreach (var p in _project.Pages)
                    {
                        p.IsLocked = lockAll;
                        if (!lockAll) TouchPage(p.Id);
                    }
                    _isDirty = true;
                    RefreshNavigationTree();
                })
            });
            root.Children.Add(BuildNavBranchHeader(Strings.Get("MainWindow_Pagine"), BootstrapIcons.Document, filtering ? visiblePages.Count : _project.Pages.Count,
                _navPagesExpanded, () => { _navPagesExpanded = !_navPagesExpanded; RefreshNavigationTree(); }, pagesIcons));

            if (_navPagesExpanded || filtering)
            {
                if (visiblePages.Count == 0)
                    root.Children.Add(Indent(EmptyHint(filtering ? Strings.Get("MainWindow_NessunaPaginaFiltro") : Strings.Get("MainWindow_NessunaPaginaVuota"))));
                foreach (var page in visiblePages)
                    root.Children.Add(Indent(BuildPageListItem(page)));
            }

            // ---- Ramo "Gruppi POI" ----
            var visibleGroups = _project.PoiGroups
                .Where(g => !filtering || Matches(g.Name) || g.Items.Any(it => Matches(it.Label)))
                .ToList();
            int visiblePoiCount = filtering
                ? visibleGroups.Sum(g => Matches(g.Name) ? g.Items.Count : g.Items.Count(it => Matches(it.Label)))
                : _project.PoiGroups.Sum(g => g.Items.Count);

            bool allPoiLocked = _project.PoiGroups.Count > 0 && _project.PoiGroups.All(g => g.IsLocked);
            var poiIcons = new List<Control>
            {
                DialogUi.MakeTreeIconButton(BootstrapIcons.Save, Strings.Get("MainWindow_EsportaGruppiPoiTooltip"), Brushes.SteelBlue, async () => await OnExportKmz()),
                DialogUi.MakeTreeIconButton(BootstrapIcons.Plus, Strings.Get("MainWindow_NuovoGruppoPoiTooltip"), Brushes.SteelBlue, async () => await OnNewPoiGroup())
            };
            if (_multiSelectedPoiKeys.Count > 0)
            {
                poiIcons.Add(DialogUi.MakeTreeIconButton(BootstrapIcons.Shuffle,
                    string.Format(Strings.Get("MainWindow_SpostaPoiSelezionatiTooltip"), _multiSelectedPoiKeys.Count),
                    Brushes.DarkOrange, async () => await MoveSelectedPoiToGroupAsync()));
            }
            poiIcons.AddRange(new List<Control>
            {
                DialogUi.MakeTreeIconButton(_poiVisible ? BootstrapIcons.Eye : BootstrapIcons.EyeSlash,
                    _poiVisible ? Strings.Get("MainWindow_NascondiPoiTooltip") : Strings.Get("MainWindow_MostraPoiTooltip"),
                    _poiVisible ? Brushes.SteelBlue : Brushes.Gray, () =>
                {
                    _poiVisible = !_poiVisible;
                    RefreshNavigationTree();
                    _mapCanvas?.InvalidateVisual();
                }),
                DialogUi.MakeTreeIconButton(allPoiLocked ? BootstrapIcons.LockClosed : BootstrapIcons.LockOpen,
                    allPoiLocked ? Strings.Get("MainWindow_SbloccaGruppiPoiTooltip") : Strings.Get("MainWindow_BloccaGruppiPoiTooltip"),
                    allPoiLocked ? Brushes.Crimson : Brushes.SteelBlue, () =>
                {
                    bool lockAll = !allPoiLocked;
                    foreach (var g in _project.PoiGroups)
                    {
                        g.IsLocked = lockAll;
                        if (!lockAll) TouchPoiGroup(g.Id);
                    }
                    _isDirty = true;
                    RefreshNavigationTree();
                })
            });
            root.Children.Add(BuildNavBranchHeader(Strings.Get("MainWindow_GruppiPoi"), BootstrapIcons.Locate, filtering ? visiblePoiCount : _project.PoiGroups.Sum(g => g.Items.Count),
                _navPoiExpanded, () => { _navPoiExpanded = !_navPoiExpanded; RefreshNavigationTree(); }, poiIcons));

            if (_navPoiExpanded || filtering)
            {
                if (visibleGroups.Count == 0)
                    root.Children.Add(Indent(EmptyHint(filtering ? Strings.Get("MainWindow_NessunGruppoPoiFiltro") : Strings.Get("MainWindow_NessunGruppoPoiVuoto"))));

                foreach (var group in visibleGroups)
                {
                    root.Children.Add(Indent(BuildPoiGroupNavHeader(group)));

                    bool groupNameMatches = filtering && Matches(group.Name);
                    bool groupExpanded = filtering || !_navCollapsedGroupIds.Contains(group.Id);
                    if (groupExpanded)
                    {
                        var visibleItems = (!filtering || groupNameMatches)
                            ? group.Items
                            : group.Items.Where(it => Matches(it.Label)).ToList();

                        if (visibleItems.Count == 0)
                            root.Children.Add(Indent(EmptyHint(Strings.Get("MainWindow_NessunPoiNelGruppo")), 28));
                        foreach (var item in visibleItems)
                            root.Children.Add(Indent(BuildPoiItemLeaf(group, item), 28));
                    }
                }
            }

            // ---- Ramo "Percorsi" ----
            var visibleRoutes = _project.Percorsi
                .Where(r => !filtering || Matches(r.Label))
                .ToList();

            bool allRoutesLocked = _project.Percorsi.Count > 0 && _project.Percorsi.All(r => r.IsLocked);
            var percorsiIcons = new List<Control>
            {
                DialogUi.MakeTreeIconButton(BootstrapIcons.Save, Strings.Get("MainWindow_EsportaPercorsiTooltip"), Brushes.SteelBlue, async () => await OnExportPercorsiKmz()),
                DialogUi.MakeTreeIconButton(BootstrapIcons.Plus, Strings.Get("MainWindow_NuovoPercorsoTooltip"), Brushes.SteelBlue, OnNewPercorso),
                DialogUi.MakeTreeIconButton(_percorsiVisible ? BootstrapIcons.Eye : BootstrapIcons.EyeSlash,
                    _percorsiVisible ? Strings.Get("MainWindow_NascondiPercorsiTooltip") : Strings.Get("MainWindow_MostraPercorsiTooltip"),
                    _percorsiVisible ? Brushes.SteelBlue : Brushes.Gray, () =>
                {
                    _percorsiVisible = !_percorsiVisible;
                    RefreshNavigationTree();
                    _mapCanvas?.InvalidateVisual();
                }),
                DialogUi.MakeTreeIconButton(allRoutesLocked ? BootstrapIcons.LockClosed : BootstrapIcons.LockOpen,
                    allRoutesLocked ? Strings.Get("MainWindow_SbloccaPercorsiTooltip") : Strings.Get("MainWindow_BloccaPercorsiTooltip"),
                    allRoutesLocked ? Brushes.Crimson : Brushes.SteelBlue, () =>
                {
                    bool lockAll = !allRoutesLocked;
                    foreach (var r in _project.Percorsi)
                    {
                        r.IsLocked = lockAll;
                        if (!lockAll) TouchPercorso(r.Id);
                    }
                    _isDirty = true;
                    RefreshNavigationTree();
                })
            };
            root.Children.Add(BuildNavBranchHeader(Strings.Get("MainWindow_Percorsi"), BootstrapIcons.Route, filtering ? visibleRoutes.Count : _project.Percorsi.Count,
                _navPercorsiExpanded, () => { _navPercorsiExpanded = !_navPercorsiExpanded; RefreshNavigationTree(); }, percorsiIcons));

            if (_navPercorsiExpanded || filtering)
            {
                if (visibleRoutes.Count == 0)
                    root.Children.Add(Indent(EmptyHint(filtering ? Strings.Get("MainWindow_NessunPercorsoFiltro") : Strings.Get("MainWindow_NessunPercorsoVuoto"))));

                foreach (var route in visibleRoutes)
                    root.Children.Add(Indent(BuildPercorsoNavItem(route)));
            }

            return root;
        }

        private static Control EmptyHint(string text) => new TextBlock
        {
            Text         = text,
            FontSize     = 11,
            Foreground   = Brushes.Gray,
            Margin       = new Thickness(4, 3),
            TextWrapping = TextWrapping.Wrap
        };

        // Intestazione di un ramo principale dell'albero (Pagine / Gruppi POI):
        // freccia espandi/collassa, titolo, contatore e icone di azione (sempre visibili)
        private Control BuildNavBranchHeader(string title, string titleIcon, int count, bool expanded, Action onToggleExpand, IReadOnlyList<Control> actionIcons)
        {
            var border = new Border
            {
                Background      = new SolidColorBrush(Color.Parse("#E4ECF5")),
                BorderBrush     = Brushes.LightGray,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(8, 6),
                Margin          = new Thickness(0, 3)
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

            var expandGlyph = new TextBlock
            {
                Text       = expanded ? "▾" : "▸",
                Width      = 16,
                FontWeight = FontWeight.Bold,
                Cursor     = new Cursor(StandardCursorType.Hand),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            row.Children.Add(expandGlyph);

            var labelPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing     = 6,
                Cursor      = new Cursor(StandardCursorType.Hand),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            labelPanel.Children.Add(DialogUi.MakeIconGlyph(titleIcon, Brushes.SteelBlue, 14));
            labelPanel.Children.Add(new TextBlock
            {
                Text         = $"{count}  {title}",
                FontWeight   = FontWeight.Bold,
                FontSize     = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            Grid.SetColumn(labelPanel, 1);
            row.Children.Add(labelPanel);

            var rightZone = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing     = 4,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            foreach (var icon in actionIcons)
                rightZone.Children.Add(icon);
            Grid.SetColumn(rightZone, 2);
            row.Children.Add(rightZone);

            border.Child = row;

            // Il click espande/collassa solo se non è su una delle icone di azione
            void ToggleHandler(object? s, PointerPressedEventArgs e)
            {
                if (e.Source is Button || (e.Source is Control c && c.FindAncestorOfType<Button>() != null)) return;
                onToggleExpand();
            }
            border.PointerPressed  += ToggleHandler;

            return border;
        }

        // Intestazione di un gruppo POI (ramo interno, espandibile): icona/colore
        // del gruppo, conteggio, e icone di azione (mostra/nascondi, aggiungi POI,
        // modifica, elimina)
        private Control BuildPoiGroupNavHeader(PoiGroup group)
        {
            bool expanded = !_navCollapsedGroupIds.Contains(group.Id);
            bool visible  = !_hiddenPoiGroupIds.Contains(group.Id);

            var border = new Border
            {
                Background      = new SolidColorBrush(Color.Parse("#EEF3FA")),
                BorderBrush     = Brushes.Gainsboro,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(6, 5),
                Margin          = new Thickness(0, 2)
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };

            row.Children.Add(new TextBlock
            {
                Text       = expanded ? "▾" : "▸",
                Width      = 14,
                FontWeight = FontWeight.Bold,
                Cursor     = new Cursor(StandardCursorType.Hand),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });

            var iconImg = new Image
            {
                Width   = 20,
                Height  = 20,
                Margin  = new Thickness(4, 0, 6, 0),
                Stretch = Stretch.Uniform,
                Cursor  = new Cursor(StandardCursorType.Hand)
            };
            using (var bmp = PoiIconRenderer.RenderToBitmap(group.Icon, PoiIconRenderer.ParseColor(group.ColorHex), 32))
                iconImg.Source = SkiaImageHelper.ToAvaloniaBitmap(bmp);
            Grid.SetColumn(iconImg, 1);
            row.Children.Add(iconImg);

            var label = new TextBlock
            {
                Text         = $"{group.Items.Count}  {group.Name}",
                FontSize     = 12,
                FontWeight   = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Cursor       = new Cursor(StandardCursorType.Hand),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(label, 2);
            row.Children.Add(label);

            var actions = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing     = 4,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            actions.Children.Add(DialogUi.MakeTreeIconButton(BootstrapIcons.Plus, Strings.Get("MainWindow_AggiungiPoiAlGruppoTooltip"), Brushes.SteelBlue,
                () => StartAddPoiMode(group)));
            actions.Children.Add(DialogUi.MakeTreeIconButton(BootstrapIcons.Pencil, Strings.Get("MainWindow_ModificaGruppoTooltip"), Brushes.SteelBlue,
                async () => await OnEditPoiGroup(group)));
            actions.Children.Add(DialogUi.MakeTreeIconButton(BootstrapIcons.Close, Strings.Get("MainWindow_EliminaGruppoTooltip"), Brushes.Crimson,
                () => OnDeletePoiGroup(group)));
            actions.Children.Add(DialogUi.MakeTreeIconButton(visible ? BootstrapIcons.Eye : BootstrapIcons.EyeSlash,
                visible ? Strings.Get("MainWindow_NascondiGruppoTooltip") : Strings.Get("MainWindow_MostraGruppoTooltip"),
                visible ? Brushes.SteelBlue : Brushes.Gray, () =>
            {
                if (visible) _hiddenPoiGroupIds.Add(group.Id);
                else         _hiddenPoiGroupIds.Remove(group.Id);
                RefreshNavigationTree();
                _mapCanvas?.InvalidateVisual();
            }));
            actions.Children.Add(DialogUi.MakeTreeIconButton(group.IsLocked ? BootstrapIcons.LockClosed : BootstrapIcons.LockOpen,
                group.IsLocked ? Strings.Get("MainWindow_SbloccaGruppoTooltip") : Strings.Get("MainWindow_BloccaGruppoTooltip"),
                group.IsLocked ? Brushes.Crimson : Brushes.SteelBlue, () =>
            {
                group.IsLocked = !group.IsLocked;
                if (!group.IsLocked) TouchPoiGroup(group.Id);
                _isDirty = true;
                RefreshNavigationTree();
            }));
            Grid.SetColumn(actions, 3);
            row.Children.Add(actions);

            border.Child = row;

            void ToggleHandler(object? s, PointerPressedEventArgs e)
            {
                if (e.Source is Button || (e.Source is Control c && c.FindAncestorOfType<Button>() != null)) return;
                if (expanded) _navCollapsedGroupIds.Add(group.Id);
                else          _navCollapsedGroupIds.Remove(group.Id);
                RefreshNavigationTree();
            }
            border.PointerPressed += ToggleHandler;

            return border;
        }

        // Foglia di un POI: cliccando si centra la mappa sulle sue coordinate;
        // icone di modifica ed eliminazione
        private Control BuildPoiItemLeaf(PoiGroup group, PoiItem item)
        {
            bool isMultiSelected = _multiSelectedPoiKeys.Contains((group.Id, item.Id));

            var border = new Border
            {
                Background      = isMultiSelected ? new SolidColorBrush(Color.Parse("#FFE0B2")) : Brushes.White,
                BorderBrush     = isMultiSelected ? Brushes.DarkOrange : Brushes.Gainsboro,
                BorderThickness = new Thickness(isMultiSelected ? 2 : 1),
                CornerRadius    = new CornerRadius(3),
                Padding         = new Thickness(6, 4),
                Margin          = new Thickness(0, 1),
                Cursor          = new Cursor(StandardCursorType.Hand)
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

            var info = new StackPanel { Spacing = 1 };
            info.Children.Add(new TextBlock { Text = item.Label, FontSize = 12 });
            info.Children.Add(new TextBlock
            {
                Text       = $"{item.Lon:F4}°E, {item.Lat:F4}°N",
                FontSize   = 10,
                Foreground = Brushes.DimGray
            });
            Grid.SetColumn(info, 0);
            row.Children.Add(info);

            var editBtn = DialogUi.MakeTreeIconButton(BootstrapIcons.Pencil, Strings.Get("MainWindow_ModificaPoiTooltip"), Brushes.SteelBlue,
                async () => await OnEditPoiItem(group, item));
            Grid.SetColumn(editBtn, 1);
            row.Children.Add(editBtn);

            var delBtn = DialogUi.MakeTreeIconButton(BootstrapIcons.Close, Strings.Get("MainWindow_EliminaPoiTooltip"), Brushes.Crimson,
                () => OnDeletePoiItem(group, item));
            Grid.SetColumn(delBtn, 2);
            row.Children.Add(delBtn);

            border.Child = row;

            // Click singolo: centra la mappa sul POI. Ctrl+clic: aggiunge/rimuove
            // il POI dalla selezione multipla (per spostarli in un altro gruppo)
            border.PointerPressed += (s, e) =>
            {
                if (e.Source is Button || (e.Source is Control c && c.FindAncestorOfType<Button>() != null)) return;

                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    var key = (group.Id, item.Id);
                    if (!_multiSelectedPoiKeys.Remove(key))
                        _multiSelectedPoiKeys.Add(key);
                    RefreshNavigationTree();
                    return;
                }

                _viewCenterLon = item.Lon;
                _viewCenterLat = item.Lat;
                _mapCanvas?.InvalidateVisual();
            };

            return border;
        }

        // Voce di un percorso nell'albero: swatch colore, etichetta, lunghezza
        // e numero di punti; cliccando (fuori dai bottoni) si centra la mappa
        // sul primo punto del percorso. Icone: mostra/nascondi, modifica, elimina.
        private Control BuildPercorsoNavItem(Percorso route)
        {
            bool visible = !_hiddenPercorsoIds.Contains(route.Id);

            var border = new Border
            {
                Background      = Brushes.White,
                BorderBrush     = Brushes.Gainsboro,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                Padding         = new Thickness(6, 4),
                Margin          = new Thickness(0, 1),
                Cursor          = new Cursor(StandardCursorType.Hand)
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto,Auto") };

            var swatch = new Border
            {
                Width        = 14,
                Height       = 14,
                CornerRadius = new CornerRadius(3),
                Background   = new SolidColorBrush(Color.Parse(string.IsNullOrWhiteSpace(route.ColorHex) ? "#E53935" : route.ColorHex)),
                BorderBrush  = Brushes.DimGray,
                BorderThickness = new Thickness(1),
                Margin       = new Thickness(0, 0, 8, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(swatch, 0);
            row.Children.Add(swatch);

            var info = new StackPanel { Spacing = 1 };
            info.Children.Add(new TextBlock { Text = route.Label, FontSize = 12 });
            double lengthKm = PercorsoRenderer.LengthKm(route);
            info.Children.Add(new TextBlock
            {
                Text       = $"{lengthKm:0.##} km  ·  {route.Points.Count} punti",
                FontSize   = 10,
                Foreground = Brushes.DimGray
            });
            Grid.SetColumn(info, 1);
            row.Children.Add(info);

            var addPtsBtn = DialogUi.MakeTreeIconButton(BootstrapIcons.Plus, Strings.Get("MainWindow_AggiungiPuntiPercorsoTooltip"), Brushes.SteelBlue,
                () => StartAddRoutePointsMode(route));
            Grid.SetColumn(addPtsBtn, 2);
            row.Children.Add(addPtsBtn);

            var editBtn = DialogUi.MakeTreeIconButton(BootstrapIcons.Pencil, Strings.Get("MainWindow_ModificaPercorsoTooltip"), Brushes.SteelBlue,
                async () => await OnEditPercorso(route));
            Grid.SetColumn(editBtn, 3);
            row.Children.Add(editBtn);

            var delBtn = DialogUi.MakeTreeIconButton(BootstrapIcons.Close, Strings.Get("MainWindow_EliminaPercorsoTooltip"), Brushes.Crimson,
                () => OnDeletePercorso(route));
            Grid.SetColumn(delBtn, 4);
            row.Children.Add(delBtn);

            var eyeBtn = DialogUi.MakeTreeIconButton(visible ? BootstrapIcons.Eye : BootstrapIcons.EyeSlash,
                visible ? Strings.Get("MainWindow_NascondiPercorsoTooltip") : Strings.Get("MainWindow_MostraPercorsoTooltip"),
                visible ? Brushes.SteelBlue : Brushes.Gray, () =>
            {
                if (visible) _hiddenPercorsoIds.Add(route.Id);
                else         _hiddenPercorsoIds.Remove(route.Id);
                RefreshNavigationTree();
                _mapCanvas?.InvalidateVisual();
            });
            Grid.SetColumn(eyeBtn, 5);
            row.Children.Add(eyeBtn);

            var lockBtn = DialogUi.MakeTreeIconButton(route.IsLocked ? BootstrapIcons.LockClosed : BootstrapIcons.LockOpen,
                route.IsLocked ? Strings.Get("MainWindow_SbloccaPercorsoTooltip") : Strings.Get("MainWindow_BloccaPercorsoTooltip"),
                route.IsLocked ? Brushes.Crimson : Brushes.SteelBlue, () =>
            {
                route.IsLocked = !route.IsLocked;
                if (!route.IsLocked) TouchPercorso(route.Id);
                _isDirty = true;
                RefreshNavigationTree();
            });
            Grid.SetColumn(lockBtn, 6);
            row.Children.Add(lockBtn);

            border.Child = row;

            border.PointerPressed += (s, e) =>
            {
                if (e.Source is Button || (e.Source is Control c && c.FindAncestorOfType<Button>() != null)) return;
                if (route.Points.Count == 0) return;
                _viewCenterLon = route.Points[0].Lon;
                _viewCenterLat = route.Points[0].Lat;
                _mapCanvas?.InvalidateVisual();
            };

            return border;
        }

        // Rientra visivamente un nodo dell'albero di un livello
        private static Control Indent(Control c, double left = 14) =>
            new Border { Margin = new Thickness(left, 0, 0, 0), Child = c };

        // Elemento singolo della lista pagine
        private Control BuildPageListItem(MapPage page)
        {
            bool isSelected      = page.Id == _selectedPageId;
            bool isMultiSelected = _multiSelectedPageIds.Contains(page.Id);

            var item = new Border
            {
                Name            = $"PageItem_{page.Id}",
                Background      = isMultiSelected
                    ? new SolidColorBrush(Color.Parse("#FFE0B2"))
                    : isSelected
                        ? new SolidColorBrush(Color.Parse("#CCE8FF"))
                        : new SolidColorBrush(Colors.White),
                BorderBrush     = isMultiSelected ? Brushes.DarkOrange : isSelected ? Brushes.SteelBlue : Brushes.LightGray,
                BorderThickness = new Thickness(isMultiSelected ? 2 : 1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(6, 5),
                Margin          = new Thickness(0, 1),
                Cursor          = new Cursor(StandardCursorType.Hand)
            };

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto")
            };

            // Info pagina
            var info = new StackPanel { Spacing = 1 };
            info.Children.Add(new TextBlock
            {
                Text       = $"{page.Label}",
                FontWeight = FontWeight.Bold,
                FontSize   = 12
            });
            info.Children.Add(new TextBlock
            {
                Text     = $"{page.GeoBounds.CenterLon:F4}°E, {page.GeoBounds.CenterLat:F4}°N",
                FontSize = 10,
                Foreground = Brushes.DimGray
            });
            if (!string.IsNullOrWhiteSpace(page.Description))
                info.Children.Add(new TextBlock
                {
                    Text     = page.Description,
                    FontSize = 10,
                    Foreground = Brushes.Gray
                });

            Grid.SetColumn(info, 0);
            row.Children.Add(info);

            // Pulsante modifica
            var editBtn = DialogUi.MakeTreeIconButton(BootstrapIcons.Pencil, Strings.Get("MainWindow_ModificaEtichettaDescrizioneTooltip"), Brushes.SteelBlue,
                async () => await EditPage(page));
            Grid.SetColumn(editBtn, 1);
            row.Children.Add(editBtn);

            // Pulsante elimina
            var delBtn = DialogUi.MakeTreeIconButton(BootstrapIcons.Close, Strings.Get("MainWindow_EliminaPaginaTooltip"), Brushes.Crimson,
                () => DeletePage(page.Id));
            Grid.SetColumn(delBtn, 2);
            row.Children.Add(delBtn);

            // Pulsante blocca/sblocca (impedisce il trascinamento accidentale)
            var lockBtn = DialogUi.MakeTreeIconButton(page.IsLocked ? BootstrapIcons.LockClosed : BootstrapIcons.LockOpen,
                page.IsLocked ? Strings.Get("MainWindow_SbloccaPaginaTooltip") : Strings.Get("MainWindow_BloccaPaginaTooltip"),
                page.IsLocked ? Brushes.Crimson : Brushes.SteelBlue, () =>
            {
                page.IsLocked = !page.IsLocked;
                if (!page.IsLocked) TouchPage(page.Id);
                _isDirty = true;
                RefreshNavigationTree();
            });
            Grid.SetColumn(lockBtn, 3);
            row.Children.Add(lockBtn);

            item.Child = row;

            // Click singolo: seleziona e centra (non su un'icona di azione).
            // Ctrl+clic: aggiunge/rimuove la pagina dalla selezione multipla
            // (per eliminazione in blocco) senza toccare vista/selezione singola
            item.PointerPressed += (s, e) =>
            {
                if (e.Source is Button || (e.Source is Control c && c.FindAncestorOfType<Button>() != null)) return;

                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    if (!_multiSelectedPageIds.Remove(page.Id))
                        _multiSelectedPageIds.Add(page.Id);
                    RefreshNavigationTree();
                    return;
                }

                _multiSelectedPageIds.Clear();
                _selectedPageId = page.Id;
                _viewCenterLon  = page.GeoBounds.CenterLon;
                _viewCenterLat  = page.GeoBounds.CenterLat;
                RefreshNavigationTree();
                _mapCanvas?.InvalidateVisual();
            };

            // Doppio click: apre il dialog di modifica
            item.DoubleTapped += async (s, e) => await EditPage(page);

            return item;
        }

        // ---------------------------------------------------------------
        // Rendering mappa
        // ---------------------------------------------------------------
        private void OnPaintMapSurface(object? sender, SkiaPaintEventArgs e)
        {
            float w = e.Width;
            float h = e.Height;

            var pagesForRender = _pagesVisible ? _project.Pages : new List<MapPage>();
            var poiForRender = _poiVisible
                ? _project.PoiGroups.Where(g => !_hiddenPoiGroupIds.Contains(g.Id)).ToList()
                : new List<PoiGroup>();
            var routesForRender = _percorsiVisible
                ? _project.Percorsi.Where(r => !_hiddenPercorsoIds.Contains(r.Id)).ToList()
                : new List<Percorso>();

            _renderer.RenderMap(
                e.Canvas, w, h,
                _viewCenterLon, _viewCenterLat,
                _viewZoom,
                _project.Settings.GetEffectiveTileServerUrl(),
                pagesForRender,
                _selectedPageId,
                onTileLoaded: () => Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => _mapCanvas?.InvalidateVisual()),
                poiGroups: poiForRender,
                routes: routesForRender,
                previewRoute: _addRouteMode ? _drawingRoute : null
            );

            // Overlay modalità aggiungi pagina
            if (_addPageMode)
                DrawOverlayHint(e.Canvas, Strings.Get("MainWindow_OverlayAggiungiPagina"), h);

            // Overlay modalità disegna percorso
            if (_addRouteMode)
                DrawOverlayHint(e.Canvas, Strings.Get("MainWindow_OverlayDisegnaPercorso"), h);

            // Overlay modalità aggiungi POI
            if (_addPoiMode)
                DrawOverlayHint(e.Canvas, Strings.Get("MainWindow_OverlayAggiungiPoi"), h);

            // Overlay modalità estendi percorso esistente
            if (_addRoutePointsMode)
                DrawOverlayHint(e.Canvas, Strings.Get("MainWindow_OverlayEstendiPercorso"), h);

            // Overlay modalità identifica ("cosa c'è qui")
            if (_identifyMode)
                DrawOverlayHint(e.Canvas, Strings.Get("MainWindow_OverlayIdentifica"), h);

            // Overlay modalità righello (misura distanza)
            if (_rulerMode)
            {
                using var linePaint = new SKPaint
                {
                    Color       = new SKColor(220, 20, 140),
                    StrokeWidth = 2.5f,
                    IsAntialias = true,
                    Style       = SKPaintStyle.Stroke,
                    PathEffect  = SKPathEffect.CreateDash(new float[] { 6, 4 }, 0)
                };
                using var dotFill = new SKPaint { Color = new SKColor(220, 20, 140), IsAntialias = true, Style = SKPaintStyle.Fill };
                using var dotHalo = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };

                var pts = _rulerPoints
                    .Select(p => GeoUtils.GeoToPixel(p.Lon, p.Lat, _viewCenterLon, _viewCenterLat, _viewZoom, w, h))
                    .ToList();
                for (int i = 0; i < pts.Count; i++)
                {
                    if (i > 0)
                        e.Canvas.DrawLine((float)pts[i - 1].x, (float)pts[i - 1].y, (float)pts[i].x, (float)pts[i].y, linePaint);
                    e.Canvas.DrawCircle((float)pts[i].x, (float)pts[i].y, 4, dotFill);
                    e.Canvas.DrawCircle((float)pts[i].x, (float)pts[i].y, 4, dotHalo);
                }

                double totalKm = 0;
                for (int i = 1; i < _rulerPoints.Count; i++)
                    totalKm += GeoUtils.DistanceKm(_rulerPoints[i - 1].Lon, _rulerPoints[i - 1].Lat, _rulerPoints[i].Lon, _rulerPoints[i].Lat);
                string distStr = totalKm >= 1 ? $"{totalKm:0.##} km" : $"{totalKm * 1000:0} m";

                DrawOverlayHint(e.Canvas,
                    _rulerPoints.Count == 0
                        ? Strings.Get("MainWindow_OverlayRighelloInizio")
                        : string.Format(Strings.Get("MainWindow_OverlayRighelloProsegui"), distStr, _rulerPoints.Count),
                    h);
            }

            // Overlay modalità ricerca POI online: marker candidati cliccabili
            if (_poiSearchMode && _poiSearchResults.Count > 0)
            {
                // Colore pallino: arancione di default (nessuna valutazione
                // AI, comportamento storico); quando l'AI ha assegnato una
                // confidence (vedi PoiSearchService.FilterAndScoreByQueryAsync)
                // il colore va invece dal rosso (poco affidabile) al verde
                // (molto affidabile) — l'utente sceglie a colpo d'occhio quali
                // marker valutare per primi, il dettaglio numerico e il
                // motivo restano comunque nel tooltip.
                using var fillPaint   = new SKPaint { Color = new SKColor(255, 140, 0), IsAntialias = true, Style = SKPaintStyle.Fill };
                using var borderPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
                using var textPaint   = new SKPaint { Color = SKColors.Black, TextSize = 11, IsAntialias = true };
                using var textHalo    = new SKPaint { Color = SKColors.White, TextSize = 11, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };

                foreach (var r in _poiSearchResults)
                {
                    var (px, py) = GeoUtils.GeoToPixel(r.Lon, r.Lat, _viewCenterLon, _viewCenterLat, _viewZoom, w, h);

                    fillPaint.Color = r.Confidence.HasValue ? ConfidenceColor(r.Confidence.Value) : new SKColor(255, 140, 0);
                    e.Canvas.DrawCircle((float)px, (float)py, 9, fillPaint);
                    e.Canvas.DrawCircle((float)px, (float)py, 9, borderPaint);

                    string label = SanitizeSearchLabel(r.DisplayName);
                    float tx = (float)px + 12, ty = (float)py + 4;
                    e.Canvas.DrawText(label, tx, ty, textHalo);
                    e.Canvas.DrawText(label, tx, ty, textPaint);
                }

                DrawOverlayHint(e.Canvas,
                    string.Format(Strings.Get("MainWindow_OverlayRisultatiRicerca"), _poiSearchResults.Count) +
                    (_poiSearchAreaClamped ? Strings.Get("MainWindow_OverlayAreaTroppoAmpia") : ""),
                    h);

                // Tooltip con più dettagli sul marker sotto il cursore
                if (_hoveredPoiSearchResult != null && _poiSearchResults.Contains(_hoveredPoiSearchResult))
                {
                    var hr = _hoveredPoiSearchResult;
                    var (hx, hy) = GeoUtils.GeoToPixel(hr.Lon, hr.Lat, _viewCenterLon, _viewCenterLat, _viewZoom, w, h);
                    DrawPoiSearchTooltip(e.Canvas, hr, (float)hx, (float)hy, w, h);
                }
            }

            // Tooltip su un POI già piazzato (fuori dalla modalità ricerca,
            // che ha il suo overlay sopra)
            if (_hoveredPoi != null)
            {
                var (hGroup, hItem) = _hoveredPoi.Value;
                var (hx, hy) = GeoUtils.GeoToPixel(hItem.Lon, hItem.Lat, _viewCenterLon, _viewCenterLat, _viewZoom, w, h);
                DrawPlacedPoiTooltip(e.Canvas, hGroup, hItem, (float)hx, (float)hy, w, h);
            }

            // Marker "dove sono": pallino blu con alone bianco, stile mappa
            // classico, più un cerchio tratteggiato per l'accuratezza (se nota)
            if (_myLocationActive && _myLocation != null)
                DrawMyLocationMarker(e.Canvas, _myLocation, _myLocationAccuracyM, w, h);
        }

        private void DrawMyLocationMarker(SKCanvas canvas, GeoPoint pos, double? accuracyMeters, float w, float h)
        {
            var (px, py) = GeoUtils.GeoToPixel(pos.Lon, pos.Lat, _viewCenterLon, _viewCenterLat, _viewZoom, w, h);
            float x = (float)px, y = (float)py;

            if (accuracyMeters is double acc && acc > 0)
            {
                // Raggio dell'accuratezza in pixel: converte metri in gradi di
                // longitudine alla latitudine corrente, poi in pixel schermo
                // con lo stesso zoom frazionario usato da GeoToPixel
                double metersPerDegLon = 111_320.0 * Math.Cos(pos.Lat * Math.PI / 180.0);
                double accDegLon       = metersPerDegLon > 1 ? acc / metersPerDegLon : 0;
                var (ex, _)            = GeoUtils.GeoToPixel(pos.Lon + accDegLon, pos.Lat, _viewCenterLon, _viewCenterLat, _viewZoom, w, h);
                float radiusPx         = Math.Max(0f, (float)Math.Abs(ex - px));

                if (radiusPx > 6)
                {
                    using var accFill = new SKPaint { Color = new SKColor(30, 144, 255, 40), IsAntialias = true, Style = SKPaintStyle.Fill };
                    using var accStroke = new SKPaint { Color = new SKColor(30, 144, 255, 120), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
                    canvas.DrawCircle(x, y, radiusPx, accFill);
                    canvas.DrawCircle(x, y, radiusPx, accStroke);
                }
            }

            using var halo = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };
            using var dot  = new SKPaint { Color = new SKColor(30, 144, 255), IsAntialias = true, Style = SKPaintStyle.Fill };
            using var ring = new SKPaint { Color = new SKColor(30, 144, 255), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f };

            canvas.DrawCircle(x, y, 9, halo);
            canvas.DrawCircle(x, y, 7, dot);
            canvas.DrawCircle(x, y, 9, ring);
        }

        // Riquadro informativo mostrato quando il cursore è sopra un marker
        // della ricerca POI online: nome, categoria OSM, indirizzo (se
        // disponibile) e coordinate — più dettagliato della sola etichetta
        // breve sempre visibile accanto al marker
        private void DrawPoiSearchTooltip(SKCanvas canvas, PoiSearchService.Result r, float markerX, float markerY, float canvasW, float canvasH)
        {
            var lines = new List<string> { SanitizeSearchLabel(r.DisplayName) };

            string? category = CombineCategory(r.Category, r.Type);
            if (!string.IsNullOrWhiteSpace(category)) lines.Add(category!);

            if (!string.IsNullOrWhiteSpace(r.Address))
                lines.AddRange(WrapText(r.Address!, 40));
            else
                lines.AddRange(WrapText(r.DisplayName, 40).Take(2));

            // Details è un'unica stringa "chiave=valore;chiave=valore;..."
            // (tutti i tag OSM grezzi dell'elemento, vedi
            // PoiSearchService.BuildOsmTagsString): una riga per ogni ";",
            // nessun campo prefissato da riconoscere.
            if (!string.IsNullOrWhiteSpace(r.Details))
                foreach (string detailLine in r.Details!.Split(';'))
                    lines.AddRange(WrapText(detailLine.Trim(), 40));

            if (r.Confidence.HasValue)
            {
                lines.Add(string.Format(Strings.Get("MainWindow_TooltipAffidabilita"), r.Confidence.Value));
                if (!string.IsNullOrWhiteSpace(r.Motivo))
                    lines.AddRange(WrapText(r.Motivo!, 40));
            }

            lines.Add($"{r.Lat:F5}°N, {r.Lon:F5}°E");

            DrawTooltipBox(canvas, lines, markerX, markerY, canvasW, canvasH, new SKColor(255, 140, 0));
        }

        // Tooltip sul marker di un POI già presente in un gruppo del progetto
        // (a differenza di DrawPoiSearchTooltip, non un risultato di ricerca
        // transitorio): nome, descrizione (multi-riga, spezzata su "\n" — sia
        // testo libero scritto dall'utente sia quella generata dalla ricerca
        // POI, vedi ConfirmPoiSearchResult) e coordinate. Bordo del colore
        // del gruppo, come i marker stessi sulla mappa.
        private void DrawPlacedPoiTooltip(SKCanvas canvas, PoiGroup group, PoiItem item, float markerX, float markerY, float canvasW, float canvasH)
        {
            var lines = new List<string> { item.Label };
            if (!string.IsNullOrWhiteSpace(item.Description))
                foreach (string descLine in item.Description.Split('\n'))
                    lines.AddRange(WrapText(descLine.Trim(), 40));
            lines.Add($"{item.Lat:F5}°N, {item.Lon:F5}°E");

            SKColor borderColor = SKColor.TryParse(group.ColorHex, out var c) ? c : new SKColor(30, 136, 229);
            DrawTooltipBox(canvas, lines, markerX, markerY, canvasW, canvasH, borderColor);
        }

        // Gradazione rosso (0, poco affidabile) → verde (100, molto
        // affidabile) per colorare i marker dei risultati valutati dall'AI
        // (Result.Confidence, vedi PoiSearchService.FilterAndScoreByQueryAsync).
        private static SKColor ConfidenceColor(int confidence)
        {
            double t = Math.Clamp(confidence, 0, 100) / 100.0;
            return new SKColor((byte)(255 * (1 - t)), (byte)(180 * t), 0);
        }

        // Riquadro bianco arrotondato con titolo (prima riga, in grassetto) +
        // righe di corpo, posizionato accanto al marker evitando di uscire
        // dal bordo del canvas: geometria condivisa fra tutti i tooltip della
        // mappa (ricerca POI, POI già piazzati...).
        private void DrawTooltipBox(SKCanvas canvas, List<string> lines, float markerX, float markerY, float canvasW, float canvasH, SKColor borderColor)
        {
            using var titleFont = SKTypeface.FromFamilyName(null, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            using var titlePaint = new SKPaint { TextSize = 13, IsAntialias = true, Typeface = titleFont, Color = SKColors.Black };
            using var bodyPaint  = new SKPaint { TextSize = 12, IsAntialias = true, Color = new SKColor(50, 50, 50) };
            using var bgPaint    = new SKPaint { Color = new SKColor(255, 255, 255, 240), IsAntialias = true };
            using var borderPnt  = new SKPaint { Color = borderColor, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };

            const float lineHeight = 16f;
            const float padding    = 8f;

            float maxWidth = 0;
            for (int i = 0; i < lines.Count; i++)
                maxWidth = Math.Max(maxWidth, (i == 0 ? titlePaint : bodyPaint).MeasureText(lines[i]));

            float boxWidth  = maxWidth + padding * 2;
            float boxHeight = lines.Count * lineHeight + padding * 2;

            float boxX = markerX + 14;
            float boxY = markerY - boxHeight - 10;
            if (boxX + boxWidth > canvasW) boxX = markerX - boxWidth - 14;
            if (boxY < 0) boxY = markerY + 14;
            if (boxY + boxHeight > canvasH) boxY = canvasH - boxHeight - 4;

            var rect = new SKRect(boxX, boxY, boxX + boxWidth, boxY + boxHeight);
            canvas.DrawRoundRect(rect, 6, 6, bgPaint);
            canvas.DrawRoundRect(rect, 6, 6, borderPnt);

            float ty = boxY + padding + 11;
            for (int i = 0; i < lines.Count; i++)
            {
                canvas.DrawText(lines[i], boxX + padding, ty, i == 0 ? titlePaint : bodyPaint);
                ty += lineHeight;
            }
        }

        // Combina i tag grezzi OSM class/type (es. "shop"/"butcher") in una
        // riga leggibile ("Butcher (shop)"); se ne manca uno usa solo l'altro
        private static string? CombineCategory(string? category, string? type)
        {
            string? prettyType = string.IsNullOrWhiteSpace(type) ? null : Prettify(type!);
            string? prettyCategory = string.IsNullOrWhiteSpace(category) ? null : category!.Replace('_', ' ');

            if (prettyType != null && prettyCategory != null && !string.Equals(prettyType, prettyCategory, StringComparison.OrdinalIgnoreCase))
                return $"{prettyType} ({prettyCategory})";
            return prettyType ?? prettyCategory;
        }

        private static string Prettify(string tag)
        {
            string s = tag.Replace('_', ' ');
            return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        // Spezza un testo in più righe di al più maxCharsPerLine caratteri,
        // andando a capo solo tra parole intere (word-wrap approssimativo,
        // sufficiente per il tooltip: la larghezza reale del riquadro è
        // comunque ricalcolata misurando le righe risultanti)
        private static List<string> WrapText(string text, int maxCharsPerLine)
        {
            var words = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var lines = new List<string>();
            var current = new System.Text.StringBuilder();
            foreach (var word in words)
            {
                if (current.Length > 0 && current.Length + 1 + word.Length > maxCharsPerLine)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }
                if (current.Length > 0) current.Append(' ');
                current.Append(word);
            }
            if (current.Length > 0) lines.Add(current.ToString());
            return lines;
        }

        // Disegna un testo informativo sovrapposto alla mappa (modalità attive,
        // esiti ricerca, ecc.) in modo leggibile su qualsiasi sfondo: rosso,
        // grassetto, con alone bianco di contorno
        private static void DrawOverlayHint(SKCanvas canvas, string text, float canvasHeight)
        {
            using var typeface = SKTypeface.FromFamilyName(null, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            using var halo = new SKPaint
            {
                Color       = SKColors.White,
                TextSize    = 15,
                IsAntialias = true,
                Style       = SKPaintStyle.Stroke,
                StrokeWidth = 4,
                Typeface    = typeface
            };
            using var fill = new SKPaint
            {
                Color       = new SKColor(200, 0, 0),
                TextSize    = 15,
                IsAntialias = true,
                Typeface    = typeface
            };
            canvas.DrawText(text, 10, canvasHeight - 12, halo);
            canvas.DrawText(text, 10, canvasHeight - 12, fill);
        }

        // ---------------------------------------------------------------
        // Interazione mouse sulla mappa
        // ---------------------------------------------------------------
        private void OnMapPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var props = e.GetCurrentPoint(_mapCanvas).Properties;
            var pos   = e.GetPosition(_mapCanvas);
            float cw  = (float)(_mapCanvas?.Bounds.Width  ?? 800);
            float ch  = (float)(_mapCanvas?.Bounds.Height ?? 600);

            if (props.IsLeftButtonPressed)
            {
                if (_identifyMode)
                {
                    var (lon, lat) = GeoUtils.PixelToGeo(pos.X, pos.Y,
                        _viewCenterLon, _viewCenterLat, _viewZoom, cw, ch);
                    _identifyMode = false;
                    _ = OnReverseGeocodeAsync(lon, lat);
                    return;
                }

                if (_rulerMode)
                {
                    var (lon, lat) = GeoUtils.PixelToGeo(pos.X, pos.Y,
                        _viewCenterLon, _viewCenterLat, _viewZoom, cw, ch);
                    _rulerPoints.Add(new GeoPoint { Lon = lon, Lat = lat });
                    _mapCanvas?.InvalidateVisual();
                    return;
                }

                if (_poiSearchMode)
                {
                    var hit = FindPoiSearchResultAtPoint(pos, cw, ch);
                    if (hit != null)
                    {
                        ConfirmPoiSearchResult(hit);
                        return;
                    }
                    // Nessun marker colpito: non blocca il pan della mappa (i
                    // marker sono ancorati alle coordinate geografiche, restano
                    // visibili anche spostando/zoomando la vista)
                }

                if (_addRouteMode && _drawingRoute != null)
                {
                    var (lon, lat) = GeoUtils.PixelToGeo(pos.X, pos.Y,
                        _viewCenterLon, _viewCenterLat, _viewZoom, cw, ch);
                    _drawingRoute.Points.Add(new GeoPoint { Lon = lon, Lat = lat });

                    // shift+clic = termina il disegno (invece del doppio clic)
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                        FinishRouteDrawing();
                    else
                        _mapCanvas?.InvalidateVisual();
                    return;
                }

                if (_addPageMode)
                {
                    var (lon, lat) = GeoUtils.PixelToGeo(pos.X, pos.Y,
                        _viewCenterLon, _viewCenterLat, _viewZoom, cw, ch);
                    AddPageAtLocation(lon, lat);
                    _addPageMode = false;
                    return;
                }

                if (_addPoiMode && _addPoiTargetGroup != null)
                {
                    var (lon, lat) = GeoUtils.PixelToGeo(pos.X, pos.Y,
                        _viewCenterLon, _viewCenterLat, _viewZoom, cw, ch);
                    var targetGroup = _addPoiTargetGroup;
                    _addPoiMode        = false;
                    _addPoiTargetGroup = null;
                    _mapCanvas?.InvalidateVisual();
                    AddPoiAtLocation(targetGroup, lon, lat);
                    return;
                }

                if (_addRoutePointsMode && _addRoutePointsTarget != null)
                {
                    var (lon, lat) = GeoUtils.PixelToGeo(pos.X, pos.Y,
                        _viewCenterLon, _viewCenterLat, _viewZoom, cw, ch);
                    var newPoint = new GeoPoint { Lon = lon, Lat = lat };

                    // Al primo punto della sessione, decide una volta sola
                    // quale estremità estendere in base a quella più vicina
                    if (_addRoutePointsSessionCount == 0 && _addRoutePointsTarget.Points.Count > 0)
                    {
                        var first = _addRoutePointsTarget.Points[0];
                        var last  = _addRoutePointsTarget.Points[^1];
                        double distToFirst = GeoUtils.DistanceKm(lon, lat, first.Lon, first.Lat);
                        double distToLast  = GeoUtils.DistanceKm(lon, lat, last.Lon, last.Lat);
                        _addRoutePointsPrepend = distToFirst < distToLast;
                    }

                    if (_addRoutePointsPrepend)
                        _addRoutePointsTarget.Points.Insert(0, newPoint);
                    else
                        _addRoutePointsTarget.Points.Add(newPoint);
                    _addRoutePointsSessionCount++;

                    TouchPercorso(_addRoutePointsTarget.Id);
                    _isDirty = true;
                    var routeExtended = _addRoutePointsTarget;
                    bool prepend       = _addRoutePointsPrepend;
                    PushUndo(
                        undo: () => routeExtended.Points.RemoveAt(prepend ? 0 : routeExtended.Points.Count - 1),
                        redo: () => { if (prepend) routeExtended.Points.Insert(0, newPoint); else routeExtended.Points.Add(newPoint); });

                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    {
                        _addRoutePointsMode         = false;
                        _addRoutePointsTarget       = null;
                        _addRoutePointsSessionCount = 0;
                        RefreshNavigationTree();
                    }
                    _mapCanvas?.InvalidateVisual();
                    return;
                }

                // Trascinamento diretto di un vertice di un percorso esistente
                var hitRoutePoint = FindRoutePointAtPoint(pos, cw, ch);
                if (hitRoutePoint != null)
                {
                    _isDraggingRoutePoint = true;
                    _draggingRoute        = hitRoutePoint.Value.route;
                    _draggingPointIndex   = hitRoutePoint.Value.index;
                    _routePointDragOrigLon = hitRoutePoint.Value.route.Points[hitRoutePoint.Value.index].Lon;
                    _routePointDragOrigLat = hitRoutePoint.Value.route.Points[hitRoutePoint.Value.index].Lat;
                    e.Pointer.Capture(_mapCanvas);
                    return;
                }

                // Trascinamento diretto di un POI esistente
                var hitPoi = FindPoiAtPoint(pos, cw, ch);
                if (hitPoi != null)
                {
                    _isDraggingPoi      = true;
                    _draggingPoiItem    = hitPoi.Value.item;
                    _draggingPoiGroupId = hitPoi.Value.group.Id;
                    _poiDragOrigLon     = hitPoi.Value.item.Lon;
                    _poiDragOrigLat     = hitPoi.Value.item.Lat;
                    e.Pointer.Capture(_mapCanvas);
                    return;
                }

                // Controlla se il click cade dentro la pagina selezionata
                var selPage = _selectedPageId.HasValue
                    ? _project.Pages.Find(p => p.Id == _selectedPageId.Value)
                    : null;

                if (selPage != null && !selPage.IsLocked && HitTestPage(selPage, pos, cw, ch))
                {
                    // Inizia drag della pagina
                    _isDraggingPage   = true;
                    _dragStart        = pos;
                    _pageDragStartLon = selPage.GeoBounds.CenterLon;
                    _pageDragStartLat = selPage.GeoBounds.CenterLat;
                    _pageDragOrigBounds = CloneRect(selPage.GeoBounds);
                    e.Pointer.Capture(_mapCanvas);
                }
                else
                {
                    // Inizia pan della mappa; se click su pagina non selezionata, selezionala
                    var hitPage = FindPageAtPoint(pos, cw, ch);
                    if (hitPage != null && hitPage.Id != _selectedPageId)
                    {
                        _selectedPageId = hitPage.Id;
                        RefreshNavigationTree();
                    }

                    _isDragging    = true;
                    _dragStart     = pos;
                    _dragCenterLon = _viewCenterLon;
                    _dragCenterLat = _viewCenterLat;
                    e.Pointer.Capture(_mapCanvas);
                }
            }
            else if (props.IsRightButtonPressed)
            {
                // Shift + tasto destro (con nessuna modalità attiva): scorciatoia
                // per la stessa ricerca inversa "cosa c'è in questo punto GPS"
                // attivabile anche dal bottone ❓📍 in toolbar (più scopribile)
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) &&
                    !_addPageMode && !_addRouteMode && !_addPoiMode && !_addRoutePointsMode && !_rulerMode && !_poiSearchMode && !_identifyMode)
                {
                    var (lon, lat) = GeoUtils.PixelToGeo(pos.X, pos.Y,
                        _viewCenterLon, _viewCenterLat, _viewZoom, cw, ch);
                    _ = OnReverseGeocodeAsync(lon, lat);
                    return;
                }

                if (_identifyMode)
                {
                    _identifyMode = false;
                    _mapCanvas?.InvalidateVisual();
                    return;
                }

                if (_rulerMode)
                {
                    if (_rulerPoints.Count > 0)
                        _rulerPoints.RemoveAt(_rulerPoints.Count - 1);
                    else
                        _rulerMode = false;
                    _mapCanvas?.InvalidateVisual();
                    return;
                }

                if (_poiSearchMode)
                {
                    _poiSearchMode    = false;
                    _poiSearchResults = new List<PoiSearchService.Result>();
                    _mapCanvas?.InvalidateVisual();
                    return;
                }

                if (_addRouteMode && _drawingRoute != null)
                {
                    if (_drawingRoute.Points.Count > 0)
                        _drawingRoute.Points.RemoveAt(_drawingRoute.Points.Count - 1);
                    else
                    {
                        _addRouteMode = false;
                        _drawingRoute = null;
                    }
                    _mapCanvas?.InvalidateVisual();
                    return;
                }

                if (_addPoiMode)
                {
                    _addPoiMode        = false;
                    _addPoiTargetGroup = null;
                    _mapCanvas?.InvalidateVisual();
                    return;
                }

                if (_addRoutePointsMode && _addRoutePointsTarget != null)
                {
                    if (_addRoutePointsSessionCount > 0)
                    {
                        if (_addRoutePointsPrepend)
                            _addRoutePointsTarget.Points.RemoveAt(0);
                        else
                            _addRoutePointsTarget.Points.RemoveAt(_addRoutePointsTarget.Points.Count - 1);
                        _addRoutePointsSessionCount--;
                    }
                    else
                    {
                        _addRoutePointsMode   = false;
                        _addRoutePointsTarget = null;
                    }
                    _mapCanvas?.InvalidateVisual();
                    return;
                }

                _addPageMode = false;
                _mapCanvas?.InvalidateVisual();
            }
        }

        // Termina il disegno del percorso in corso (invocato con shift+clic al
        // posto del doppio clic) e apre RouteEditWindow per etichetta/colore/descrizione
        private void FinishRouteDrawing()
        {
            if (_drawingRoute == null) return;

            var route = _drawingRoute;
            _addRouteMode = false;
            _drawingRoute = null;
            _mapCanvas?.InvalidateVisual();

            if (route.Points.Count < 2)
            {
                ShowStatusMessage(Strings.Get("MainWindow_PercorsoMinimoDuePuntiAnnullato"), isError: true);
                return;
            }

            route.Id    = _percorsoSvc.GetNextId(_project.Percorsi);
            route.Label = $"PATH{GetNextPercorsoLabelNumber()}";
            _project.Percorsi.Add(route);
            TouchPercorso(route.Id);
            _isDirty = true;
            PushUndo(
                undo: () => _project.Percorsi.Remove(route),
                redo: () => { if (!_project.Percorsi.Contains(route)) _project.Percorsi.Add(route); });
            RefreshNavigationTree();
            _mapCanvas?.InvalidateVisual();
        }

        // Trova il numero progressivo successivo per l'etichetta automatica
        // "PATH<n>" guardando il massimo già usato nel progetto
        private int GetNextPercorsoLabelNumber()
        {
            int max = 0;
            foreach (var r in _project.Percorsi)
            {
                var m = Regex.Match(r.Label ?? "", @"^PATH(\d+)$");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n > max)
                    max = n;
            }
            return max + 1;
        }

        private void OnMapPointerMoved(object? sender, PointerEventArgs e)
        {
            var pos  = e.GetPosition(_mapCanvas);
            float cw = (float)(_mapCanvas?.Bounds.Width  ?? 800);
            float ch = (float)(_mapCanvas?.Bounds.Height ?? 600);

            if (_statusBarPositionText != null)
            {
                var (cursorLon, cursorLat) = GeoUtils.PixelToGeo(pos.X, pos.Y,
                    _viewCenterLon, _viewCenterLat, _viewZoom, cw, ch);
                _statusBarPositionText.Text = $"🔍 {_viewZoom:0.#}   {cursorLon:F5}°E, {cursorLat:F5}°N";
            }

            // Hover su un marker della ricerca POI: mostra un riquadro con più
            // dettagli (indirizzo, categoria, coordinate) di quanto ci stia
            // nella piccola etichetta sempre visibile accanto al marker
            if (_poiSearchMode)
            {
                var hovered = FindPoiSearchResultAtPoint(pos, cw, ch);
                if (!Equals(hovered, _hoveredPoiSearchResult))
                {
                    _hoveredPoiSearchResult = hovered;
                    _mapCanvas?.InvalidateVisual();
                }
            }
            else if (_hoveredPoiSearchResult != null)
            {
                _hoveredPoiSearchResult = null;
            }

            // Stesso tooltip anche sui POI già piazzati (non solo sui
            // risultati di ricerca transitori): niente durante il drag di un
            // POI/vertice, per non sovrapporre un riquadro informativo a
            // quello che si sta trascinando.
            if (!_poiSearchMode && !_isDraggingPoi && !_isDraggingRoutePoint)
            {
                var hoveredPoi = FindAnyPoiAtPoint(pos, cw, ch);
                if (!Equals(hoveredPoi, _hoveredPoi))
                {
                    _hoveredPoi = hoveredPoi;
                    _mapCanvas?.InvalidateVisual();
                }
            }
            else if (_hoveredPoi != null)
            {
                _hoveredPoi = null;
            }

            if (_isDraggingRoutePoint && _draggingRoute != null)
            {
                var (lon, lat) = GeoUtils.PixelToGeo(pos.X, pos.Y,
                    _viewCenterLon, _viewCenterLat, _viewZoom, cw, ch);
                _draggingRoute.Points[_draggingPointIndex].Lon = lon;
                _draggingRoute.Points[_draggingPointIndex].Lat = lat;
                _mapCanvas?.InvalidateVisual();
                return;
            }

            if (_isDraggingPoi && _draggingPoiItem != null)
            {
                var (lon, lat) = GeoUtils.PixelToGeo(pos.X, pos.Y,
                    _viewCenterLon, _viewCenterLat, _viewZoom, cw, ch);
                _draggingPoiItem.Lon = lon;
                _draggingPoiItem.Lat = lat;
                _mapCanvas?.InvalidateVisual();
                return;
            }

            if (_isDraggingPage)
            {
                // Sposta la pagina selezionata seguendo il mouse
                var selPage = _selectedPageId.HasValue
                    ? _project.Pages.Find(p => p.Id == _selectedPageId.Value)
                    : null;
                if (selPage == null) return;

                // Calcola la nuova posizione geografica del centro
                var (newLon, newLat) = GeoUtils.PixelToGeo(pos.X, pos.Y,
                    _viewCenterLon, _viewCenterLat, _viewZoom, cw, ch);

                // Offset rispetto al punto di inizio drag (per spostamento relativo)
                var (startLon, startLat) = GeoUtils.PixelToGeo(_dragStart.X, _dragStart.Y,
                    _viewCenterLon, _viewCenterLat, _viewZoom, cw, ch);

                double dLon = newLon - startLon;
                double dLat = newLat - startLat;

                double centerLon = _pageDragStartLon + dLon;
                double centerLat = _pageDragStartLat + dLat;

                selPage.GeoBounds = GeoUtils.CalcPageBounds(centerLon, centerLat, _project.Settings);
                _mapCanvas?.InvalidateVisual();
                return;
            }

            if (!_isDragging) return;

            // Pan mappa
            double dx = pos.X - _dragStart.X;
            double dy = pos.Y - _dragStart.Y;

            double scale        = 256.0 * Math.Pow(2.0, _viewZoom);
            double pixPerDegLon = scale / 360.0;

            if (pixPerDegLon < 1e-10) return;

            _viewCenterLon = _dragCenterLon - dx / pixPerDegLon;
            // Per la lat usiamo la proiezione Mercatore inversa: newTileY è già
            // nell'unità di misura corretta ("tile units" a questo zoom) per
            // TileYToLat, va passato così com'è. Un precedente giro superfluo
            // "/ 2^zoom poi * 2^zoom" (numericamente un no-op) introduceva un
            // piccolo errore di arrotondamento ad ogni pan — visibile come un
            // impercettibile "micro-pan" residuo, soprattutto a zoom alti dove
            // 2^zoom è un numero grande.
            double centerTileY0 = GeoUtils.LatToTileY(_dragCenterLat, _viewZoom);
            double tilePixels   = 256.0 * Math.Pow(2.0, _viewZoom);
            double newTileY     = centerTileY0 - dy / tilePixels * Math.Pow(2.0, _viewZoom);
            _viewCenterLat = GeoUtils.TileYToLat(newTileY, _viewZoom);

            _viewCenterLon = Math.Max(-180, Math.Min(180, _viewCenterLon));
            _viewCenterLat = Math.Max(-85,  Math.Min(85,  _viewCenterLat));

            _mapCanvas?.InvalidateVisual();
        }

        private static GeoRect CloneRect(GeoRect r) => new GeoRect
        {
            MinLon = r.MinLon, MinLat = r.MinLat, MaxLon = r.MaxLon, MaxLat = r.MaxLat
        };

        private void OnMapPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_isDraggingRoutePoint)
            {
                var route = _draggingRoute;
                int idx   = _draggingPointIndex;
                if (route != null)
                {
                    TouchPercorso(route.Id);
                    double newLon = route.Points[idx].Lon, newLat = route.Points[idx].Lat;
                    double oldLon = _routePointDragOrigLon, oldLat = _routePointDragOrigLat;
                    if (Math.Abs(newLon - oldLon) > 1e-12 || Math.Abs(newLat - oldLat) > 1e-12)
                    {
                        PushUndo(
                            undo: () => { route.Points[idx].Lon = oldLon; route.Points[idx].Lat = oldLat; },
                            redo: () => { route.Points[idx].Lon = newLon; route.Points[idx].Lat = newLat; });
                    }
                }
                _isDraggingRoutePoint = false;
                _draggingRoute        = null;
                _draggingPointIndex   = -1;
                e.Pointer.Capture(null);
                _isDirty = true;
                RefreshNavigationTree();
                return;
            }
            if (_isDraggingPoi)
            {
                var item    = _draggingPoiItem;
                int groupId = _draggingPoiGroupId;
                if (item != null && groupId >= 0)
                {
                    TouchPoiGroup(groupId);
                    double newLon = item.Lon, newLat = item.Lat;
                    double oldLon = _poiDragOrigLon, oldLat = _poiDragOrigLat;
                    if (Math.Abs(newLon - oldLon) > 1e-12 || Math.Abs(newLat - oldLat) > 1e-12)
                    {
                        PushUndo(
                            undo: () => { item.Lon = oldLon; item.Lat = oldLat; },
                            redo: () => { item.Lon = newLon; item.Lat = newLat; });
                    }
                }
                _isDraggingPoi      = false;
                _draggingPoiItem    = null;
                _draggingPoiGroupId = -1;
                e.Pointer.Capture(null);
                _isDirty = true;
                RefreshNavigationTree();
                return;
            }
            if (_isDraggingPage)
            {
                _isDraggingPage = false;
                e.Pointer.Capture(null);
                var page = _selectedPageId.HasValue ? _project.Pages.Find(p => p.Id == _selectedPageId.Value) : null;
                if (page != null)
                {
                    TouchPage(page.Id);
                    var oldBounds = _pageDragOrigBounds;
                    var newBounds = CloneRect(page.GeoBounds);
                    if (oldBounds != null && (Math.Abs(oldBounds.MinLon - newBounds.MinLon) > 1e-12 || Math.Abs(oldBounds.MinLat - newBounds.MinLat) > 1e-12))
                    {
                        PushUndo(
                            undo: () => { page.GeoBounds = oldBounds; },
                            redo: () => { page.GeoBounds = newBounds; });
                    }
                }
                _pageDragOrigBounds = null;
                _isDirty = true;
                // Aggiorna la lista (le coordinate sono cambiate)
                RefreshNavigationTree();
                return;
            }
            if (_isDragging)
            {
                _isDragging = false;
                e.Pointer.Capture(null);
            }
        }

        private void OnMapWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            double delta = e.Delta.Y > 0 ? 0.5 : -0.5;
            double maxZoom = TileServers.GetMaxZoom(_project.Settings.TileServerUrl);
            _viewZoom = Math.Clamp(_viewZoom + delta, 1.0, maxZoom);
            if (_statusBarPositionText != null)
            {
                var pos = e.GetPosition(_mapCanvas);
                float cw = (float)(_mapCanvas?.Bounds.Width  ?? 800);
                float ch = (float)(_mapCanvas?.Bounds.Height ?? 600);
                var (cursorLon, cursorLat) = GeoUtils.PixelToGeo(pos.X, pos.Y,
                    _viewCenterLon, _viewCenterLat, _viewZoom, cw, ch);
                _statusBarPositionText.Text = $"🔍 {_viewZoom:0.#}   {cursorLon:F5}°E, {cursorLat:F5}°N";
            }
            _mapCanvas?.InvalidateVisual();
        }

        // Verifica se un punto pixel cade dentro il rettangolo di una pagina
        private bool HitTestPage(MapPage page, Point pt, float cw, float ch)
        {
            var (x1, y1) = GeoUtils.GeoToPixel(page.GeoBounds.MinLon, page.GeoBounds.MaxLat,
                _viewCenterLon, _viewCenterLat, _viewZoom, cw, ch);
            var (x2, y2) = GeoUtils.GeoToPixel(page.GeoBounds.MaxLon, page.GeoBounds.MinLat,
                _viewCenterLon, _viewCenterLat, _viewZoom, cw, ch);

            return pt.X >= x1 && pt.X <= x2 && pt.Y >= y1 && pt.Y <= y2;
        }

        // Trova la prima pagina il cui rettangolo contiene il punto pixel
        private MapPage? FindPageAtPoint(Point pt, float cw, float ch)
        {
            foreach (var p in _project.Pages)
                if (HitTestPage(p, pt, cw, ch))
                    return p;
            return null;
        }

        // Trova il POI più vicino al punto pixel (entro PoiHitRadiusPx), fra
        // i gruppi/POI attualmente visibili sulla mappa
        private (PoiGroup group, PoiItem item)? FindPoiAtPoint(Point pt, float cw, float ch)
        {
            if (!_poiVisible) return null;

            foreach (var group in _project.PoiGroups)
            {
                if (_hiddenPoiGroupIds.Contains(group.Id)) continue;
                if (group.IsLocked) continue;
                foreach (var item in group.Items)
                {
                    var (x, y) = GeoUtils.GeoToPixel(item.Lon, item.Lat,
                        _viewCenterLon, _viewCenterLat, _viewZoom, cw, ch);
                    double dx = pt.X - x, dy = pt.Y - y;
                    if (dx * dx + dy * dy <= PoiHitRadiusPx * PoiHitRadiusPx)
                        return (group, item);
                }
            }
            return null;
        }

        // Come FindPoiAtPoint, ma per il tooltip al passaggio del mouse (non
        // per il drag): include anche i gruppi bloccati, il blocco impedisce
        // solo di spostare accidentalmente il POI, non di vederne i dettagli.
        private (PoiGroup group, PoiItem item)? FindAnyPoiAtPoint(Point pt, float cw, float ch)
        {
            if (!_poiVisible) return null;

            foreach (var group in _project.PoiGroups)
            {
                if (_hiddenPoiGroupIds.Contains(group.Id)) continue;
                foreach (var item in group.Items)
                {
                    var (x, y) = GeoUtils.GeoToPixel(item.Lon, item.Lat,
                        _viewCenterLon, _viewCenterLat, _viewZoom, cw, ch);
                    double dx = pt.X - x, dy = pt.Y - y;
                    if (dx * dx + dy * dy <= PoiHitRadiusPx * PoiHitRadiusPx)
                        return (group, item);
                }
            }
            return null;
        }

        // Trova il vertice di percorso più vicino al punto pixel (entro
        // RoutePointHitRadiusPx), fra i percorsi attualmente visibili sulla mappa
        private (Percorso route, int index)? FindRoutePointAtPoint(Point pt, float cw, float ch)
        {
            if (!_percorsiVisible) return null;

            foreach (var route in _project.Percorsi)
            {
                if (_hiddenPercorsoIds.Contains(route.Id)) continue;
                if (route.IsLocked) continue;
                for (int i = 0; i < route.Points.Count; i++)
                {
                    var p = route.Points[i];
                    var (x, y) = GeoUtils.GeoToPixel(p.Lon, p.Lat,
                        _viewCenterLon, _viewCenterLat, _viewZoom, cw, ch);
                    double dx = pt.X - x, dy = pt.Y - y;
                    if (dx * dx + dy * dy <= RoutePointHitRadiusPx * RoutePointHitRadiusPx)
                        return (route, i);
                }
            }
            return null;
        }

        // Trova il risultato di ricerca POI online più vicino al punto pixel
        // (entro PoiHitRadiusPx), fra i marker candidati mostrati sulla mappa
        private PoiSearchService.Result? FindPoiSearchResultAtPoint(Point pt, float cw, float ch)
        {
            foreach (var r in _poiSearchResults)
            {
                var (x, y) = GeoUtils.GeoToPixel(r.Lon, r.Lat, _viewCenterLon, _viewCenterLat, _viewZoom, cw, ch);
                double dx = pt.X - x, dy = pt.Y - y;
                if (dx * dx + dy * dy <= PoiHitRadiusPx * PoiHitRadiusPx)
                    return r;
            }
            return null;
        }

        // ---------------------------------------------------------------
        // Gestione pagine
        // ---------------------------------------------------------------

        // Svuota la cache tile e forza il ridisegno completo della mappa
        private void OnRefreshMap(object? sender, RoutedEventArgs e)
        {
            _renderer.ClearCache();
            _mapCanvas?.InvalidateVisual();
        }

        // ---------------------------------------------------------------
        // Righello (misura distanza)
        // ---------------------------------------------------------------
        private void OnToggleRuler(object? sender, RoutedEventArgs e)
        {
            bool wasActive = _rulerMode;
            CancelAllAddModes();
            if (!wasActive) _rulerMode = true;
            _mapCanvas?.InvalidateVisual();
        }

        // Attiva/disattiva la modalità "identifica" (❓📍): stesso bottone
        // clic-poi-clic degli altri modi, ma per la ricerca inversa "cosa c'è
        // qui" invece che per aggiungere qualcosa — più scopribile della sola
        // scorciatoia shift + tasto destro
        private void OnToggleIdentifyMode(object? sender, RoutedEventArgs e)
        {
            bool wasActive = _identifyMode;
            CancelAllAddModes();
            if (!wasActive) _identifyMode = true;
            _mapCanvas?.InvalidateVisual();
        }

        // ---------------------------------------------------------------
        // Localizza dove sono
        // ---------------------------------------------------------------
        private void OnToggleMyLocation(object? sender, RoutedEventArgs e)
        {
            if (_myLocationActive)
            {
                _geoLocationSvc.Stop();
                _myLocationActive       = false;
                _myLocationCenteredOnce = false;
                _myLocation             = null;
                _myLocationAccuracyM    = null;
                _mapCanvas?.InvalidateVisual();
                return;
            }

            _myLocationActive       = true;
            _myLocationCenteredOnce = false;
            _myLocation             = null;
            _myLocationAccuracyM    = null;
            // Durata lunga: deve restare visibile finché non arriva l'esito
            // vero e proprio (OnMyLocationStarted/Updated/Error la sostituisce
            // comunque prima), non sparire nel mentre lasciando la status bar
            // vuota mentre si è ancora in attesa di una risposta
            ShowStatusMessage(Strings.Get("MainWindow_LocalizzazioneAvvio"), seconds: 60);
            _geoLocationSvc.Start();
        }

        // Chiamato dal thread di lettura in background di GeolocationService
        // non appena il processo esterno è partito ed è stato interpellato il
        // servizio di sistema (non ancora un fix: solo conferma che il primo
        // passo è avvenuto, utile perché l'attesa del fix vero e proprio può
        // durare qualche secondo)
        private void OnMyLocationStarted()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_myLocationActive) return;
                ShowStatusMessage(Strings.Get("MainWindow_LocalizzazioneServizioAvviato"), seconds: 60);
            });
        }

        // Chiamato dal thread di lettura in background di GeolocationService:
        // va rimarshallato sul thread UI prima di toccare stato/controlli
        private void OnMyLocationUpdated(GeoFix fix)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_myLocationActive) return;

                _myLocation          = new GeoPoint { Lon = fix.Lon, Lat = fix.Lat };
                _myLocationAccuracyM = fix.AccuracyMeters;

                if (!_myLocationCenteredOnce)
                {
                    _myLocationCenteredOnce = true;
                    _viewCenterLon = fix.Lon;
                    _viewCenterLat = fix.Lat;
                    ShowStatusMessage(Strings.Get("MainWindow_LocalizzazioneRiuscita"));
                }

                _mapCanvas?.InvalidateVisual();
            });
        }

        private void OnMyLocationError(string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ShowStatusMessage(string.Format(Strings.Get("MainWindow_LocalizzazioneFallita"), message), isError: true, seconds: 20);
                // Nessun fix ricevuto finora: disattiva il modo, l'utente può
                // riprovare col bottone. Un errore arrivato DOPO un fix valido
                // (es. il servizio si interrompe più tardi) lascia invece
                // l'ultimo marker visibile, non lo cancella.
                if (_myLocation == null)
                {
                    _myLocationActive = false;
                    _geoLocationSvc.Stop();
                }
                _mapCanvas?.InvalidateVisual();
            });
        }

        // ---------------------------------------------------------------
        // Ricerca POI online (Nominatim)
        // ---------------------------------------------------------------

        // Determina il gruppo POI di destinazione per la ricerca online, senza
        // mai chiedere all'utente: il primo gruppo NON bloccato (se ce n'è
        // più di uno), altrimenti il primo gruppo comunque, altrimenti ne
        // crea uno nuovo al volo (senza dialog) intitolato alla ricerca
        // effettuata (_poiSearchQueryLabel)
        private PoiGroup ResolvePoiSearchTargetGroup()
        {
            if (_project.PoiGroups.Count == 0)
                return CreateAutoPoiGroup(_poiSearchQueryLabel);
            return _project.PoiGroups.FirstOrDefault(g => !g.IsLocked) ?? _project.PoiGroups[0];
        }

        // Crea un gruppo POI senza dialog, con nome/icona/colore di default,
        // intitolato al testo passato (capitalizzato) — usato quando la
        // ricerca online non trova nessun gruppo esistente in cui inserire i risultati
        private PoiGroup CreateAutoPoiGroup(string name)
        {
            string trimmed = (name ?? "").Trim();
            string label = trimmed.Length == 0
                ? Strings.Get("MainWindow_RicercaLabelDefault")
                : char.ToUpperInvariant(trimmed[0]) + trimmed.Substring(1);

            var group = new PoiGroup { Id = _poiSvc.GetNextGroupId(_project.PoiGroups), Name = label };
            _project.PoiGroups.Add(group);
            _navCollapsedGroupIds.Remove(group.Id);
            TouchPoiGroup(group.Id);
            _isDirty = true;
            return group;
        }

        // Nasconde di nuovo il campo di ricerca POI in toolbar e ne svuota il
        // testo (non tocca eventuali risultati già mostrati sulla mappa). La
        // categoria selezionata NON viene resettata: resta l'ultima scelta,
        // pronta per la prossima ricerca (vedi anche la persistenza in
        // AppPreferencesService.SaveLastPoiCategory).
        private void HidePoiSearchBox()
        {
            if (_poiSearchTextBox == null) return;
            _poiSearchTextBox.Text      = "";
            _poiSearchTextBox.IsVisible = false;
            if (_categoryFilterComboBox != null)
                _categoryFilterComboBox.IsVisible = false;
        }

        // Ricostruisce le voci del combo categoria dopo che le Impostazioni
        // hanno cambiato l'elenco delle categorie personalizzate (vedi
        // OnOpenSettings): prova a mantenere la selezione corrente per
        // etichetta, altrimenti torna al primo elemento.
        private void RefreshCategoryCombo()
        {
            if (_categoryFilterComboBox == null) return;
            string? currentLabel = _categoryFilterComboBox.SelectedItem as string;
            var labels = PoiSearchService.AllCategories.Select(c => c.Label).ToList();
            _categoryFilterComboBox.ItemsSource = labels;
            int idx = currentLabel != null ? labels.IndexOf(currentLabel) : -1;
            _categoryFilterComboBox.SelectedIndex = idx >= 0 ? idx : 0;
        }

        // Categoria scelta nel combo: sempre valorizzata (nessuna voce
        // "qualsiasi categoria"), quindi null solo in caso difensivo di
        // indice non valido.
        private (string Key, string Value, string Label)? GetSelectedCategoryFilter()
        {
            int idx = _categoryFilterComboBox?.SelectedIndex ?? -1;
            if (idx < 0 || idx >= PoiSearchService.AllCategories.Count) return null;
            return PoiSearchService.AllCategories[idx];
        }

        // Aggiorna il watermark del campo di testo in base alla voce scelta
        // nel combo: le due voci speciali (indirizzo/città, vedi
        // PoiSearchService.SentinelCategoryKey) richiedono sempre del testo,
        // le categorie vere no (il testo è un filtro opzionale sul nome).
        private void UpdatePoiSearchWatermark()
        {
            if (_poiSearchTextBox == null) return;
            var selected = GetSelectedCategoryFilter();
            _poiSearchTextBox.Watermark = selected?.Key == PoiSearchService.SentinelCategoryKey
                ? (selected.Value.Value == PoiSearchService.AddressSearchValue
                    ? Strings.Get("MainWindow_IndirizzoWatermark")
                    : Strings.Get("MainWindow_CittaWatermark"))
                : Strings.Get("MainWindow_TestoLiberoWatermark");
        }

        // Riconosce una preposizione di luogo ("a", "in", "presso", "vicino a")
        // dentro il testo digitato accanto al combo categoria: la parte dopo
        // NON è un filtro sul nome, è un luogo diverso dall'area visualizzata
        // (es. "a Pechino", "stazione centrale a Prato"). Ritorna (null, null)
        // se non ne trova una: in quel caso l'intero testo resta un filtro sul
        // nome, comportamento invariato.
        private static (string? NamePart, string? LocationPart) SplitNameAndLocation(string text)
        {
            string t = text.Trim();
            foreach (string prep in new[] { "a", "in", "presso", "vicino a" })
            {
                string withSpace = prep + " ";
                int idx = -1;
                if (t.StartsWith(withSpace, StringComparison.OrdinalIgnoreCase))
                    idx = 0;
                else
                {
                    int pos = t.IndexOf(" " + withSpace, StringComparison.OrdinalIgnoreCase);
                    if (pos >= 0) idx = pos + 1;
                }
                if (idx < 0) continue;

                string namePart = t.Substring(0, idx).Trim();
                string locPart  = t.Substring(idx + withSpace.Length).Trim();
                if (locPart.Length > 0)
                    return (namePart.Length > 0 ? namePart : null, locPart);
            }
            return (null, null);
        }

        // Il gruppo di destinazione NON viene creato qui: solo quando l'utente
        // conferma davvero un risultato cliccandolo sulla mappa
        // (ConfirmPoiSearchResult), così una ricerca "a vuoto" o solo
        // esplorativa non crea gruppi indesiderati
        // Riquadro geografico dell'area attualmente visualizzata sulla mappa
        // interattiva: usato da tutte le ricerche (testuale, per categoria,
        // in linguaggio naturale) per sapere dove cercare
        private GeoRect GetCurrentViewBounds()
        {
            float cw = (float)(_mapCanvas?.Bounds.Width  ?? 800);
            float ch = (float)(_mapCanvas?.Bounds.Height ?? 600);
            var (minLon, maxLat) = GeoUtils.PixelToGeo(0, 0, _viewCenterLon, _viewCenterLat, _viewZoom, cw, ch);
            var (maxLon, minLat) = GeoUtils.PixelToGeo(cw, ch, _viewCenterLon, _viewCenterLat, _viewZoom, cw, ch);
            return new GeoRect { MinLon = minLon, MinLat = minLat, MaxLon = maxLon, MaxLat = maxLat };
        }

        // La categoria si sceglie SOLO dal combo (sempre valorizzato, nessuna
        // voce "qualsiasi categoria" — vedi BuildToolbar): il testo digitato
        // è sempre un raffinamento ALL'INTERNO della categoria (es. categoria
        // "stazioni ferroviarie" + testo "centrale" → solo le stazioni con
        // "centrale" nel nome; testo vuoto → tutti i luoghi della categoria).
        private async Task OnPoiSearchAsync()
        {
            string query = _poiSearchTextBox?.Text?.Trim() ?? "";
            var selectedCategory = GetSelectedCategoryFilter();
            if (selectedCategory == null) return;

            var (key, value, label) = selectedCategory.Value;
            var viewBounds = GetCurrentViewBounds();

            // Ogni nuova ricerca parte "pulita": i marker della ricerca
            // precedente vanno tolti subito, non solo quando la nuova ricerca
            // trova a sua volta qualcosa. Altrimenti una nuova query che non
            // trova nulla lascerebbe in giro (e selezionabili) i risultati
            // della query precedente, mentre il messaggio dice "non trovato".
            _poiSearchMode    = false;
            _poiSearchResults = new List<PoiSearchService.Result>();
            _poiSearchResultsAreAddresses = false;
            _mapCanvas?.InvalidateVisual();

            _lastPoiSearchQuery = query;
            // Persistita per riproporla come default del combo alla
            // prossima apertura dell'app (vedi BuildToolbar/AppPreferencesService).
            _appPrefsSvc.SaveLastPoiCategory(key, value);

            GeoRect searchBounds = viewBounds;
            string? nameFilter   = string.IsNullOrWhiteSpace(query) ? null : query;

            // Finestra di log che segue passo-passo l'intera ricerca (Invio o
            // secondo clic sulla lente): l'utente vuole sapere sempre cosa sta
            // succedendo, non solo una riga di stato che sparisce dopo pochi
            // secondi. Un solo modo di chiuderla manualmente ("Annulla", anche
            // dalla X) che annulla l'operazione tramite il token; si chiude da
            // sola SOLO a operazione conclusa (successo, errore o annullamento),
            // mai prima — vedi UI/PoiSearchLogWindow.
            var logWindow = new PoiSearchLogWindow();
            using var cts = new CancellationTokenSource();
            logWindow.CancelRequested += () => cts.Cancel();
            var dialogTask = logWindow.ShowDialog(this);

            logWindow.Log(string.Format(Strings.Get("MainWindow_LogAvvioRicerca"), label) +
                (nameFilter != null ? string.Format(Strings.Get("MainWindow_LogConTesto"), nameFilter) : "") + "...");
            try
            {
                // Le due voci "speciali" in testa al combo (vedi
                // PoiSearchService.AllCategories/SentinelCategoryKey) non sono
                // tag OSM da interrogare su Overpass: dirottano su un flusso
                // di ricerca completamente diverso (geocoding indirizzo,
                // nome città anche parziale da cities500.csv), quindi
                // saltano tutta la logica sotto (preposizione di luogo,
                // RunCategorySearchAsync) che si applica solo alle categorie
                // vere e proprie.
                if (key == PoiSearchService.SentinelCategoryKey)
                {
                    if (value == PoiSearchService.AddressSearchValue)
                        await RunAddressSearchAsync(query, viewBounds, logWindow, cts.Token);
                    else if (value == PoiSearchService.CitySearchValue)
                        await RunCitySearchAsync(query, viewBounds, logWindow, cts.Token);
                    return;
                }

                // Se il testo contiene una preposizione di luogo ("a Pechino",
                // "stazione centrale a Prato"...) la parte dopo NON è un filtro
                // sul nome: è un luogo diverso dall'area visualizzata, e va
                // cercato lì — geocodificato via Nominatim (nessuna IA, stesso
                // meccanismo deterministico di questo percorso), non lasciato
                // come testo da cercare nel nome (dove non troverebbe mai
                // nulla: nessuna stazione si chiama letteralmente "a Pechino").
                if (nameFilter != null)
                {
                    var (namePart, locationPart) = SplitNameAndLocation(nameFilter);
                    if (locationPart != null)
                    {
                        logWindow.Log(string.Format(Strings.Get("MainWindow_LogGeocodificaLuogo"), locationPart));
                        ShowStatusMessage(string.Format(Strings.Get("MainWindow_CercoSullaMappa"), locationPart), seconds: 15);
                        GeoRect? geocoded = null;
                        try { geocoded = await _poiSearchSvc.GeocodePlaceAsync(locationPart, logWindow.Log, cts.Token); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[PoiSearchCategory] geocodifica di \"{locationPart}\" fallita: {ex.Message}");
                            // Non fatale: si prosegue comunque cercando "query" nell'area
                            // visualizzata (vedi ramo "else" sotto), quindi solo Log, non
                            // LogError (che terrebbe la finestra aperta anche se la ricerca
                            // poi va comunque a buon fine).
                            logWindow.Log(string.Format(Strings.Get("MainWindow_LogGeocodificaFallita"), ex.Message));
                        }

                        if (geocoded != null)
                        {
                            searchBounds   = geocoded;
                            nameFilter     = namePart;
                            // Ricentra la mappa lì (senza toccare lo zoom, stessa
                            // regola di sempre), altrimenti i risultati trovati
                            // resterebbero comunque fuori dallo schermo
                            _viewCenterLon = searchBounds.CenterLon;
                            _viewCenterLat = searchBounds.CenterLat;
                            logWindow.Log(Strings.Get("MainWindow_LogLuogoTrovato"));
                        }
                        else
                        {
                            logWindow.Log(string.Format(Strings.Get("MainWindow_LuogoNonTrovato"), locationPart, query));
                            ShowStatusMessage(string.Format(Strings.Get("MainWindow_LuogoNonTrovato"), locationPart, query), isError: true, seconds: 6);
                        }
                    }
                }

                await RunCategorySearchAsync(key, value, label, searchBounds, subFilters: null, nameFilter: nameFilter,
                    logWindow: logWindow, ct: cts.Token);
            }
            catch (OperationCanceledException)
            {
                logWindow.Log(Strings.Get("MainWindow_LogRicercaAnnullataUtente"));
                ShowStatusMessage(Strings.Get("MainWindow_RicercaAnnullata"), seconds: 4);
            }
            finally
            {
                // Si chiude da sé solo se la ricerca è finita bene (o è stata
                // annullata dall'utente): in caso di errore (LogError più sopra
                // ha impostato HasError) resta aperta, altrimenti il messaggio
                // d'errore sparirebbe prima che l'utente possa leggerlo — va
                // chiusa a mano con "Annulla"/X. In entrambi i casi però si
                // attende SEMPRE che la finestra si chiuda (qui o manualmente)
                // prima di uscire dal metodo: "cts" è "using" in questo scope,
                // quindi verrebbe già smaltito al return — se la finestra
                // restasse aperta oltre la fine del metodo, il click su
                // "Annulla" chiamerebbe cts.Cancel() su un token già disposto
                // (ObjectDisposedException).
                if (!logWindow.HasError)
                    logWindow.CloseProgrammatically();
                await dialogTask;
            }
        }

        // Lato massimo (in gradi) del riquadro di ricerca per categoria: oltre
        // questo la vista viene ristretta a un quadrato centrato sul centro
        // della mappa (vedi RunCategorySearchAsync) invece di interrogare
        // Overpass su tutta l'area visualizzata.
        private const double MaxCategorySearchDegrees = 3.0;

        // Ricerca per categoria via tag OSM (Overpass): unico chiamante è il
        // ramo "categoria scelta dal combo" di OnPoiSearchAsync — la
        // categoria si sceglie SOLO lì, mai da testo libero riconosciuto
        // automaticamente (nessuna ambiguità: se l'utente non la sceglie dal
        // combo, il testo va sempre alla ricerca in linguaggio naturale/
        // Nominatim, mai qui). Flusso lineare: 1) Overpass (SEMPRE l'elenco
        // completo della categoria nell'area, nessun filtro sul nome lato
        // Overpass); 2) se c'è del testo digitato, lo si usa per raffinare —
        // con l'AI (se è configurata una chiave Groq: sceglie e valuta i
        // candidati, vedi FilterAndScoreByQueryAsync) o, altrimenti, con un
        // semplice filtro letterale sul nome (sottostringa, case-insensitive).
        private async Task RunCategorySearchAsync(string key, string value, string label, GeoRect viewBounds,
            IEnumerable<string>? subFilters, string? nameFilter = null,
            PoiSearchLogWindow? logWindow = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            void log(string msg) => logWindow?.Log(msg);

            // Le ricerche per categoria (Overpass) scandagliscono tutta l'area
            // visualizzata cercando il tag OSM: su una vista molto ampia (es.
            // un'intera regione/nazione) diventano lentissime e rischiano il
            // timeout sul server pubblico condiviso. Invece di rifiutarsi e
            // basta (lasciando l'utente senza risultati e con un messaggio
            // facile da non notare), si restringe la ricerca a un riquadro
            // di lato MaxCategorySearchDegrees centrato sul centro della
            // vista attuale — così torna comunque qualcosa di utile, anche
            // se non copre tutta l'area visualizzata.
            bool areaClamped = viewBounds.Width > MaxCategorySearchDegrees || viewBounds.Height > MaxCategorySearchDegrees;
            if (areaClamped)
            {
                double origWidth = viewBounds.Width, origHeight = viewBounds.Height;
                double half = MaxCategorySearchDegrees / 2.0;
                viewBounds = new GeoRect
                {
                    MinLon = viewBounds.CenterLon - half,
                    MaxLon = viewBounds.CenterLon + half,
                    MinLat = viewBounds.CenterLat - half,
                    MaxLat = viewBounds.CenterLat + half,
                };
                log(string.Format(Strings.Get("MainWindow_LogAreaTroppoAmpiaRestringo"), origWidth.ToString("F1"), origHeight.ToString("F1"), MaxCategorySearchDegrees));
            }

            string displayLabel = string.IsNullOrWhiteSpace(nameFilter) ? label : string.Format(Strings.Get("MainWindow_LabelConTestoTraVirgolette"), label, nameFilter);
            ShowStatusMessage(areaClamped
                ? string.Format(Strings.Get("MainWindow_AreaTroppoAmpiaCercoIntorno"), displayLabel)
                : string.Format(Strings.Get("MainWindow_CercoNellaZona"), displayLabel), seconds: 3);

            var allSubFilters = (subFilters ?? Enumerable.Empty<string>())
                .Concat(PoiSearchService.GetCategoryExcludeFilters(key, value));

            List<PoiSearchService.Result> results;
            try
            {
                // Unisce sempre i filtri di esclusione fissi della categoria
                // (es. "station!=subway" per "stazioni ferroviarie": senza,
                // in una città con molte fermate di metro taggate allo stesso
                // modo, queste riempiono da sole il limite di risultati e le
                // stazioni ferroviarie vere restano fuori — vedi
                // PoiSearchService.CategoryExcludeFilters). Sempre l'elenco
                // completo della categoria: nessun filtro sul nome qui.
                log(string.Format(Strings.Get("MainWindow_LogCercoCategoriaArea"), label, key, value));
                results = await _poiSearchSvc.SearchCategoryAsync(key, value, viewBounds, allSubFilters, log, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logWindow?.LogError(string.Format(Strings.Get("MainWindow_ErroreRicercaCategoria"), ex.Message));
                return;
            }

            // Se SearchCategoryAsync ha dovuto tagliare (più di CategoryResultCap
            // elementi trovati su Overpass), l'elenco non è completo: lo si
            // segnala sempre, non solo quando il testo digitato filtra ancora
            // di più (vedi PoiSearchService.CategoryResultCap).
            bool possiblyTruncated = results.Count >= PoiSearchService.CategoryResultCap;

            bool usedAi = false;
            if (results.Count > 0 && !string.IsNullOrWhiteSpace(nameFilter))
            {
                bool aiAvailable = !string.IsNullOrWhiteSpace(_project.Settings.GroqApiKey);
                if (aiAvailable)
                {
                    try
                    {
                        results = await _poiSearchSvc.FilterAndScoreByQueryAsync(
                            _project.Settings.GroqApiKey, nameFilter, results,
                            viewBounds, viewBounds.CenterLon, viewBounds.CenterLat, log, ct);
                        usedAi = true;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // Non fatale: una chiave Groq scaduta/non valida o un
                        // errore di rete verso l'AI non deve lasciare l'utente
                        // a mani vuote — si ripiega sullo stesso filtro
                        // letterale usato quando l'AI non è configurata
                        // affatto (vedi ramo "else" sotto), invece di
                        // interrompere la ricerca con zero risultati. Loggato
                        // come errore (resta visibile, rosso) ma la ricerca
                        // prosegue.
                        logWindow?.LogError(string.Format(Strings.Get("MainWindow_SelezioneAiNonRiuscita"), ex.Message));
                        aiAvailable = false;
                    }
                }

                if (!aiAvailable)
                {
                    // Nessuna chiave AI configurata (o chiamata AI fallita,
                    // vedi sopra): il testo digitato filtra comunque, ma con
                    // un semplice confronto sul nome (sottostringa,
                    // case-insensitive) invece dell'AI — meno capace di
                    // richieste articolate ("della linea Firenze-Bologna"),
                    // ma sempre meglio di ignorare il testo o di azzerare i
                    // risultati.
                    log(string.Format(Strings.Get("MainWindow_LogFiltroTestoLetterale"), nameFilter));
                    results = results
                        .Where(r => r.DisplayName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
            }

            if (results.Count == 0)
            {
                logWindow?.LogError(areaClamped
                    ? string.Format(Strings.Get("MainWindow_NessunRisultatoAreaRistretta"), displayLabel)
                    : string.Format(Strings.Get("MainWindow_NessunRisultatoZonaVisualizzata"), displayLabel));
                return;
            }

            log(string.Format(Strings.Get("MainWindow_LogRisultatiTrovatiMostro"), results.Count));
            CancelAllAddModes();
            _poiSearchMode         = true;
            _poiSearchResults      = results;
            _poiSearchQueryLabel   = displayLabel;
            _poiSearchAreaClamped  = areaClamped;
            _mapCanvas?.InvalidateVisual();
            // Se il conteggio tocca il limite della query, l'elenco
            // potrebbe non essere completo: l'utente deve saperlo invece
            // di credere che siano davvero tutti (vedi PoiSearchService.CategoryResultCap)
            string resultMessage = string.Format(Strings.Get("MainWindow_ResRisultatiPer"), results.Count, displayLabel);
            if (usedAi)             resultMessage += Strings.Get("MainWindow_ResSelezionatiAI");
            resultMessage += Strings.Get("MainWindow_ResClickMarker");
            if (possiblyTruncated)  resultMessage += Strings.Get("MainWindow_ResPossiblyTruncated");
            if (areaClamped)        resultMessage += Strings.Get("MainWindow_ResAreaClamped");
            ShowStatusMessage(resultMessage, seconds: 8);
        }

        // Voce "Ricerca un indirizzo" del combo categoria (sentinella, vedi
        // PoiSearchService.SentinelCategoryKey/AllCategories): il testo
        // digitato è un indirizzo/via+città, geocodificato su Nominatim
        // (PoiSearchService.SearchAddressAsync) — a differenza della ricerca
        // per categoria non c'è alcun tag OSM/Overpass coinvolto, quindi
        // niente clamp d'area (un indirizzo scritto per esteso può stare
        // benissimo fuori dalla vista attuale).
        private async Task RunAddressSearchAsync(string query, GeoRect viewBounds, PoiSearchLogWindow logWindow, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(query))
            {
                logWindow.LogError(Strings.Get("MainWindow_ScriviIndirizzoPrimaCercare"));
                return;
            }

            logWindow.Log(string.Format(Strings.Get("MainWindow_LogCercoIndirizzo"), query));
            ShowStatusMessage(string.Format(Strings.Get("MainWindow_LogCercoIndirizzo"), query), seconds: 3);

            List<PoiSearchService.Result> results;
            try
            {
                results = await _poiSearchSvc.SearchAddressAsync(query, viewBounds, onProgress: logWindow.Log, ct: ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logWindow.LogError(string.Format(Strings.Get("MainWindow_ErroreRicercaIndirizzo"), ex.Message));
                return;
            }

            if (results.Count == 0)
            {
                logWindow.LogError(string.Format(Strings.Get("MainWindow_NessunIndirizzoTrovatoPer"), query));
                return;
            }

            logWindow.Log(string.Format(Strings.Get("MainWindow_LogIndirizziTrovatiMostro"), results.Count));
            CancelAllAddModes();
            // Ricentra la mappa sul risultato più rilevante (il primo,
            // l'ordine di Nominatim è per pertinenza — non sulla media di
            // tutti: risultati sparsi in tutto il mondo per una query
            // generica darebbero un baricentro senza senso), stessa regola
            // della geocodifica "a <città>" in OnPoiSearchAsync, senza
            // toccare lo zoom. Un indirizzo cercato può benissimo stare
            // fuori dalla vista attuale, altrimenti i marker resterebbero
            // fuori schermo e sembrerebbe che la ricerca non abbia trovato
            // nulla.
            _viewCenterLon = results[0].Lon;
            _viewCenterLat = results[0].Lat;
            _poiSearchMode        = true;
            _poiSearchResults     = results;
            _poiSearchResultsAreAddresses = true;
            _poiSearchQueryLabel  = $"indirizzo \"{query}\"";
            _poiSearchAreaClamped = false;
            _mapCanvas?.InvalidateVisual();
            ShowStatusMessage(
                string.Format(Strings.Get("MainWindow_RisultatiPerIndirizzo"), results.Count, query),
                seconds: 8);
        }

        // Numero massimo di città mostrate quando il testo è vuoto (vedi
        // sotto): solo per orientarsi tra quelle già visibili nella vista
        // attuale, non serve mostrarle tutte come per una ricerca per nome.
        private const int CitiesInViewMax = 30;

        // Voce "Ricerca una città" del combo categoria (sentinella): il testo
        // digitato è un nome di città anche parziale, cercato su tutto il
        // database mondiale cities500.csv (CityDatabase.SearchByName) — nessuna
        // chiamata di rete, ricerca locale immediata. Niente clamp d'area/
        // vincolo alla vista attuale: cercare "milano" deve trovare Milano
        // anche se la mappa è centrata altrove. Testo vuoto (a differenza
        // della ricerca indirizzo, qui NON è obbligatorio): mostra invece le
        // città già visibili nella vista attuale (CityDatabase.FindTopCities),
        // "quello che vedo", senza ricentrare la mappa (sono già a schermo).
        private async Task RunCitySearchAsync(string query, GeoRect viewBounds, PoiSearchLogWindow logWindow, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            bool inViewMode = string.IsNullOrWhiteSpace(query);
            List<PoiSearchService.Result> results;

            if (inViewMode)
            {
                logWindow.Log(Strings.Get("MainWindow_LogNessunNomeCercoPopolose"));
                ShowStatusMessage(Strings.Get("MainWindow_CercoCittaVisibili"), seconds: 3);

                var cities = await Task.Run(() => CityDatabase.FindTopCities(viewBounds, CitiesInViewMax), ct);
                ct.ThrowIfCancellationRequested();

                if (cities.Count == 0)
                {
                    logWindow.LogError(string.Format(Strings.Get("MainWindow_NessunaCittaTrovataArea"), CityDatabase.LoadStatus));
                    return;
                }

                results = cities.Select(c => new PoiSearchService.Result(
                    c.Name, c.Lon, c.Lat, Strings.Get("MainWindow_CategoriaCitta"), null, string.Format(Strings.Get("MainWindow_Popolazione"), c.Population.ToString("N0")))).ToList();
            }
            else
            {
                logWindow.Log(string.Format(Strings.Get("MainWindow_LogCercoCittaContiene"), query));
                ShowStatusMessage(string.Format(Strings.Get("MainWindow_CercoCitta"), query), seconds: 3);

                var cities = await Task.Run(() => CityDatabase.SearchByName(query), ct);
                ct.ThrowIfCancellationRequested();

                if (cities.Count == 0)
                {
                    int citiesInView = CityDatabase.CountInBounds(viewBounds);
                    logWindow.LogError(string.Format(Strings.Get("MainWindow_NessunaCittaTrovataPer"), query, citiesInView));
                    return;
                }

                results = cities.Select(c => new PoiSearchService.Result(
                    c.Name, c.Lon, c.Lat, Strings.Get("MainWindow_CategoriaCitta"), null, string.Format(Strings.Get("MainWindow_Popolazione"), c.Population.ToString("N0")))).ToList();
            }

            logWindow.Log(string.Format(Strings.Get("MainWindow_LogCittaTrovateMostro"), results.Count));
            CancelAllAddModes();
            if (!inViewMode)
            {
                // Ricentra sulla città più popolosa tra i risultati (il primo:
                // CityDatabase.SearchByName ordina già per popolazione
                // decrescente) — altrimenti i marker potrebbero restare fuori
                // dalla vista attuale e sembrare "nessun risultato". Non serve
                // in modalità "quello che vedo": sono già nella vista attuale.
                _viewCenterLon = results[0].Lon;
                _viewCenterLat = results[0].Lat;
            }
            _poiSearchMode        = true;
            _poiSearchResults     = results;
            _poiSearchQueryLabel  = inViewMode ? Strings.Get("MainWindow_CittaNellAreaVisualizzata") : string.Format(Strings.Get("MainWindow_CittaTraVirgolette"), query);
            _poiSearchAreaClamped = false;
            _mapCanvas?.InvalidateVisual();
            ShowStatusMessage(
                (inViewMode
                    ? string.Format(Strings.Get("MainWindow_CittaNellAreaVisualizzataConteggio"), results.Count)
                    : string.Format(Strings.Get("MainWindow_CittaTrovatePerConteggio"), results.Count, query)) +
                Strings.Get("MainWindow_ClicMarkerAggiungerli"),
                seconds: 8);
        }

        // Ricerca inversa "cosa c'è in questo punto GPS" (shift + tasto destro
        // sulla mappa): interroga Nominatim per il luogo/indirizzo più vicino
        // al punto cliccato e lo mostra come singolo marker candidato,
        // riusando lo stesso flusso di conferma della ricerca testuale (anche
        // qui il gruppo si crea solo alla conferma, non prima)
        private async Task OnReverseGeocodeAsync(double lon, double lat)
        {
            ShowStatusMessage(Strings.Get("MainWindow_RicercaCosaSiTrova"), seconds: 3);
            try
            {
                var result = await _poiSearchSvc.ReverseAsync(lon, lat);
                if (result == null)
                {
                    ShowStatusMessage(Strings.Get("MainWindow_NessunaInfoTrovataPunto"), isError: true);
                    return;
                }

                CancelAllAddModes();
                _poiSearchMode       = true;
                _poiSearchResults    = new List<PoiSearchService.Result> { result };
                _poiSearchQueryLabel = Strings.Get("MainWindow_RicercaGps");
                _poiSearchAreaClamped = false;
                _mapCanvas?.InvalidateVisual();
                ShowStatusMessage(string.Format(Strings.Get("MainWindow_TrovatoClicMarker"), SanitizeSearchLabel(result.DisplayName)), seconds: 8);
            }
            catch (Exception ex)
            {
                ShowStatusMessage(string.Format(Strings.Get("MainWindow_ErroreRicercaInversa"), ex.Message), isError: true);
            }
        }

        // ---------------------------------------------------------------
        // File recenti
        // ---------------------------------------------------------------
        private void ShowRecentFilesFlyout(Control anchor)
        {
            var recents = _recentFilesSvc.GetRecent();

            var flyout = new MenuFlyout();
            if (recents.Count == 0)
            {
                flyout.Items.Add(new MenuItem { Header = Strings.Get("MainWindow_NessunFileRecente"), IsEnabled = false });
            }
            else
            {
                foreach (var path in recents)
                {
                    var mi = new MenuItem { Header = Path.GetFileName(path) };
                    ToolTip.SetTip(mi, path);
                    mi.Click += async (_, _) => await OpenProjectFromPath(path);
                    flyout.Items.Add(mi);
                }
            }
            FlyoutBase.SetAttachedFlyout(anchor, flyout);
            flyout.ShowAt(anchor);
        }

        // Elenco a scelta rapida delle categorie note (tag OSM, vedi
        // PoiSearchService.AllCategories/Categories): stessa ricerca per
        // categoria già raggiungibile digitando la parola chiave esatta nella
        // casella di ricerca, ma scopribile senza doverla indovinare/ricordare.
        // Scegliere una categoria NON lancia subito la ricerca: scrive solo
        // l'etichetta nella casella (così si vede sempre cosa è stato scelto)
        // e mette il focus lì — parte tutto e solo quando si preme Invio
        // (OnPoiSearchAsync), esattamente come per una ricerca digitata a mano.
        // Aggiunge una pagina centrata su (lon, lat)
        private void AddPageAtLocation(double lon, double lat)
        {
            var page = new MapPage
            {
                Id         = _projSvc.GetNextPageId(_project),
                GeoBounds  = GeoUtils.CalcPageBounds(lon, lat, _project.Settings),
                PageNumber = _project.Pages.Count + 2
            };
            page.Label = _projSvc.GenerateAutoLabel(_project, page);
            _project.Pages.Add(page);
            _selectedPageId = page.Id;
            TouchPage(page.Id);
            _isDirty = true;
            PushUndo(
                undo: () => { _project.Pages.Remove(page); if (_selectedPageId == page.Id) _selectedPageId = null; },
                redo: () => { if (!_project.Pages.Contains(page)) _project.Pages.Add(page); _selectedPageId = page.Id; });

            RefreshNavigationTree();
            _mapCanvas?.InvalidateVisual();
        }

        private void DeletePage(int pageId)
        {
            int idx = _project.Pages.FindIndex(p => p.Id == pageId);
            if (idx < 0) return;
            var page = _project.Pages[idx];

            _project.Pages.RemoveAt(idx);
            if (_selectedPageId == pageId) _selectedPageId = null;
            _isDirty = true;
            PushUndo(
                undo: () => { if (!_project.Pages.Contains(page)) _project.Pages.Insert(Math.Min(idx, _project.Pages.Count), page); },
                redo: () => { _project.Pages.Remove(page); if (_selectedPageId == page.Id) _selectedPageId = null; });
            RefreshNavigationTree();
            _mapCanvas?.InvalidateVisual();
        }

        // Elimina tutte le pagine selezionate con Ctrl+clic nell'albero, dopo
        // conferma (eliminazione in blocco su più elementi: merita comunque
        // una conferma esplicita, anche se ora è annullabile con Ctrl+Z)
        private async Task DeleteSelectedPagesAsync()
        {
            int count = _multiSelectedPageIds.Count;
            if (count == 0) return;

            bool confirmed = await AskYesNo(Strings.Get("MainWindow_EliminaPagineTitolo"),
                string.Format(Strings.Get("MainWindow_EliminarePagineSelezionate"), count));
            if (!confirmed) return;

            // Cattura pagine + indice originale (ordinati) per poterle reinserire
            // nella stessa posizione con l'undo
            var removed = _project.Pages
                .Select((p, i) => (page: p, index: i))
                .Where(t => _multiSelectedPageIds.Contains(t.page.Id))
                .ToList();

            _project.Pages.RemoveAll(p => _multiSelectedPageIds.Contains(p.Id));
            if (_selectedPageId.HasValue && _multiSelectedPageIds.Contains(_selectedPageId.Value))
                _selectedPageId = null;
            _multiSelectedPageIds.Clear();
            _isDirty = true;
            PushUndo(
                undo: () =>
                {
                    foreach (var (page, index) in removed)
                        if (!_project.Pages.Contains(page))
                            _project.Pages.Insert(Math.Min(index, _project.Pages.Count), page);
                },
                redo: () => _project.Pages.RemoveAll(p => removed.Any(t => t.page == p)));
            RefreshNavigationTree();
            _mapCanvas?.InvalidateVisual();
            ShowStatusMessage(string.Format(Strings.Get("MainWindow_EliminatePagine"), count));
        }

        // Dialog di conferma generico Sì/Annulla (per operazioni distruttive
        // che meritano più attenzione di un semplice messaggio in status bar)
        private async Task<bool> AskYesNo(string title, string message)
        {
            bool confirmed = false;
            var dlg = new Window
            {
                Title  = title,
                Width  = 420,
                Height = 170,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new StackPanel
                {
                    Margin  = new Thickness(18),
                    Spacing = 16,
                    Children =
                    {
                        new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                            Spacing = 10,
                            Children =
                            {
                                DialogUi.MakeDialogButton(Strings.Get("MainWindow_Si"), primary: true),
                                DialogUi.MakeDialogButton(Strings.Get("MainWindow_Annulla"))
                            }
                        }
                    }
                }
            };

            var btns = ((StackPanel)((StackPanel)dlg.Content!).Children[1]);
            ((Button)btns.Children[0]).Click += (_, _) => { confirmed = true;  dlg.Close(); };
            ((Button)btns.Children[1]).Click += (_, _) => { confirmed = false; dlg.Close(); };

            await dlg.ShowDialog(this);
            return confirmed;
        }

        // Dialog di scelta di un gruppo POI fra una lista data (usato per
        // spostare i POI selezionati in un altro gruppo): unico punto in cui
        // resta una ComboBox esplicita, perché qui la scelta è deliberata e
        // dell'utente, a differenza del gruppo target della ricerca online
        // (assegnato automaticamente)
        private async Task<PoiGroup?> ShowGroupPickerDialogAsync(string title, string message, List<PoiGroup> choices)
        {
            if (choices.Count == 0) return null;

            PoiGroup? chosen = null;
            var combo = new ComboBox
            {
                ItemsSource   = choices,
                SelectedIndex = 0,
                Width         = 260,
                ItemTemplate  = new Avalonia.Controls.Templates.FuncDataTemplate<object>(
                    (item, _) => new TextBlock { Text = item is PoiGroup g ? g.Name : "" })
            };

            var dlg = new Window
            {
                Title  = title,
                Width  = 380,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new StackPanel
                {
                    Margin  = new Thickness(18),
                    Spacing = 14,
                    Children =
                    {
                        new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                        combo,
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                            Spacing = 10,
                            Children =
                            {
                                DialogUi.MakeDialogButton(Strings.Get("MainWindow_Sposta"), primary: true),
                                DialogUi.MakeDialogButton(Strings.Get("MainWindow_Annulla"))
                            }
                        }
                    }
                }
            };

            var btns = ((StackPanel)((StackPanel)dlg.Content!).Children[2]);
            ((Button)btns.Children[0]).Click += (_, _) => { chosen = combo.SelectedItem as PoiGroup; dlg.Close(); };
            ((Button)btns.Children[1]).Click += (_, _) => dlg.Close();

            await dlg.ShowDialog(this);
            return chosen;
        }

        // Sposta tutti i POI selezionati con Ctrl+clic nell'albero in un
        // gruppo scelto dall'utente, riassegnando l'ID (univoco solo per
        // gruppo) tramite PoiService.GetNextItemId
        private async Task MoveSelectedPoiToGroupAsync()
        {
            if (_multiSelectedPoiKeys.Count == 0) return;

            var candidates = _project.PoiGroups.ToList();
            if (candidates.Count < 2)
            {
                ShowStatusMessage(Strings.Get("MainWindow_ServeAltroGruppoPoi"), isError: true);
                return;
            }

            var target = await ShowGroupPickerDialogAsync(Strings.Get("MainWindow_SpostaPoiTitolo"),
                string.Format(Strings.Get("MainWindow_SpostaPoiSelezionatiMessaggio"), _multiSelectedPoiKeys.Count), candidates);
            if (target == null) return;

            int moved = 0;
            foreach (var (groupId, itemId) in _multiSelectedPoiKeys.ToList())
            {
                if (groupId == target.Id) continue;
                var srcGroup = _project.PoiGroups.FirstOrDefault(g => g.Id == groupId);
                var item     = srcGroup?.Items.FirstOrDefault(it => it.Id == itemId);
                if (srcGroup == null || item == null) continue;

                srcGroup.Items.Remove(item);
                item.Id = _poiSvc.GetNextItemId(target);
                target.Items.Add(item);
                TouchPoiGroup(srcGroup.Id);
                moved++;
            }

            TouchPoiGroup(target.Id);
            _navCollapsedGroupIds.Remove(target.Id);
            _multiSelectedPoiKeys.Clear();
            _isDirty = true;
            RefreshNavigationTree();
            _mapCanvas?.InvalidateVisual();
            ShowStatusMessage(string.Format(Strings.Get("MainWindow_SpostatiPoiNelGruppo"), moved, target.Name));
        }

        // Apre EditPageWindow per modificare etichetta, descrizione e coordinate
        private async Task EditPage(MapPage page)
        {
            var win = new EditPageWindow(page, _project.Settings);
            await win.ShowDialog(this);

            if (!win.Confirmed) return;

            // Applica le modifiche alla pagina nel progetto
            var updated = win.ResultPage;
            page.Label       = updated.Label;
            page.Description = updated.Description;
            page.GeoBounds   = updated.GeoBounds;

            TouchPage(page.Id);
            _isDirty = true;
            RefreshNavigationTree();
            _mapCanvas?.InvalidateVisual();
        }

        // Aggiorna la lista pagine nel pannello sinistro
        private void RefreshNavigationTree()
        {
            // La ricerca POI online è un flusso transitorio: qualsiasi altra
            // azione che tocchi il progetto (modifica/cancella/importa/nuovo
            // progetto ecc. — praticamente tutto passa da qui) la interrompe,
            // così non resta "appesa" mentre l'utente fa altro. Non entrare in
            // questo ramo mentre i risultati vengono mostrati per la prima
            // volta: OnPoiSearchAsync/OnReverseGeocodeAsync impostano
            // _poiSearchMode SENZA passare da RefreshNavigationTree. Non entrare
            // nemmeno subito dopo aver confermato un risultato (vedi
            // _suppressPoiSearchAutoExit): l'utente potrebbe volerne
            // confermare altri dagli stessi risultati di ricerca.
            if (_poiSearchMode && !_suppressPoiSearchAutoExit)
            {
                _poiSearchMode    = false;
                _poiSearchResults = new List<PoiSearchService.Result>();
                HidePoiSearchBox();
            }

            // Ricostruisce l'intero pannello sinistra per semplicità
            // (con Avalonia MVVM si userebbe un ListBox con binding)
            if (Content is Grid mainGrid)
            {
                var split = mainGrid.Children.OfType<Grid>().FirstOrDefault();
                if (split == null) return;
                var leftBorder = split.Children.OfType<Border>().FirstOrDefault();
                if (leftBorder?.Child is DockPanel dock)
                {
                    // Rimuovi il vecchio ScrollViewer e ricrea
                    var oldScroll = dock.Children.OfType<ScrollViewer>().FirstOrDefault();
                    if (oldScroll != null) dock.Children.Remove(oldScroll);

                    var newScroll = new ScrollViewer { Content = BuildNavigationTree() };
                    dock.Children.Add(newScroll);

                    // Aggiorna anche il blocco info impostazioni
                    var settingsPanel = dock.Children.OfType<StackPanel>().FirstOrDefault();
                    if (settingsPanel != null)
                    {
                        var oldInfo = settingsPanel.Children.OfType<StackPanel>().FirstOrDefault();
                        if (oldInfo != null) settingsPanel.Children.Remove(oldInfo);
                        settingsPanel.Children.Add(BuildSettingsInfoBlock());
                    }
                }
            }
            UpdateStatusBarSummary();
        }

        // ---------------------------------------------------------------
        // Azioni toolbar
        // ---------------------------------------------------------------
        private async void OnNewProject(object? sender, RoutedEventArgs e)
        {
            // Se ci sono modifiche non salvate, chiedi conferma
            if (_isDirty)
            {
                bool save = await AskSaveChanges();
                if (save)
                {
                    if (_currentFilePath != null)
                        await SaveCurrentProject(_currentFilePath);
                    else
                    {
                        OnSaveProjectAs(sender, e);
                        return; // il salvataggio async porta avanti il flusso da solo
                    }
                }
            }

            // Crea nuovo progetto vuoto centrato su Roma
            _project         = new StradarioProject { ProjectName = Strings.Get("MainWindow_NuovoStradarioProjectName") };
            _currentFilePath = null;
            _isDirty         = false;
            _selectedPageId  = null;
            _viewCenterLon   = 12.4964;
            _viewCenterLat   = 41.9028;
            _viewZoom        = 10.0;
            _pageLastTouchUtc.Clear();
            _poiGroupLastTouchUtc.Clear();
            _percorsoLastTouchUtc.Clear();
            _multiSelectedPageIds.Clear();
            _multiSelectedPoiKeys.Clear();
            _undoStack.Clear();
            _redoStack.Clear();
            _renderer.ClearCache();
            ApplyGlobalPreferences();
            RefreshNavigationTree();
            _mapCanvas?.InvalidateVisual();
            UpdateTitle();
        }
        private async void OnOpenProject(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title          = Strings.Get("MainWindow_ApriProgettoTitolo"),
                AllowMultiple  = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType(Strings.Get("MainWindow_FiltroStradario"))
                        { Patterns = new[] { "*.stradario" } },
                    new Avalonia.Platform.Storage.FilePickerFileType(Strings.Get("MainWindow_FiltroTuttiFile"))
                        { Patterns = new[] { "*.*" } }
                }
            });

            if (files.Count == 0) return;
            await OpenProjectFromPath(files[0].Path.LocalPath);
        }

        // Carica un progetto da un percorso noto (usato sia dal file picker sia
        // dal menu "File recenti"); non chiede conferma per modifiche non
        // salvate del progetto corrente: il chiamante se ne occupa se necessario
        private async Task OpenProjectFromPath(string path)
        {
            try
            {
                _project         = await _projSvc.LoadAsync(path);
                _currentFilePath = path;
                _viewCenterLon   = _project.ViewCenterLon;
                _viewCenterLat   = _project.ViewCenterLat;
                _viewZoom        = _project.ViewZoom;
                _selectedPageId  = null;
                _isDirty         = false;
                _undoStack.Clear();
                _redoStack.Clear();
                _multiSelectedPageIds.Clear();
                _multiSelectedPoiKeys.Clear();

                // All'apertura, tutto parte bloccato: protegge da spostamenti
                // accidentali un progetto già impostato che si riapre solo per
                // consultarlo/generare il PDF (si sblocca manualmente quando serve)
                foreach (var p in _project.Pages)     p.IsLocked = true;
                foreach (var g in _project.PoiGroups) g.IsLocked = true;
                foreach (var r in _project.Percorsi)  r.IsLocked = true;
                _pageLastTouchUtc.Clear();
                _poiGroupLastTouchUtc.Clear();
                _percorsoLastTouchUtc.Clear();

                ApplyGlobalPreferences();
                _recentFilesSvc.Add(path);
                RefreshNavigationTree();
                _mapCanvas?.InvalidateVisual();
                UpdateTitle();
            }
            catch (Exception ex)
            {
                await ShowError(string.Format(Strings.Get("MainWindow_ErroreAperturaFile"), ex.Message));
            }
        }

        private async void OnSaveProject(object? sender, RoutedEventArgs e)
        {
            if (_currentFilePath == null)
            {
                OnSaveProjectAs(sender, e);
                return;
            }
            await SaveCurrentProject(_currentFilePath);
        }

        private async void OnSaveProjectAs(object? sender, RoutedEventArgs e)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title                  = Strings.Get("MainWindow_SalvaProgettoTitolo"),
                DefaultExtension       = "stradario",
                SuggestedFileName      = _project.ProjectName,
                FileTypeChoices = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType(Strings.Get("MainWindow_FiltroStradario"))
                        { Patterns = new[] { "*.stradario" } }
                }
            });

            if (file == null) return;
            string path = file.Path.LocalPath;
            _currentFilePath = path;
            await SaveCurrentProject(path);
        }

        private async Task SaveCurrentProject(string path)
        {
            _project.ViewCenterLon = _viewCenterLon;
            _project.ViewCenterLat = _viewCenterLat;
            _project.ViewZoom      = _viewZoom;

            try
            {
                await _projSvc.SaveAsync(_project, path);
                _isDirty = false;
                _recentFilesSvc.Add(path);
                try { File.Delete(GetAutosavePath()); } catch { /* sidecar facoltativo, ignora errori */ }
                UpdateTitle();
            }
            catch (Exception ex)
            {
                await ShowError(string.Format(Strings.Get("MainWindow_ErroreSalvataggio"), ex.Message));
            }
        }

        // Mostra dialog "Vuoi salvare le modifiche?" → true = salva, false = scarta
        private async Task<bool> AskSaveChanges()
        {
            bool save = false;
            var dlg = new Window
            {
                Title   = Strings.Get("MainWindow_ModificheNonSalvateTitolo"),
                Width   = 420,
                Height  = 170,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin  = new Thickness(18),
                    Spacing = 16,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = Strings.Get("MainWindow_ProgettoHaModifiche"),
                            TextWrapping = TextWrapping.Wrap
                        },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                            Spacing = 10,
                            Children =
                            {
                                DialogUi.MakeDialogButton(Strings.Get("MainWindow_SalvaEmoji"), primary: true),
                                DialogUi.MakeDialogButton(Strings.Get("MainWindow_ScartaEmoji")),
                                DialogUi.MakeDialogButton(Strings.Get("MainWindow_Annulla"))
                            }
                        }
                    }
                }
            };

            bool cancelled = false;
            var btnPanel = ((StackPanel)((StackPanel)dlg.Content!).Children[1]);
            ((Button)btnPanel.Children[0]).Click += (s, e) => { save = true;  dlg.Close(); };
            ((Button)btnPanel.Children[1]).Click += (s, e) => { save = false; dlg.Close(); };
            ((Button)btnPanel.Children[2]).Click += (s, e) => { cancelled = true; dlg.Close(); };

            await dlg.ShowDialog(this);
            if (cancelled) return false; // chiamante non deve procedere
            return save;
        }

        // Genera il PDF in un file temporaneo, lo apre nel visualizzatore PDF
        // di sistema per l'anteprima, quindi chiede all'utente se salvarlo in
        // una posizione definitiva o scartarlo (invece di chiedere subito
        // dove salvare, come in precedenza)
        private async void OnGeneratePdf(object? sender, RoutedEventArgs e)
        {
            if (_project.Pages.Count == 0)
            {
                await ShowError(Strings.Get("MainWindow_NessunaPaginaDefinita"));
                return;
            }

            string tempPath = Path.Combine(Path.GetTempPath(), $"stradario_preview_{Guid.NewGuid():N}.pdf");

            var progressWin = new ProgressWindow(Strings.Get("MainWindow_GenerazionePdfInCorso"));
            progressWin.Show(this);

            var generator = new PdfGenerator();
            try
            {
                await generator.GenerateAsync(_project, tempPath,
                    (current, total, msg) =>
                        Dispatcher.UIThread.Post(() => progressWin.Update(current, total, msg)));
                progressWin.Close();
            }
            catch (Exception ex)
            {
                progressWin.Close();
                await ShowError(string.Format(Strings.Get("MainWindow_ErroreGenerazionePdf"), ex.Message));
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
            }
            catch { /* nessun visualizzatore PDF disponibile: l'anteprima resta comunque salvabile */ }

            await ShowPdfPreviewDialog(tempPath);
        }

        // Dialog di anteprima mostrato dopo la generazione: il PDF è già
        // aperto nel visualizzatore di sistema; l'utente sceglie se salvarlo
        // in una posizione definitiva o chiudere (scartando il file temporaneo)
        private async Task ShowPdfPreviewDialog(string tempPath)
        {
            bool save = false;

            var dlg = new Window
            {
                Title  = Strings.Get("MainWindow_AnteprimaPdfTitolo"),
                Width  = 440,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new StackPanel
                {
                    Margin  = new Thickness(18),
                    Spacing = 16,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = Strings.Get("MainWindow_PdfGeneratoMessaggio"),
                            TextWrapping = TextWrapping.Wrap
                        },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                            Spacing = 10,
                            Children =
                            {
                                DialogUi.MakeDialogButton(Strings.Get("MainWindow_SalvaEmoji"), primary: true),
                                DialogUi.MakeDialogButton(Strings.Get("MainWindow_ChiudiEmoji"))
                            }
                        }
                    }
                }
            };

            var btns = ((StackPanel)((StackPanel)dlg.Content!).Children[1]);
            ((Button)btns.Children[0]).Click += (_, _) => { save = true;  dlg.Close(); };
            ((Button)btns.Children[1]).Click += (_, _) => { save = false; dlg.Close(); };

            await dlg.ShowDialog(this);

            if (save)
            {
                var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title             = Strings.Get("MainWindow_SalvaPdfTitolo"),
                    DefaultExtension  = "pdf",
                    SuggestedFileName = _project.ProjectName,
                    FileTypeChoices   = new[]
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType(Strings.Get("MainWindow_FiltroPdf"))
                            { Patterns = new[] { "*.pdf" } }
                    }
                });

                if (file != null)
                {
                    try
                    {
                        File.Copy(tempPath, file.Path.LocalPath, overwrite: true);
                        ShowStatusMessage(string.Format(Strings.Get("MainWindow_PdfSalvato"), file.Path.LocalPath));
                    }
                    catch (Exception ex)
                    {
                        await ShowError(string.Format(Strings.Get("MainWindow_ErroreSalvataggio"), ex.Message));
                    }
                }
            }

            try { File.Delete(tempPath); } catch { /* file temporaneo, ignora eventuali errori */ }
        }

        // ---------------------------------------------------------------
        // Gestione gruppi POI e POI (azioni dall'albero di navigazione)
        // ---------------------------------------------------------------
        private async Task<PoiGroup?> OnNewPoiGroup()
        {
            var newGroup = new PoiGroup { Id = _poiSvc.GetNextGroupId(_project.PoiGroups) };
            var win = new PoiGroupEditWindow(newGroup);
            await win.ShowDialog(this);
            if (!win.Confirmed) return null;

            _project.PoiGroups.Add(win.ResultGroup);
            _navCollapsedGroupIds.Remove(win.ResultGroup.Id);
            TouchPoiGroup(win.ResultGroup.Id);
            _isDirty = true;
            var createdGroup = win.ResultGroup;
            PushUndo(
                undo: () => _project.PoiGroups.Remove(createdGroup),
                redo: () => { if (!_project.PoiGroups.Contains(createdGroup)) _project.PoiGroups.Add(createdGroup); });
            RefreshNavigationTree();
            _mapCanvas?.InvalidateVisual();
            return win.ResultGroup;
        }

        private async Task OnEditPoiGroup(PoiGroup group)
        {
            var win = new PoiGroupEditWindow(group);
            await win.ShowDialog(this);
            if (!win.Confirmed) return;

            int idx = _project.PoiGroups.FindIndex(g => g.Id == group.Id);
            if (idx >= 0) _project.PoiGroups[idx] = win.ResultGroup;
            TouchPoiGroup(group.Id);
            _isDirty = true;
            RefreshNavigationTree();
            _mapCanvas?.InvalidateVisual();
        }

        private void OnDeletePoiGroup(PoiGroup group)
        {
            int idx = _project.PoiGroups.IndexOf(group);
            _project.PoiGroups.Remove(group);
            _navCollapsedGroupIds.Remove(group.Id);
            _hiddenPoiGroupIds.Remove(group.Id);
            _isDirty = true;
            PushUndo(
                undo: () => { if (!_project.PoiGroups.Contains(group)) _project.PoiGroups.Insert(Math.Min(idx, _project.PoiGroups.Count), group); },
                redo: () => _project.PoiGroups.Remove(group));
            RefreshNavigationTree();
            _mapCanvas?.InvalidateVisual();
        }

        // Avvia la modalità "aggiungi POI": il prossimo click sulla mappa apre
        // il dialog di creazione, precompilato con le coordinate cliccate
        private void StartAddPoiMode(PoiGroup group)
        {
            CancelAllAddModes();
            _addPoiMode        = true;
            _addPoiTargetGroup = group;
            _mapCanvas?.InvalidateVisual();
        }

        // Avvia la modalità "estendi percorso": ogni click sulla mappa
        // aggiunge un punto a una delle due estremità del percorso indicato
        private void StartAddRoutePointsMode(Percorso route)
        {
            CancelAllAddModes();
            _addRoutePointsMode         = true;
            _addRoutePointsTarget       = route;
            _addRoutePointsSessionCount = 0;
            _mapCanvas?.InvalidateVisual();
        }

        private void AddPoiAtLocation(PoiGroup group, double lon, double lat)
        {
            var newItem = new PoiItem
            {
                Id    = _poiSvc.GetNextItemId(group),
                Label = $"POI{GetNextPoiLabelNumber()}",
                Lon   = lon,
                Lat   = lat
            };
            group.Items.Add(newItem);
            _navCollapsedGroupIds.Remove(group.Id);
            TouchPoiGroup(group.Id);
            _isDirty = true;
            PushUndo(
                undo: () => group.Items.Remove(newItem),
                redo: () => { if (!group.Items.Contains(newItem)) group.Items.Add(newItem); });
            RefreshNavigationTree();
            _mapCanvas?.InvalidateVisual();
        }

        // Conferma un risultato di ricerca POI online: SOLO ora (non prima,
        // durante la ricerca) determina/crea il gruppo di destinazione, poi
        // aggiunge il nuovo PoiItem (etichetta = primo segmento del nome
        // restituito da Nominatim, descrizione = indirizzo completo). Non esce
        // dalla modalità di ricerca: toglie solo il marker appena confermato,
        // così si possono aggiungere più risultati di seguito; esce da sola
        // quando i risultati finiscono, oppure con ESC/tasto destro/qualsiasi
        // altra azione nel frattempo
        private void ConfirmPoiSearchResult(PoiSearchService.Result result)
        {
            // Se ResolvePoiSearchTargetGroup crea un gruppo al volo (nessun
            // gruppo esistente), l'aggiunta del POI e la creazione del gruppo
            // sono UN solo gesto dell'utente (un click sul marker): un solo
            // Ctrl+Z deve annullare entrambi insieme, non solo il POI lasciando
            // un gruppo vuoto orfano — da qui il confronto pre/post invece di
            // due PushUndo separati.
            var existingGroupIds = _project.PoiGroups.Select(g => g.Id).ToHashSet();
            var target = ResolvePoiSearchTargetGroup();
            bool groupWasCreated = !existingGroupIds.Contains(target.Id);

            string label = SanitizeSearchLabel(result.DisplayName);
            // Descrizione su più righe: nome, poi (per città) la popolazione
            // in Address, poi tutti i tag OSM grezzi di Details (uno per
            // riga, spezzati a ogni ";" — vedi PoiSearchService.
            // BuildOsmTagsString) quando la ricerca li ha trovati. Non per la
            // ricerca indirizzo: lì DisplayName è già l'indirizzo completo di
            // Nominatim, riaggiungere Address ripeterebbe la stessa
            // informazione due volte.
            var descriptionLines = new List<string> { result.DisplayName };
            if (!_poiSearchResultsAreAddresses && !string.IsNullOrWhiteSpace(result.Address))
                descriptionLines.Add(result.Address!);
            if (!string.IsNullOrWhiteSpace(result.Details))
                descriptionLines.AddRange(result.Details!.Split(';').Select(s => s.Trim()));
            string description = string.Join("\n", descriptionLines);

            var item = new PoiItem
            {
                Id          = _poiSvc.GetNextItemId(target),
                Label       = label,
                Description = description,
                Lon         = result.Lon,
                Lat         = result.Lat
            };
            target.Items.Add(item);
            _navCollapsedGroupIds.Remove(target.Id);
            TouchPoiGroup(target.Id);
            _isDirty = true;
            PushUndo(
                undo: () =>
                {
                    target.Items.Remove(item);
                    if (groupWasCreated) _project.PoiGroups.Remove(target);
                },
                redo: () =>
                {
                    if (groupWasCreated && !_project.PoiGroups.Contains(target)) _project.PoiGroups.Add(target);
                    if (!target.Items.Contains(item)) target.Items.Add(item);
                });

            _poiSearchResults.Remove(result);
            bool moreLeft = _poiSearchResults.Count > 0;
            if (!moreLeft)
            {
                _poiSearchMode = false;
                HidePoiSearchBox();
            }

            _suppressPoiSearchAutoExit = true;
            RefreshNavigationTree();
            _suppressPoiSearchAutoExit = false;

            _mapCanvas?.InvalidateVisual();
            ShowStatusMessage(string.Format(Strings.Get("MainWindow_AggiuntoAlGruppo"), label, target.Name) +
                (moreLeft ? string.Format(Strings.Get("MainWindow_AltriRisultatiSullaMappa"), _poiSearchResults.Count) : ""));
        }

        // Estrae dal display_name di Nominatim (es. "Macelleria Rossi, Via Roma
        // 12, ...") solo il primo segmento, da usare come etichetta breve del POI
        private static string SanitizeSearchLabel(string displayName)
        {
            string first = (displayName ?? "").Split(',')[0].Trim();
            if (first.Length > 40) first = first.Substring(0, 40).TrimEnd() + "…";
            return string.IsNullOrWhiteSpace(first) ? Strings.Get("MainWindow_PoiLabelDefault") : first;
        }

        // Trova il numero progressivo successivo per l'etichetta automatica
        // "POI<n>" guardando il massimo già usato in tutto il progetto (così
        // da non riutilizzare numeri di POI eliminati in precedenza)
        private int GetNextPoiLabelNumber()
        {
            int max = 0;
            foreach (var g in _project.PoiGroups)
                foreach (var it in g.Items)
                {
                    var m = Regex.Match(it.Label ?? "", @"^POI(\d+)$");
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n > max)
                        max = n;
                }
            return max + 1;
        }

        private async Task OnEditPoiItem(PoiGroup group, PoiItem item)
        {
            var win = new PoiItemEditWindow(item, _viewCenterLon, _viewCenterLat);
            await win.ShowDialog(this);
            if (!win.Confirmed) return;

            int idx = group.Items.FindIndex(it => it.Id == item.Id);
            if (idx >= 0) group.Items[idx] = win.ResultItem;
            TouchPoiGroup(group.Id);
            _isDirty = true;
            RefreshNavigationTree();
            _mapCanvas?.InvalidateVisual();
        }

        private void OnDeletePoiItem(PoiGroup group, PoiItem item)
        {
            int idx = group.Items.IndexOf(item);
            group.Items.Remove(item);
            _multiSelectedPoiKeys.Remove((group.Id, item.Id));
            _isDirty = true;
            PushUndo(
                undo: () => { if (!group.Items.Contains(item)) group.Items.Insert(Math.Min(idx, group.Items.Count), item); },
                redo: () => group.Items.Remove(item));
            RefreshNavigationTree();
            _mapCanvas?.InvalidateVisual();
        }

        // Esporta i gruppi POI nel formato scelto dall'utente (KMZ zippato
        // con icone incorporate, KML grezzo con solo il colore del gruppo, o
        // GPX come lista piatta di waypoint): il formato è dedotto
        // dall'estensione del file scelto/digitato nel dialog di salvataggio.
        private async Task OnExportKmz()
        {
            if (_project.PoiGroups.Count == 0)
            {
                ShowStatusMessage(Strings.Get("MainWindow_NessunGruppoDaEsportare"), isError: true);
                return;
            }

            var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title             = Strings.Get("MainWindow_EsportaPoiTitolo"),
                DefaultExtension  = "kmz",
                SuggestedFileName = "poi",
                FileTypeChoices   = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType(Strings.Get("MainWindow_FiltroKmzIcone")) { Patterns = new[] { "*.kmz" } },
                    new Avalonia.Platform.Storage.FilePickerFileType(Strings.Get("MainWindow_FiltroKml"))                  { Patterns = new[] { "*.kml" } },
                    new Avalonia.Platform.Storage.FilePickerFileType(Strings.Get("MainWindow_FiltroGpx"))                  { Patterns = new[] { "*.gpx" } }
                }
            });
            if (file == null) return;
            string path = file.Path.LocalPath;

            try
            {
                switch (Path.GetExtension(path).ToLowerInvariant())
                {
                    case ".kml": await _poiSvc.ExportKmlAsync(_project.PoiGroups, path); break;
                    case ".gpx": await _poiSvc.ExportGpxAsync(_project.PoiGroups, path); break;
                    default:     await _poiSvc.ExportKmzAsync(_project.PoiGroups, path); break;
                }
                ShowStatusMessage(string.Format(Strings.Get("MainWindow_Esportato"), path));
            }
            catch (Exception ex)
            {
                await ShowError(string.Format(Strings.Get("MainWindow_ErroreEsportazione"), ex.Message));
            }
        }

        // ---------------------------------------------------------------
        // Gestione percorsi (azioni dall'albero di navigazione)
        // ---------------------------------------------------------------

        // Avvia la modalità "disegna percorso": ogni click aggiunge un punto,
        // doppio click conclude e apre RouteEditWindow per rifinire i dettagli.
        private void OnNewPercorso()
        {
            CancelAllAddModes();
            _drawingRoute = new Percorso { Id = 0, ColorHex = PercorsoRenderer.DefaultColorHex };
            _addRouteMode = true;
            _mapCanvas?.InvalidateVisual();
        }

        private async Task OnEditPercorso(Percorso route)
        {
            var win = new RouteEditWindow(route, _viewCenterLon, _viewCenterLat);
            await win.ShowDialog(this);
            if (!win.Confirmed) return;

            int idx = _project.Percorsi.FindIndex(r => r.Id == route.Id);
            if (idx >= 0) _project.Percorsi[idx] = win.ResultRoute;
            TouchPercorso(route.Id);
            _isDirty = true;
            RefreshNavigationTree();
            _mapCanvas?.InvalidateVisual();
        }

        private void OnDeletePercorso(Percorso route)
        {
            int idx = _project.Percorsi.IndexOf(route);
            _project.Percorsi.Remove(route);
            _hiddenPercorsoIds.Remove(route.Id);
            _isDirty = true;
            PushUndo(
                undo: () => { if (!_project.Percorsi.Contains(route)) _project.Percorsi.Insert(Math.Min(idx, _project.Percorsi.Count), route); },
                redo: () => _project.Percorsi.Remove(route));
            RefreshNavigationTree();
            _mapCanvas?.InvalidateVisual();
        }

        // Legge i byte del file scelto nel picker provando sia lo Stream
        // fornito da Avalonia sia (come fallback) la lettura diretta dal
        // path locale, con un paio di tentativi: alcuni backend dei file
        // picker su Linux (xdg-desktop-portal, mount FUSE del document
        // portal, condivisioni di rete) possono restituire transitoriamente
        // uno Stream o un path locale vuoti nell'istante subito dopo la
        // selezione, prima che il contenuto reale sia effettivamente pronto.
        private static async Task<byte[]> ReadPickedFileBytesAsync(Avalonia.Platform.Storage.IStorageFile file)
        {
            string? localPath = file.TryGetLocalPath();
            long streamLength = -1;
            bool streamSeekable = false;
            long fileInfoLength = -1;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                await using (var stream = await file.OpenReadAsync())
                using (var buffer = new MemoryStream())
                {
                    streamSeekable = stream.CanSeek;
                    if (stream.CanSeek) streamLength = stream.Length;
                    await stream.CopyToAsync(buffer);
                    if (buffer.Length > 0) return buffer.ToArray();
                }

                if (localPath != null && File.Exists(localPath))
                {
                    fileInfoLength = new FileInfo(localPath).Length;
                    if (fileInfoLength > 0)
                    {
                        byte[] raw = await File.ReadAllBytesAsync(localPath);
                        if (raw.Length > 0) return raw;
                    }
                }

                if (attempt < 2) await Task.Delay(150);
            }

            throw new InvalidDataException(string.Format(
                Strings.Get("MainWindow_FileVuotoDiagnostica"),
                file.Name, localPath ?? "null", streamSeekable, streamLength, fileInfoLength));
        }

        // Import unico da KMZ/KML/GPX (menu principale, non più duplicato per
        // ramo): un file può contenere sia gruppi di POI (Placemark con
        // <Point>, o <wpt> in GPX) sia percorsi (Placemark con <LineString>,
        // o <trk>/<rte> in GPX), anche mescolati nello stesso documento —
        // entrambi i parser leggono lo stesso file e ignorano silenziosamente
        // ciò che non li riguarda, quindi basta eseguirli entrambi e sommare
        // i risultati. Accetta .kmz (zip), .kml/.gpx grezzi non compressi
        // (vedi PoiService/PercorsoService).
        private async void OnImportKmzUnified(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title          = Strings.Get("MainWindow_ImportaPoiPercorsiTitolo"),
                AllowMultiple  = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType(Strings.Get("MainWindow_FiltroKmzKmlGpx")) { Patterns = new[] { "*.kmz", "*.kml", "*.gpx" } },
                    new Avalonia.Platform.Storage.FilePickerFileType(Strings.Get("MainWindow_FiltroTuttiFile")) { Patterns = new[] { "*.*" } }
                }
            });
            if (files.Count == 0) return;
            await ImportFromFileAsync(files[0]);
        }

        // Estensioni accettate per l'importazione, sia dal file picker (vedi
        // sopra) sia dal drag&drop (vedi OnWindowDrop): un solo posto da
        // aggiornare se in futuro se ne aggiungono altre
        private static readonly string[] ImportableExtensions = { ".kmz", ".kml", ".gpx" };

        // Logica di importazione condivisa da toolbar (file picker) e
        // drag&drop: entrambe risolvono a un IStorageFile e la passano qui
        private async Task ImportFromFileAsync(Avalonia.Platform.Storage.IStorageFile file)
        {
            string fileName = file.Name;
            string fileNameHint = Path.GetFileNameWithoutExtension(fileName);

            try
            {
                byte[] raw = await ReadPickedFileBytesAsync(file);

                var importedGroups = _poiSvc.ImportKmz(raw, _project, fileNameHint);
                var importedRoutes = _percorsoSvc.ImportKmz(raw, _project);

                if (importedGroups.Count == 0 && importedRoutes.Count == 0)
                {
                    ShowStatusMessage(Strings.Get("MainWindow_NessunGruppoPercorsoTrovato"), isError: true);
                    return;
                }

                // Dati importati da fonte esterna: bloccati subito, per
                // evitare di spostarli accidentalmente prima ancora di averli
                // controllati (si sbloccano manualmente col lucchetto quando serve)
                foreach (var g in importedGroups) g.IsLocked = true;
                foreach (var r in importedRoutes) r.IsLocked = true;

                if (importedGroups.Count > 0) _project.PoiGroups.AddRange(importedGroups);
                if (importedRoutes.Count > 0) _project.Percorsi.AddRange(importedRoutes);
                _isDirty = true;
                PushUndo(
                    undo: () =>
                    {
                        foreach (var g in importedGroups) _project.PoiGroups.Remove(g);
                        foreach (var r in importedRoutes) _project.Percorsi.Remove(r);
                    },
                    redo: () =>
                    {
                        foreach (var g in importedGroups) if (!_project.PoiGroups.Contains(g)) _project.PoiGroups.Add(g);
                        foreach (var r in importedRoutes) if (!_project.Percorsi.Contains(r)) _project.Percorsi.Add(r);
                    });
                RefreshNavigationTree();
                _mapCanvas?.InvalidateVisual();

                var parts = new List<string>();
                if (importedGroups.Count > 0)
                    parts.Add(string.Format(Strings.Get("MainWindow_ImportatiGruppiPoiConteggio"), importedGroups.Count, importedGroups.Sum(g => g.Items.Count)));
                if (importedRoutes.Count > 0)
                    parts.Add(string.Format(Strings.Get("MainWindow_ImportatiPercorsiConteggio"), importedRoutes.Count));
                ShowStatusMessage(string.Format(Strings.Get("MainWindow_ImportatiRiepilogo"), string.Join(", ", parts)));
            }
            catch (Exception ex)
            {
                string detail = ex.InnerException != null
                    ? $"{ex.GetType().Name}: {ex.Message}\n({ex.InnerException.GetType().Name}: {ex.InnerException.Message})"
                    : $"{ex.GetType().Name}: {ex.Message}";
                await ShowError(string.Format(Strings.Get("MainWindow_ErroreImportazioneDi"), fileName, detail));
            }
        }

        // Drag&drop di un file KMZ/KML/GPX sulla finestra: stessa logica di
        // importazione del pulsante toolbar, senza passare dal file picker.
        // Registrato sulla finestra intera (non solo sul canvas mappa) così
        // funziona anche rilasciando il file sul pannello di navigazione.
        private void OnWindowDragOver(object? sender, DragEventArgs e)
        {
            e.DragEffects = IsImportableDrag(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void OnWindowDrop(object? sender, DragEventArgs e)
        {
            e.Handled = true;
            if (!IsImportableDrag(e)) return;

            var file = e.Data.GetFiles()?
                .OfType<Avalonia.Platform.Storage.IStorageFile>()
                .FirstOrDefault(f => ImportableExtensions.Contains(Path.GetExtension(f.Name).ToLowerInvariant()));
            if (file == null) return;

            await ImportFromFileAsync(file);
        }

        private static bool IsImportableDrag(DragEventArgs e) =>
            e.Data.GetFiles()?
                .OfType<Avalonia.Platform.Storage.IStorageFile>()
                .Any(f => ImportableExtensions.Contains(Path.GetExtension(f.Name).ToLowerInvariant())) ?? false;

        // Esporta i percorsi nel formato scelto dall'utente (KMZ, KML grezzo,
        // o GPX come <trk>); il formato è dedotto dall'estensione del file
        // scelto/digitato nel dialog di salvataggio.
        private async Task OnExportPercorsiKmz()
        {
            if (_project.Percorsi.Count == 0)
            {
                ShowStatusMessage(Strings.Get("MainWindow_NessunPercorsoDaEsportare"), isError: true);
                return;
            }

            var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title             = Strings.Get("MainWindow_EsportaPercorsiTitolo"),
                DefaultExtension  = "kmz",
                SuggestedFileName = "percorsi",
                FileTypeChoices   = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType(Strings.Get("MainWindow_FiltroKmzZip")) { Patterns = new[] { "*.kmz" } },
                    new Avalonia.Platform.Storage.FilePickerFileType(Strings.Get("MainWindow_FiltroKml"))       { Patterns = new[] { "*.kml" } },
                    new Avalonia.Platform.Storage.FilePickerFileType(Strings.Get("MainWindow_FiltroGpx"))       { Patterns = new[] { "*.gpx" } }
                }
            });
            if (file == null) return;
            string path = file.Path.LocalPath;

            try
            {
                switch (Path.GetExtension(path).ToLowerInvariant())
                {
                    case ".kml": await _percorsoSvc.ExportKmlAsync(_project.Percorsi, path); break;
                    case ".gpx": await _percorsoSvc.ExportGpxAsync(_project.Percorsi, path); break;
                    default:     await _percorsoSvc.ExportKmzAsync(_project.Percorsi, path); break;
                }
                ShowStatusMessage(string.Format(Strings.Get("MainWindow_Esportato"), path));
            }
            catch (Exception ex)
            {
                await ShowError(string.Format(Strings.Get("MainWindow_ErroreEsportazione"), ex.Message));
            }
        }

        private async void OnOpenSettings(object? sender, RoutedEventArgs e)
        {
            var win = new SettingsWindow(_project.Settings, PoiSearchService.CustomCategories);
            await win.ShowDialog(this);

            if (win.Confirmed)
            {
                bool serverChanged = win.ResultSettings.TileServerUrl != _project.Settings.TileServerUrl
                    || win.ResultSettings.TileServerApiKey != _project.Settings.TileServerApiKey;

                _project.Settings = win.ResultSettings;

                // Le chiavi API sono credenziali dell'utente, non del progetto:
                // salvate anche a parte così restano disponibili di default sui
                // prossimi progetti nuovi/aperti (vedi ApplyGlobalPreferences)
                _appPrefsSvc.Save(_project.Settings.GroqApiKey, _project.Settings.TileServerApiKey);

                // Categorie POI personalizzate (tab "Categorie POI"): stesso
                // pattern, persistite globalmente e riapplicate subito al
                // combo di ricerca (nuove aggiunte visibili senza riavviare).
                _appPrefsSvc.SaveCustomPoiCategories(win.ResultCustomCategories);
                PoiSearchService.SetCustomCategories(win.ResultCustomCategories);
                RefreshCategoryCombo();

                // Lingua interfaccia: persistita subito così resta effettiva
                // anche se l'utente chiude l'app senza toccare altro, ma non
                // applicata a caldo (l'intera UI già costruita ha le stringhe
                // "cotte" nei controlli esistenti) — richiede un riavvio,
                // segnalato nella nota sotto il combo in SettingsWindow.
                _appPrefsSvc.SaveLanguage(win.ResultLanguage);
                StradarioApp.Resources.Strings.SetLanguage(win.ResultLanguage);

                foreach (var p in _project.Pages)
                    p.GeoBounds = GeoUtils.CalcPageBounds(
                        p.GeoBounds.CenterLon, p.GeoBounds.CenterLat, _project.Settings);
                _isDirty = true;

                // Se il tile server è cambiato, svuota la cache per ricaricare i nuovi tile
                // e riporta lo zoom corrente entro il massimo supportato dal nuovo server:
                // altrimenti la mappa resta sui tile del vecchio zoom (troppo alto per il
                // nuovo server), che non esistono e non vengono mai caricati.
                if (serverChanged)
                {
                    _renderer.ClearCache();
                    double maxZoom = TileServers.GetMaxZoom(_project.Settings.TileServerUrl);
                    if (_viewZoom > maxZoom)
                        _viewZoom = maxZoom;
                }

                RefreshNavigationTree();
                _mapCanvas?.InvalidateVisual();
            }
        }

        private async void OnOpenAbout(object? sender, RoutedEventArgs e)
        {
            var win = new AboutWindow();
            await win.ShowDialog(this);
        }

        // ---------------------------------------------------------------
        // Utilità UI
        // ---------------------------------------------------------------
        private Button MakeButton(string text, EventHandler<RoutedEventArgs> handler)
        {
            var btn = new Button
            {
                Content = text,
                Padding = new Thickness(8, 4),
                FontSize = 12
            };
            btn.Click += handler;
            return btn;
        }

        // Bottone "OK" centrato usato dai dialog di errore/informazione
        // (il chiamante collega ancora Click a dlg.Close())
        private Button CenteredOkButton()
        {
            var btn = DialogUi.MakeDialogButton(Strings.Get("MainWindow_Ok"), primary: true);
            btn.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            return btn;
        }

        private void UpdateTitle()
        {
            string file   = _currentFilePath != null ? Path.GetFileName(_currentFilePath) : Strings.Get("MainWindow_SenzaTitolo");
            string dirty  = _isDirty ? " •" : "";
            Title = string.Format(Strings.Get("MainWindow_TitoloConProgetto"), _project.ProjectName, file, dirty);
            UpdateStatusBarSummary();
        }

        private async Task ShowError(string message)
        {
            var dlg = new Window
            {
                Title   = Strings.Get("MainWindow_ErroreTitolo"),
                Width   = 420,
                Height  = 190,
                Content = new StackPanel
                {
                    Margin  = new Thickness(18),
                    Spacing = 16,
                    Children =
                    {
                        new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                        CenteredOkButton()
                    }
                }
            };

            var okBtn = ((StackPanel)dlg.Content).Children.OfType<Button>().First();
            okBtn.Click += (s, e) => dlg.Close();
            await dlg.ShowDialog(this);
        }

    }
}
