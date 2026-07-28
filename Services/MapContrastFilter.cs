// =============================================================================
// Services/MapContrastFilter.cs
//
// SINOSSI: Filtro di contrasto applicato al bitmap dei tile OSM SOLO in fase
//   di export PDF (mai sulla mappa interattiva a schermo), su richiesta
//   dell'utente (StradarioSettings.PdfContrastMode). Applicato in
//   PdfGenerator.RenderMapPageAsync sul bitmap composito dei tile, PRIMA che
//   vengano disegnati percorsi e POI sopra.
//
//   Motivazione: lo stile "OSM Carto" standard distingue le aree (edifici,
//   verde, residenziale...) soprattutto per TONALITÀ (hue) a parità di
//   luminosità quasi identica (riempimenti pastello tutti intorno a
//   L≈0.80–0.95). In stampa B/N una conversione a scala di grigi "ingenua"
//   (media/luminanza) appiattisce quasi tutto sullo stesso grigio chiaro,
//   rendendo la mappa illeggibile: analizzando un tile reale a zoom urbano,
//   i riempimenti (edifici #D8CFC9, residenziale #E7D9D4/#EDEBD5, strade
//   principali #EEA89B) cadono tutti in una fascia di luminosità molto
//   stretta, mentre i bordi/testo (#010101, #5F5F5E, #343433) sono già
//   scuri. Serve quindi stirare quella fascia intermedia, non solo
//   desaturare.
//
//   RoadEmphasis va oltre: BlackWhite/Color stirano solo la LUMINANZA, ma nei
//   toni chiari di OSM Carto le strade principali/secondarie (arancio/rosso,
//   es. #EEA89B, satur. ~71%) cadono nella STESSA fascia di luminosità di
//   edifici/residenziale (#D8CFC9/#E7D9D4/#EDEBD5) e dell'acqua (#AAD3DF) —
//   uno stiramento per luminanza non può separarli, e nemmeno la sola
//   saturazione basta: un riempimento residenziale reale (#EDEBD5) arriva a
//   satur. 40%, vicino al 71% delle strade. RoadEmphasis lavora invece
//   pixel per pixel in spazio HSL con soglie verificate sui campioni reali:
//   i toni quasi neutri (bassa saturazione) ma non troppo scuri, i toni
//   chiari blu/verdi (acqua, parchi/verde) e i riempimenti caldi ma non
//   abbastanza saturi da essere strada vengono spinti verso il bianco;
//   i toni caldi (arancio/rosso/giallo) con saturazione ben sopra quella dei
//   riempimenti campionati vengono resi più saturi e scuriti per risaltare
//   sul nuovo sfondo quasi bianco; i toni quasi neutri E scuri (bordi
//   strada, testo — incluso un grigio bordo reale con luminosità ~37%, sopra
//   il taglio "scuro" usato da BlackWhite) restano invariati. Funziona con
//   qualunque tile server in stile "OSM Carto" (non solo la fonte di
//   default), perché la classificazione si basa su tonalità/saturazione
//   relative, non su colori esatti di un singolo provider.
//
//   Tre funzioni aggiuntive (vedi i rispettivi metodi più sotto per il
//   dettaglio, incluse le tarature verificate su tile OSM reali):
//   AdaptiveContrast (CLAHE locale, alternativa a BlackWhite/Color che resta
//   a colori), ApplyEdgeReinforcement (Sobel, applicabile SOPRA qualunque
//   PdfContrastMode — non è una modalità alternativa) e
//   ApplyFloydSteinbergDither (retinatura per stampa B/N vera, applicabile
//   solo quando la modalità scelta è BlackWhite).
// =============================================================================

using System;
using SkiaSharp;
using StradarioApp.Models;

namespace StradarioApp.Services
{
    public static class MapContrastFilter
    {
        // Pesi di luminanza percettiva (Rec.709). ATTENZIONE: a differenza della
        // ColorMatrix di Android, SkiaSharp/Skia opera sui componenti colore
        // normalizzati in 0..1 (bias incluso), non in 0..255.
        private const float LumaR = 0.2126f;
        private const float LumaG = 0.7152f;
        private const float LumaB = 0.0722f;

        public static SKColorFilter? Build(PdfContrastMode mode) => mode switch
        {
            PdfContrastMode.Color      => BuildColorContrast(),
            PdfContrastMode.BlackWhite => BuildBlackWhiteContrast(),
            _                          => null
        };

        // Applica il filtro (se presente) al bitmap sorgente, restituendo un
        // nuovo bitmap. Se mode è None ritorna la stessa istanza invariata.
        // RoadEmphasis/AdaptiveContrast non sono SKColorFilter lineari (serve
        // una decisione per pixel, o per-tessera, non esprimibile come
        // SKColorMatrix), quindi sono gestiti a parte.
        public static SKBitmap Apply(SKBitmap source, PdfContrastMode mode)
        {
            if (mode == PdfContrastMode.RoadEmphasis)
                return ApplyRoadEmphasis(source);
            if (mode == PdfContrastMode.AdaptiveContrast)
                return ApplyAdaptiveContrast(source);

            var filter = Build(mode);
            if (filter == null) return source;

            var result = new SKBitmap(source.Width, source.Height);
            using (var canvas = new SKCanvas(result))
            using (var paint = new SKPaint { ColorFilter = filter })
            {
                canvas.DrawBitmap(source, 0, 0, paint);
            }
            filter.Dispose();
            source.Dispose();
            return result;
        }

        // Riclassifica ogni pixel in spazio HSL (vedi motivazione in testa al
        // file) e lo spinge verso il bianco (riempimenti) o lo accentua
        // (strade/testo). SKBitmap.Pixels effettua la copia dell'intero
        // buffer in un'unica chiamata nativa: molto più veloce di
        // GetPixel/SetPixel per-pixel, che in SkiaSharp è tipicamente lento.
        private static SKBitmap ApplyRoadEmphasis(SKBitmap source)
        {
            var pixels = source.Pixels;
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = RoadEmphasisPixel(pixels[i]);

            var result = new SKBitmap(source.Info);
            result.Pixels = pixels;
            source.Dispose();
            return result;
        }

        // Soglie tarate campionando i toni reali di OSM Carto (vedi analisi in
        // testa al file). Punto delicato verificato empiricamente su un tile
        // reale (zoom 16, area urbana): un riempimento residenziale/edifici
        // reale (#D9D0C9) ha saturazione ~17%, un prato (#F7FABF) ~86%, un
        // riempimento verde/acqua altrettanto saturi — TUTTI i riempimenti/
        // aree campionati hanno saturazione ≥15%. Il casing di strade minori
        // (residenziali/terziarie), invece, è grigio "puro" (saturazione 0%)
        // ma a luminosità MOLTO varia — nello stesso tile compaiono grigi
        // puri a luminosità 29%, 45%, 60%, 73%, 80%, tutti riferiti a
        // strade/bordi di classi diverse, non a sfondi.
        // In precedenza un taglio aggiuntivo "scuro = invariato" (luminosità
        // <55) scartava proprio i grigi più chiari (es. #BBBBBB, lum. 73%,
        // casing di strade residenziali/terziarie), che finivano quindi nel
        // ramo "riempimento" e sbiadivano fino a sparire contro lo sfondo
        // sbiancato — il bug segnalato ("alcune strade si perdono/sbiadiscono").
        // Poiché la sola saturazione separa già in modo affidabile grigi
        // (bordi/strade) da riempimenti (sempre colorati/pastello), il taglio
        // di luminosità è stato rimosso: qualunque pixel quasi-neutro resta
        // invariato, indipendentemente da quanto è chiaro o scuro.
        private const float AchromaticSatMax  = 15f; // sotto: grigio/nero/bianco "puro" (bordi, casing strade, testo)
        private const float RoadMinSaturation = 50f; // sopra il ~40% massimo dei riempimenti campionati

        private static SKColor RoadEmphasisPixel(SKColor c)
        {
            c.ToHsl(out float h, out float s, out float l);

            bool achromatic = s < AchromaticSatMax;
            if (achromatic)
                return c; // bordi, testo, casing stradali (qualunque grigio "puro"): invariati

            bool warmHue = h < 70f || h >= 320f; // arancio/rosso/giallo: tonalità delle strade
            bool isRoad  = warmHue && s >= RoadMinSaturation;

            if (isRoad)
            {
                // Strada: più satura e leggermente più scura, per risaltare
                // sul nuovo sfondo quasi bianco.
                float sr = Math.Min(100f, s * 1.15f + 10f);
                float lr = l * 0.75f;
                return SKColor.FromHsl(h, sr, lr, c.Alpha);
            }

            // Riempimento (edifici, residenziale, sfondo, acqua, verde/parchi,
            // o qualunque altro tono chiaro non riconosciuto come strada):
            // schiarito e desaturato verso il bianco.
            float sf = s * 0.25f;
            float lf = l + (97f - l) * 0.85f;
            return SKColor.FromHsl(h, sf, lf, c.Alpha);
        }

        // ---------------------------------------------------------------
        // "Contrasto adattivo": CLAHE (Contrast-Limited Adaptive Histogram
        // Equalization) sulla luminanza percettiva, invece della S-curve
        // GLOBALE di BlackWhite. Una curva unica (tarata su un tile medio)
        // può non essere quella giusta per un'area con distribuzione di
        // luminosità molto diversa (un grande parco quasi uniforme, una
        // costa dominata dall'acqua): CLAHE calcola una curva PER TESSERA
        // della griglia e le interpola bilinearmente pixel per pixel, così
        // ogni zona della mappa si stira in base al proprio contenuto reale.
        //
        // Due scelte verificate rendering i risultati su tile OSM reali
        // (zoom 16 area Colosseo/Roma — bordi/testo densi — e un tile con
        // una grande area verde pressoché uniforme, apposta per stressare il
        // rischio di artefatti):
        // 1) Il canale scelto è la LUMA percettiva (Rec.709), non la L di
        //    HSL: una prima versione operava in HSL (nuova L, stessi H/S)
        //    e produceva colori innaturalmente più saturi/aranciati su
        //    strade ed edifici — la L di HSL non è "disaccoppiata" dal resto
        //    del colore nello stesso modo in cui lo è la luma. Qui invece si
        //    ricalcola la luma per tessera e si RISCALA l'intero pixel RGB
        //    per il rapporto (nuova luma / vecchia luma) — stesso principio
        //    di una correzione d'esposizione fotografica, che preserva tinta
        //    e vivacità relativa molto meglio di un giro HSL completo.
        // 2) Un clip limit troppo permissivo (provato inizialmente a 3.0×)
        //    produceva bande visibili ai bordi delle tessere proprio
        //    sull'area verde quasi uniforme: l'istogramma di una tessera
        //    quasi piatta ha un picco enorme in pochi bin, e anche dopo il
        //    clip quel picco domina la CDF, amplificando differenze minime
        //    fra tessere adiacenti in una banda visibile nonostante
        //    l'interpolazione bilineare. clipLimit 2.0× l'ha eliminata sui
        //    due tile di test mantenendo comunque un effetto visibile.
        private const int    ClaheGridSize     = 8;   // tessere per lato
        private const double ClaheClipLimit    = 2.0; // × altezza media dei bin (standard CLAHE: 1.5-4)

        private static SKBitmap ApplyAdaptiveContrast(SKBitmap source)
        {
            int w = source.Width, h = source.Height;
            var pixels = source.Pixels;

            var luma = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                luma[i] = LumaR * c.Red + LumaG * c.Green + LumaB * c.Blue;
            }

            int tilesX = Math.Max(1, Math.Min(ClaheGridSize, w));
            int tilesY = Math.Max(1, Math.Min(ClaheGridSize, h));
            double tileW = (double)w / tilesX;
            double tileH = (double)h / tilesY;

            // Tabella di mappatura (CDF normalizzata 0..255) per ogni tessera.
            var cdfTables = new byte[tilesY, tilesX][];
            for (int ty = 0; ty < tilesY; ty++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    int x0 = (int)(tx * tileW), x1 = (int)Math.Min(w, (tx + 1) * tileW);
                    int y0 = (int)(ty * tileH), y1 = (int)Math.Min(h, (ty + 1) * tileH);

                    var hist  = new int[256];
                    int count = 0;
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++)
                        {
                            hist[(int)Math.Clamp(luma[y * w + x], 0, 255)]++;
                            count++;
                        }

                    int clipLimit = Math.Max(1, (int)(ClaheClipLimit * count / 256.0));
                    int excess = 0;
                    for (int b = 0; b < 256; b++)
                        if (hist[b] > clipLimit) { excess += hist[b] - clipLimit; hist[b] = clipLimit; }
                    int redistribute = excess / 256;
                    for (int b = 0; b < 256; b++) hist[b] += redistribute;

                    var cdf = new byte[256];
                    long cum = 0;
                    for (int b = 0; b < 256; b++)
                    {
                        cum += hist[b];
                        cdf[b] = (byte)Math.Clamp((int)Math.Round(cum / (double)count * 255.0), 0, 255);
                    }
                    cdfTables[ty, tx] = cdf;
                }
            }

            // Per pixel: interpolazione bilineare fra le 4 tessere più vicine
            // (tecnica standard CLAHE per evitare "gradini" ai bordi tessera).
            var outPixels = new SKColor[pixels.Length];
            for (int y = 0; y < h; y++)
            {
                double fy  = (y + 0.5) / tileH - 0.5;
                int    ty0 = (int)Math.Floor(fy);
                double wy  = fy - ty0;
                int tyA = Math.Clamp(ty0, 0, tilesY - 1), tyB = Math.Clamp(ty0 + 1, 0, tilesY - 1);

                for (int x = 0; x < w; x++)
                {
                    double fx  = (x + 0.5) / tileW - 0.5;
                    int    tx0 = (int)Math.Floor(fx);
                    double wx  = fx - tx0;
                    int txA = Math.Clamp(tx0, 0, tilesX - 1), txB = Math.Clamp(tx0 + 1, 0, tilesX - 1);

                    int idx  = y * w + x;
                    int lBin = (int)Math.Clamp(luma[idx], 0, 255);

                    double v00 = cdfTables[tyA, txA][lBin], v10 = cdfTables[tyA, txB][lBin];
                    double v01 = cdfTables[tyB, txA][lBin], v11 = cdfTables[tyB, txB][lBin];
                    double top = v00 * (1 - wx) + v10 * wx;
                    double bot = v01 * (1 - wx) + v11 * wx;
                    double newLuma = top * (1 - wy) + bot * wy;

                    double ratio = newLuma / Math.Max(luma[idx], 1.0);
                    var c = pixels[idx];
                    outPixels[idx] = new SKColor(
                        (byte)Math.Clamp(c.Red   * ratio, 0, 255),
                        (byte)Math.Clamp(c.Green * ratio, 0, 255),
                        (byte)Math.Clamp(c.Blue  * ratio, 0, 255),
                        c.Alpha);
                }
            }

            var result = new SKBitmap(source.Info);
            result.Pixels = outPixels;
            source.Dispose();
            return result;
        }

        // ---------------------------------------------------------------
        // "Rinforza contorni": individua i bordi (Sobel sulla luma
        // percettiva) e li scurisce, senza toccare le zone piatte —
        // applicabile SOPRA qualunque PdfContrastMode (compreso None),
        // non è un'alternativa alle altre modalità. Motivazione: a DPI di
        // stampa alti i tratti sottili di OSM Carto (1px al livello tile)
        // diventano linee capillari; scurire selettivamente i bordi reali
        // li rende più leggibili senza alterare i riempimenti.
        //
        // La magnitudine del gradiente viene dilatata (max-filter 3×3)
        // PRIMA di soglia/applicazione apposta per ispessire i contorni di
        // ~1-2px, il problema specifico segnalato come motivazione
        // dell'idea — non solo per accentuarli.
        //
        // Parametri verificati rendering su tile OSM reali (stessi due usati
        // per CLAHE sopra): un primo tentativo (soglie 0.08-0.35, fattore di
        // scurimento 0.85) anneriva quasi ogni strada/etichetta, illeggibile
        // — la dilatazione 3×3 espande già molto l'area "bordo", quindi
        // servono soglie alte (solo i gradienti più forti contano) e un
        // fattore di scurimento moderato.
        private const double EdgeDarkenFactor  = 0.5;
        private const double EdgeThresholdLow  = 0.35; // sotto: contorno non rinforzato
        private const double EdgeThresholdHigh = 0.7;  // sopra: scurimento massimo (EdgeDarkenFactor)

        public static SKBitmap ApplyEdgeReinforcement(SKBitmap source)
        {
            int w = source.Width, h = source.Height;
            var pixels = source.Pixels;

            var luma = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                luma[i] = (LumaR * c.Red + LumaG * c.Green + LumaB * c.Blue) / 255f;
            }

            float At(int x, int y)
            {
                x = Math.Clamp(x, 0, w - 1);
                y = Math.Clamp(y, 0, h - 1);
                return luma[y * w + x];
            }

            var mag = new float[pixels.Length];
            float maxMag = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    // Kernel Sobel 3×3
                    float gx = -At(x - 1, y - 1) + At(x + 1, y - 1)
                             - 2 * At(x - 1, y)   + 2 * At(x + 1, y)
                             - At(x - 1, y + 1)   + At(x + 1, y + 1);
                    float gy = -At(x - 1, y - 1) - 2 * At(x, y - 1) - At(x + 1, y - 1)
                             + At(x - 1, y + 1)   + 2 * At(x, y + 1) + At(x + 1, y + 1);
                    float m = MathF.Sqrt(gx * gx + gy * gy);
                    mag[y * w + x] = m;
                    if (m > maxMag) maxMag = m;
                }

            // Dilatazione 3×3 (max-filter): ispessisce i contorni rilevati.
            var dilated = new float[pixels.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float best = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = Math.Clamp(x + dx, 0, w - 1);
                            int yy = Math.Clamp(y + dy, 0, h - 1);
                            float v = mag[yy * w + xx];
                            if (v > best) best = v;
                        }
                    dilated[y * w + x] = best;
                }

            var outPixels = new SKColor[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                double norm     = maxMag > 0 ? dilated[i] / maxMag : 0;
                double strength = Math.Clamp((norm - EdgeThresholdLow) / (EdgeThresholdHigh - EdgeThresholdLow), 0, 1);
                double factor   = 1 - strength * EdgeDarkenFactor;
                var c = pixels[i];
                outPixels[i] = new SKColor(
                    (byte)Math.Clamp(c.Red   * factor, 0, 255),
                    (byte)Math.Clamp(c.Green * factor, 0, 255),
                    (byte)Math.Clamp(c.Blue  * factor, 0, 255),
                    c.Alpha);
            }

            var result = new SKBitmap(source.Info);
            result.Pixels = outPixels;
            source.Dispose();
            return result;
        }

        // ---------------------------------------------------------------
        // Retinatura Floyd-Steinberg: quantizza a bianco/nero puri
        // diffondendo l'errore di arrotondamento ai pixel vicini ancora da
        // visitare (7/16 destra, 3/16 sotto-sinistra, 5/16 sotto, 1/16
        // sotto-destra — pesi standard dell'algoritmo). Pensata per la
        // stampa su una vera stampante monocromatica: il grigio continuo di
        // "Contrasta B/N" viene comunque ri-ditherato dal driver della
        // stampante in modo imprevedibile, mentre un pattern di punti
        // applicativo preserva parchi/acqua come texture riconoscibile
        // invece di un grigio piatto.
        //
        // Va SEMPRE applicata al bitmap già passato per
        // BuildBlackWhiteContrast (S-curve), non al tile grezzo: verificato
        // rendering entrambi gli ordini su un tile reale — ditherare la luma
        // grezza (quasi tutta vicina al bianco, coi pastelli OSM Carto)
        // produce solo rumore, mentre ditherare il risultato già stirato
        // preserva la separazione fra riempimenti/sfondo ottenuta dalla
        // curva. Va invocata DOPO un eventuale ApplyEdgeReinforcement, mai
        // prima: verificato che scurire i contorni sul grigio continuo PRIMA
        // di ditherare dà contorni netti e solidi; ditherare e poi scurire
        // avrebbe invece un contorno "spezzato" dal pattern di dithering
        // già presente sotto.
        public static SKBitmap ApplyFloydSteinbergDither(SKBitmap source)
        {
            int w = source.Width, h = source.Height;
            var pixels = source.Pixels;

            var luma = new double[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                luma[i] = LumaR * c.Red + LumaG * c.Green + LumaB * c.Blue;
            }

            var outPixels = new SKColor[pixels.Length];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    double old    = Math.Clamp(luma[idx], 0, 255);
                    byte   newVal = old < 128 ? (byte)0 : (byte)255;
                    double error  = old - newVal;
                    outPixels[idx] = new SKColor(newVal, newVal, newVal, pixels[idx].Alpha);

                    void Distribute(int dx, int dy, double factor)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || xx >= w || yy < 0 || yy >= h) return;
                        luma[yy * w + xx] += error * factor;
                    }
                    Distribute(1, 0, 7.0 / 16);
                    Distribute(-1, 1, 3.0 / 16);
                    Distribute(0, 1, 5.0 / 16);
                    Distribute(1, 1, 1.0 / 16);
                }
            }

            var result = new SKBitmap(source.Info);
            result.Pixels = outPixels;
            source.Dispose();
            return result;
        }

        // "Contrasta colore": satura leggermente di più (per accentuare la
        // differenza di tonalità fra riempimenti pastello adiacenti) e poi
        // stira il contrasto attorno al punto medio. Resta a colori.
        private static SKColorFilter BuildColorContrast()
        {
            var saturation = SaturationMatrix(1.45f);
            var contrast   = ContrastMatrix(1.28f);
            using var satFilter = SKColorFilter.CreateColorMatrix(saturation);
            var contrastFilter  = SKColorFilter.CreateColorMatrix(contrast);
            // compose(outer, inner): applica prima la saturazione, poi il contrasto
            return SKColorFilter.CreateCompose(contrastFilter, satFilter);
        }

        // "Contrasta B/N": converte in scala di grigi per luminanza percettiva,
        // poi applica una curva a "S" (sigmoide) che stira fortemente la fascia
        // intermedia dove cadono i riempimenti pastello di OSM Carto, lasciando
        // pressoché invariati i bianchi e i neri puri (niente clipping netto).
        private static SKColorFilter BuildBlackWhiteContrast()
        {
            using var grayFilter = SKColorFilter.CreateColorMatrix(GrayscaleMatrix());
            // Pivot/pendenza tarati sui toni reali di OSM Carto (vedi analisi
            // in testa al file): edifici/verde/strade principali cadono tutti
            // fra luminanza 0.70 e 0.96, molto vicini fra loro; casing/testo
            // sono già scuri (<0.35). Un pivot troppo alto (es. 0.85) crea un
            // gradino troppo stretto proprio in mezzo a quella fascia,
            // schiacciando edifici e strade sullo stesso grigio scuro invece
            // di separarli. Pivot 0.78 con pendenza moderata allarga invece
            // gli scarti reciproci in tutta la fascia 0.70–0.96 mantenendo il
            // bianco quasi bianco, e spinge il gruppo già scuro verso il nero.
            var curve = BuildSCurveTable(steepness: 6.0, pivot: 0.78);
            using var curveFilter = SKColorFilter.CreateTable(null, curve, curve, curve);
            return SKColorFilter.CreateCompose(curveFilter, grayFilter);
        }

        private static float[] SaturationMatrix(float saturation)
        {
            float inv = 1 - saturation;
            float r = LumaR * inv, g = LumaG * inv, b = LumaB * inv;
            return new[]
            {
                r + saturation, g,              b,              0f, 0f,
                r,              g + saturation, b,              0f, 0f,
                r,              g,              b + saturation, 0f, 0f,
                0f,             0f,             0f,             1f, 0f
            };
        }

        private static float[] ContrastMatrix(float contrast)
        {
            float t = 0.5f * (1 - contrast);
            return new[]
            {
                contrast, 0f,       0f,       0f, t,
                0f,       contrast, 0f,       0f, t,
                0f,       0f,       contrast, 0f, t,
                0f,       0f,       0f,       1f, 0f
            };
        }

        private static float[] GrayscaleMatrix() => new[]
        {
            LumaR, LumaG, LumaB, 0f, 0f,
            LumaR, LumaG, LumaB, 0f, 0f,
            LumaR, LumaG, LumaB, 0f, 0f,
            0f,    0f,    0f,    1f, 0f
        };

        // Sigmoide normalizzata a f(0)=0, f(1)=1: valori estremi restano
        // pressoché invariati, la fascia attorno a "pivot" viene stirata in
        // proporzione a "steepness".
        private static byte[] BuildSCurveTable(double steepness, double pivot)
        {
            double Raw(double v) => 1.0 / (1.0 + Math.Exp(-steepness * (v - pivot)));
            double y0 = Raw(0), y1 = Raw(1);

            var table = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double x = i / 255.0;
                double y = (Raw(x) - y0) / (y1 - y0);
                table[i] = (byte)Math.Clamp((int)Math.Round(y * 255.0), 0, 255);
            }
            return table;
        }
    }
}
