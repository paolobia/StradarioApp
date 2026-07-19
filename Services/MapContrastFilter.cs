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
        // RoadEmphasis non è un SKColorFilter lineare (serve una decisione
        // per pixel basata su tonalità/saturazione), quindi è gestito a parte.
        public static SKBitmap Apply(SKBitmap source, PdfContrastMode mode)
        {
            if (mode == PdfContrastMode.RoadEmphasis)
                return ApplyRoadEmphasis(source);

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
