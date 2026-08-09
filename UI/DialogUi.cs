// =============================================================================
// UI/DialogUi.cs
//
// SINOSSI: Helper condivisi per uno stile coerente dei controlli della UI.
//   - MakeDialogButton: bottoni OK/Annulla/azione dei dialog, tutti della
//     stessa altezza e con spazio sufficiente per il testo (MinWidth, non
//     Width fissa, così le etichette più lunghe non vengono tagliate).
//   - MakeTreeIconButton: icone di azione compatte ma ben leggibili usate
//     nell'albero di navigazione (aggiungi/modifica/elimina/mostra-nascondi),
//     disegnate come Path vettoriale da un path SVG (BootstrapIcons) invece
//     che come glifo emoji: stesso trattamento della toolbar, per coerenza.
// =============================================================================

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Calendar = Avalonia.Controls.Calendar;

namespace StradarioApp.UI
{
    // Coppia data+ora opzionale per un campo Da/A. La parte data è un
    // Button (aspetto testo semplice, "gg/mm/aaaa" finché vuoto) che apre
    // un Flyout con un Calendar pieno — NON il controllo CalendarDatePicker
    // nativo di Avalonia: quello ha un bottone "apri calendario" col
    // proprio glifo incorporato nel template (una mini-icona calendario col
    // giorno corrente) che risulta schiacciato contro il bordo destro senza
    // possibilità di aggiungere spazio via proprietà pubbliche (Width/
    // Padding non lo spostano, è ancorato dal template stesso) — segnalato
    // dall'utente come sgradevole anche dopo aver allargato il controllo.
    // Stesso discorso per l'ora: prima il TimePicker nativo (bordo
    // incompleto), poi una coppia di NumericUpDown (a Width stretta
    // troncavano il numero, es. "14" mostrato come "1" — le frecce ^/v del
    // template sono affiancate, non impilate in una colonna stretta) —
    // sostituiti definitivamente da una "bottoniera": un Button "hh:mm" che
    // apre un Flyout con una griglia di 24 bottoni ora (0-23) e una griglia
    // di 60 bottoni minuto (0-59), spazio non è un problema. Entrambi i
    // bottoni Da/A qui sotto sono completamente sotto il nostro controllo
    // visivo, nessun glifo/bordo incorporato di terzi.
    //
    // GetTime/SetTime/TimeChanged sono disaccoppiati dall'implementazione
    // concreta del widget ora (passati come delegati a MakeDateTimeFieldPair)
    // apposta: è già cambiata due volte in questa stessa sessione
    // (TimePicker → NumericUpDown → bottoniera) e disaccoppiare evita di
    // dover ritoccare anche PoiItemEditWindow/RouteEditWindow o
    // CombineDateTimeFields/WireAutoFillSecondFromFirst ad ogni cambio.
    internal sealed class DateTimeFieldPair
    {
        public Button   DateButton { get; }
        public Calendar Calendar   { get; }
        public Button   TimeButton { get; }
        public Control  Panel      { get; }

        private readonly Func<TimeSpan>   _getTime;
        private readonly Action<TimeSpan> _setTime;
        private readonly Func<bool>       _hasTime;
        private readonly Action           _clearTime;

        // Notifica ogni cambio d'ora (click su un bottone ora/minuto O
        // SetTime esterno) — usato da WireAutoFillSecondFromFirst per far
        // seguire "A" a "Da" mentre "A" non ha ancora una data propria.
        public event Action<TimeSpan>? TimeChanged;
        internal void RaiseTimeChanged(TimeSpan t) => TimeChanged?.Invoke(t);

        public DateTimeFieldPair(Button dateButton, Calendar calendar, Button timeButton,
            Func<TimeSpan> getTime, Action<TimeSpan> setTime, Func<bool> hasTime, Action clearTime, Control panel)
        {
            DateButton = dateButton;
            Calendar   = calendar;
            TimeButton = timeButton;
            _getTime   = getTime;
            _setTime   = setTime;
            _hasTime   = hasTime;
            _clearTime = clearTime;
            Panel      = panel;
        }

        // Imposta la data (null = nessuna data): aggiorna il Calendar, il
        // che a sua volta (via PropertyChanged wired in MakeDateTimeFieldPair)
        // aggiorna anche il testo/colore del bottone.
        public void SetDate(DateTime? date) => Calendar.SelectedDate = date;

        public TimeSpan GetTime() => _getTime();
        public void SetTime(TimeSpan t) => _setTime(t);

        // true se l'utente ha scelto esplicitamente un orario (bottoniera o
        // SetTime), false se è ancora il watermark "hh:mm" — usato da
        // WireAutoFillSecondFromFirst per copiare anche lo stato "nessun
        // orario", non solo il valore 00:00 che lo rappresenta.
        public bool HasTime => _hasTime();
        public void ClearTime() => _clearTime();
    }

    internal static class DialogUi
    {
        // Altezza e larghezza minima uniformi per tutti i bottoni dei dialog
        public const double ButtonHeight   = 34;
        public const double ButtonMinWidth = 96;

        public static Button MakeDialogButton(string text, bool primary = false)
        {
            return new Button
            {
                Content    = text,
                Height     = ButtonHeight,
                MinWidth   = ButtonMinWidth,
                Padding    = new Avalonia.Thickness(16, 0),
                FontSize   = 13,
                FontWeight = primary ? FontWeight.Bold : FontWeight.Normal,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment   = VerticalAlignment.Center
            };
        }

        // Icona di azione quadrata usata nell'albero di navigazione (pannello
        // sinistro): dimensione fissa, ben visibile, con tooltip esplicativo.
        // svgPathData è uno dei path in UI/BootstrapIcons.cs. "enabled" = false
        // disabilita davvero il click (IsEnabled) E lo segnala visivamente con
        // opacità ridotta — usato per le azioni di un gruppo POI/percorso
        // nascosto (vedi BuildPoiGroupNavHeader/BuildPercorsoNavItem: "se non
        // lo vedi non lo puoi toccare"), dove il tema di base non applicherebbe
        // da solo uno stile "disabilitato" abbastanza evidente.
        public static Button MakeTreeIconButton(string svgPathData, string tooltip, IBrush foreground, Action onClick, double size = 24, bool enabled = true)
        {
            var icon = new Path
            {
                Data    = Geometry.Parse(svgPathData),
                Fill    = foreground,
                Width   = size * 0.62,
                Height  = size * 0.62,
                Stretch = Stretch.Uniform,
                Opacity = enabled ? 1.0 : 0.35
            };
            var btn = new Button
            {
                Content    = icon,
                Width      = size,
                Height     = size,
                Padding    = new Avalonia.Thickness(0),
                Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment   = VerticalAlignment.Center,
                Cursor     = new Cursor(enabled ? StandardCursorType.Hand : StandardCursorType.No),
                IsEnabled  = enabled
            };
            ToolTip.SetTip(btn, tooltip);
            btn.Click += (_, _) => onClick();
            return btn;
        }

        // Icona vettoriale puramente decorativa (non cliccabile), usata al
        // posto di un glifo emoji davanti a un'etichetta di testo (es. titoli
        // dei rami nell'albero di navigazione).
        public static Path MakeIconGlyph(string svgPathData, IBrush foreground, double size = 16)
        {
            return new Path
            {
                Data    = Geometry.Parse(svgPathData),
                Fill    = foreground,
                Width   = size,
                Height  = size,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        // Badge "C→W" / "W→C" per la correzione GCJ-02/WGS84 dei punti che
        // ricadono in Cina (v. Services/GcjTransform): stessa identica
        // dimensione/sfondo trasparente/stile di MakeTreeIconButton (niente
        // box colorato) — solo testo al posto di un Path, non essendoci un
        // glifo SVG sensato per "conversione di sistema di coordinate".
        public static Button MakeGcjBadgeButton(string text, string tooltip, IBrush foreground, Action onClick, double size = 24)
        {
            var label = new TextBlock
            {
                Text                = text,
                FontSize            = size * 0.34,
                FontWeight          = FontWeight.Bold,
                Foreground          = foreground,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            var btn = new Button
            {
                Content    = label,
                Width      = size,
                Height     = size,
                Padding    = new Avalonia.Thickness(0),
                Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment   = VerticalAlignment.Center,
                Cursor     = new Cursor(StandardCursorType.Hand)
            };
            ToolTip.SetTip(btn, tooltip);
            btn.Click += (_, _) => onClick();
            return btn;
        }

        // Bottone con icona vettoriale + etichetta di testo, stesso trattamento
        // (Path da BootstrapIcons) delle altre icone dell'app al posto
        // dell'emoji usata in precedenza per bottoni tipo "Usa centro vista mappa".
        public static Button MakeIconTextButton(string svgPathData, string text)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(MakeIconGlyph(svgPathData, Brushes.SteelBlue, 15));
            panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return new Button
            {
                Content = panel,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
        }

        // Dialog di conferma Sì/No generico, riusabile da qualunque finestra
        // (prima era una copia privata in MainWindow, usata per le richieste
        // GCJ-02/eliminazione pagine — spostato qui per poterlo chiamare
        // anche da un dialog figlio come RouteEditWindow, che non ha
        // accesso ai metodi privati di MainWindow).
        public static async System.Threading.Tasks.Task<bool> AskYesNo(Window owner, string title, string message, string yesLabel, string noLabel)
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
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 10,
                            Children =
                            {
                                MakeDialogButton(yesLabel, primary: true),
                                MakeDialogButton(noLabel)
                            }
                        }
                    }
                }
            };

            var btns = ((StackPanel)((StackPanel)dlg.Content!).Children[1]);
            ((Button)btns.Children[0]).Click += (_, _) => { confirmed = true;  dlg.Close(); };
            ((Button)btns.Children[1]).Click += (_, _) => { confirmed = false; dlg.Close(); };

            await dlg.ShowDialog(owner);
            return confirmed;
        }

        private static string FormatDateOrWatermark(DateTime? d) =>
            d.HasValue ? d.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "gg/mm/aaaa";

        // Coppia Button+Calendar-in-Flyout / TimePicker per un campo
        // data/ora opzionale (entrambi vuoti = nessuna data). Precompilata
        // da un DateTime? esistente; l'ora resta vuota se è mezzanotte
        // esatta (nessun orario impostato in precedenza), stessa
        // convenzione usata per la visualizzazione nell'albero
        // (MainWindow.FormatItineraryDate).
        public static DateTimeFieldPair MakeDateTimeFieldPair(DateTime? initial)
        {
            var dateBtn = new Button
            {
                Width                      = 130,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content                    = FormatDateOrWatermark(initial),
                Foreground                 = initial.HasValue ? Brushes.Black : Brushes.Gray
            };

            var calendar = new Calendar { SelectedDate = initial?.Date };
            // Dichiarato prima di clearBtn perché la sua lambda lo cattura
            // per riferimento (assegnato più sotto, una volta che
            // flyoutContent è pronto — schema standard per rompere la
            // dipendenza circolare flyout→contenuto→clearBtn→flyout.Hide()).
            Flyout flyout = null!;

            // Bottoni di scorciatoia +/-1 giorno/settimana/mese: spostano la
            // data corrente (o "oggi" se non ancora impostata) senza dover
            // navigare il calendario a mano — comodo per costruire in fretta
            // un itinerario su più giorni (POI/percorsi ravvicinati di 1
            // giorno l'uno dall'altro). Non chiudono il flyout, per poter
            // premere più volte di fila (es. "+1g" tre volte).
            DateTime AdjustBase() => calendar.SelectedDate ?? DateTime.Today;
            Button MakeAdjustButton(string label, Func<DateTime, DateTime> adjust)
            {
                var btn = new Button { Content = label, FontSize = 11, Padding = new Thickness(6, 3) };
                btn.Click += (_, _) => calendar.SelectedDate = adjust(AdjustBase());
                return btn;
            }
            var adjustRow = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                Spacing             = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    MakeAdjustButton("-1m", d => d.AddMonths(-1)),
                    MakeAdjustButton("-1s", d => d.AddDays(-7)),
                    MakeAdjustButton("-1g", d => d.AddDays(-1)),
                    MakeAdjustButton("+1g", d => d.AddDays(1)),
                    MakeAdjustButton("+1s", d => d.AddDays(7)),
                    MakeAdjustButton("+1m", d => d.AddMonths(1)),
                }
            };

            // "Nessuna data": icona X (stesso trattamento vettoriale delle
            // altre icone di azione dell'app), non più un bottone di testo.
            var clearBtn = MakeTreeIconButton(BootstrapIcons.Close, "Nessuna data", Brushes.Crimson, () =>
            {
                calendar.SelectedDate = null;
                flyout.Hide();
            });
            clearBtn.HorizontalAlignment = HorizontalAlignment.Center;

            var flyoutContent = new StackPanel
            {
                Spacing = 6,
                Margin  = new Thickness(4),
                Children = { calendar, adjustRow, clearBtn }
            };
            flyout = new Flyout { Content = flyoutContent };
            dateBtn.Flyout = flyout;

            // Il bottone si aggiorna da sé ad ogni cambio di SelectedDate
            // sul Calendar, sia che arrivi da un click dell'utente nel
            // popup (giorno o scorciatoia +/-), sia da
            // DateTimeFieldPair.SetDate (auto-fill "A" da "Da") — un solo
            // punto che tiene bottone e calendario sincroni.
            calendar.PropertyChanged += (_, e) =>
            {
                if (e.Property != Calendar.SelectedDateProperty) return;
                var d = calendar.SelectedDate;
                dateBtn.Content    = FormatDateOrWatermark(d);
                dateBtn.Foreground = d.HasValue ? Brushes.Black : Brushes.Gray;
            };
            calendar.PointerReleased += (_, _) =>
            {
                // Un click su un giorno del calendario chiude subito il
                // flyout (comodo, un solo click) — ma solo se ha davvero
                // selezionato una data (non un click sul cambio mese/anno).
                if (calendar.SelectedDate.HasValue) flyout.Hide();
            };

            // ---- Ora: stesso schema Button+Flyout della data, con una
            // "bottoniera" di tutte le 24 ore e tutti i 60 minuti. "Nessun
            // orario" (watermark "hh:mm") è uno stato ESPLICITO
            // (currentHasTime), non più dedotto da "ora == mezzanotte":
            // altrimenti impostare deliberatamente 00:00 come orario reale
            // era indistinguibile da "nessun orario" e il bottone tornava
            // sempre al watermark — bug segnalato dall'utente ("non posso
            // impostare a 00:00 un'ora"). Qualunque click su un bottone
            // ora/minuto marca currentHasTime=true; solo la X lo riporta a
            // false. GetTime() continua a restituire 00:00 quando
            // currentHasTime è false (il DateTime combinato risultante è lo
            // stesso, cambia solo cosa mostra il bottone).
            var initialTime = initial?.TimeOfDay ?? TimeSpan.Zero;
            int  currentHour    = initialTime.Hours;
            int  currentMinute  = initialTime.Minutes;
            bool currentHasTime = initial.HasValue && initialTime != TimeSpan.Zero;

            var timeBtn = new Button
            {
                Width                      = 80,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content                    = FormatTimeOrWatermark(currentHasTime, initialTime),
                Foreground                 = currentHasTime ? Brushes.Black : Brushes.Gray
            };

            var hourGrid   = new UniformGrid { Columns = 6 };
            var minuteGrid = new UniformGrid { Columns = 10 };

            DateTimeFieldPair pair = null!; // assegnato in fondo, v. nota sul pattern per "flyout" sopra

            Button MakeTimeCell(int value, bool selected, Action onClick)
            {
                var btn = new Button
                {
                    Content    = value.ToString("00"),
                    FontSize   = 11,
                    Padding    = new Thickness(0),
                    Margin     = new Thickness(1),
                    Background = selected ? new SolidColorBrush(Color.Parse("#1E88E5")) : Brushes.Transparent,
                    Foreground = selected ? Brushes.White : Brushes.Black
                };
                btn.Click += (_, _) => onClick();
                return btn;
            }

            void RebuildHourGrid()
            {
                hourGrid.Children.Clear();
                for (int h = 0; h < 24; h++)
                {
                    int hh = h;
                    hourGrid.Children.Add(MakeTimeCell(hh, currentHasTime && hh == currentHour,
                        () => SetTimeInternal(new TimeSpan(hh, currentMinute, 0))));
                }
            }
            void RebuildMinuteGrid()
            {
                minuteGrid.Children.Clear();
                for (int m = 0; m < 60; m++)
                {
                    int mm = m;
                    minuteGrid.Children.Add(MakeTimeCell(mm, currentHasTime && mm == currentMinute,
                        () => SetTimeInternal(new TimeSpan(currentHour, mm, 0))));
                }
            }

            void RefreshTimeButton()
            {
                timeBtn.Content    = FormatTimeOrWatermark(currentHasTime, new TimeSpan(currentHour, currentMinute, 0));
                timeBtn.Foreground = currentHasTime ? Brushes.Black : Brushes.Gray;
            }

            // Usato sia dai click sulla bottoniera sia da SetTime esterno
            // (DateTimeFieldPair.SetTime, es. l'auto-fill "A" da "Da"):
            // in entrambi i casi rappresenta un orario scelto/copiato
            // davvero, quindi marca sempre currentHasTime=true. Solo la X
            // (ClearTimeInternal) rappresenta un vero "nessun orario".
            void SetTimeInternal(TimeSpan t)
            {
                currentHour    = t.Hours;
                currentMinute  = t.Minutes;
                currentHasTime = true;
                RebuildHourGrid();
                RebuildMinuteGrid();
                RefreshTimeButton();
                pair.RaiseTimeChanged(t);
            }

            void ClearTimeInternal()
            {
                currentHour    = 0;
                currentMinute  = 0;
                currentHasTime = false;
                RebuildHourGrid();
                RebuildMinuteGrid();
                RefreshTimeButton();
            }

            RebuildHourGrid();
            RebuildMinuteGrid();

            var timeClearBtn = MakeTreeIconButton(BootstrapIcons.Close, "Nessun orario", Brushes.Crimson,
                ClearTimeInternal);
            timeClearBtn.HorizontalAlignment = HorizontalAlignment.Center;

            var timeFlyoutContent = new StackPanel
            {
                Spacing = 6,
                Margin  = new Thickness(4),
                Children =
                {
                    new TextBlock { Text = "Ore", FontWeight = FontWeight.Bold, FontSize = 11 },
                    hourGrid,
                    new TextBlock { Text = "Minuti", FontWeight = FontWeight.Bold, FontSize = 11 },
                    minuteGrid,
                    timeClearBtn
                }
            };
            timeBtn.Flyout = new Flyout { Content = timeFlyoutContent };

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 6,
                Children    = { dateBtn, timeBtn }
            };
            pair = new DateTimeFieldPair(dateBtn, calendar, timeBtn,
                () => new TimeSpan(currentHour, currentMinute, 0), SetTimeInternal,
                () => currentHasTime, ClearTimeInternal, panel);
            return pair;
        }

        private static string FormatTimeOrWatermark(bool hasTime, TimeSpan t) =>
            hasTime ? $"{t.Hours:00}:{t.Minutes:00}" : "hh:mm";

        // Combina i due campi in un DateTime? — null se non è impostata
        // nessuna data.
        public static DateTime? CombineDateTimeFields(DateTimeFieldPair pair)
        {
            if (pair.Calendar.SelectedDate is not { } date) return null;
            return date.Date.Add(pair.GetTime());
        }

        // Mentre il campo "A" (secondo) non ha ancora una data propria,
        // ricalca automaticamente quello che l'utente sceglie nel campo
        // "Da" (primo) — comodo per il caso più comune (un singolo istante,
        // non un range): l'utente valorizza solo "Da" e "A" segue da sé,
        // pronto per essere corretto a mano se invece serve davvero un
        // intervallo. Smette di seguire non appena l'utente sceglie una
        // data propria per "A" (a quel punto SelectedDate non è più null).
        //
        // Quando invece "Da" e "A" finiscono entrambi con una data propria
        // MA diversa (un range multi-giorno vero, non solo il duplicato
        // dell'auto-fill sopra, che li rende sempre uguali) — richiesta
        // esplicita dell'utente — gli orari vengono impostati automaticamente
        // a 00:00 (Da) e 23:59 (A): i confini naturali "dall'inizio del primo
        // giorno alla fine dell'ultimo", invece di lasciare un orario magari
        // copiato dal caso a singolo istante e ormai senza senso per un
        // range di più giorni. Si riapplica ad ogni cambio di una delle due
        // date finché restano diverse — un orario scelto a mano dopo resta
        // finché non si tocca di nuovo una delle due date.
        public static void WireAutoFillSecondFromFirst(DateTimeFieldPair first, DateTimeFieldPair second)
        {
            void ApplyRangeDefaultsIfNeeded()
            {
                var d1 = first.Calendar.SelectedDate;
                var d2 = second.Calendar.SelectedDate;
                if (d1.HasValue && d2.HasValue && d1.Value.Date != d2.Value.Date)
                {
                    first.SetTime(TimeSpan.Zero);
                    second.SetTime(new TimeSpan(23, 59, 0));
                }
            }

            first.Calendar.PropertyChanged += (_, e) =>
            {
                if (e.Property != Calendar.SelectedDateProperty) return;
                if (second.Calendar.SelectedDate == null)
                {
                    second.SetDate(first.Calendar.SelectedDate);
                    if (first.HasTime) second.SetTime(first.GetTime()); else second.ClearTime();
                }
                ApplyRangeDefaultsIfNeeded();
            };
            first.TimeChanged += t =>
            {
                if (second.Calendar.SelectedDate == null) second.SetTime(t);
            };
            second.Calendar.PropertyChanged += (_, e) =>
            {
                if (e.Property != Calendar.SelectedDateProperty) return;
                ApplyRangeDefaultsIfNeeded();
            };
        }
    }
}
