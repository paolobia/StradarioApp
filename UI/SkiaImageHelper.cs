// =============================================================================
// UI/SkiaImageHelper.cs
//
// SINOSSI: Conversione di bitmap SkiaSharp in immagini Avalonia, usata per
//   mostrare le anteprime delle icone POI nei selettori della UI (le stesse
//   icone renderizzate da PoiIconRenderer per mappa/PDF/KMZ).
// =============================================================================

using System.IO;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace StradarioApp.UI
{
    internal static class SkiaImageHelper
    {
        public static Bitmap ToAvaloniaBitmap(SKBitmap skBitmap)
        {
            using var data = skBitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var ms   = new MemoryStream(data.ToArray());
            return new Bitmap(ms);
        }

        // Ritaglia un bitmap trasparente al rettangolo minimo che contiene
        // pixel non trasparenti — un'icona (es. PoiIconRenderer.RenderToBitmap,
        // reso su un canvas quadrato più largo della forma effettiva per
        // lasciare margine alla coda del pin) mostrata piccola e in linea col
        // testo (v. MainWindow.BuildPoiIconImage) risulta altrimenti
        // circondata da un margine trasparente diverso a seconda della forma
        // — visivamente percepito come "spazio vuoto asimmetrico" attorno
        // all'icona. Ritorna il bitmap originale invariato se non trova
        // alcun pixel visibile (bitmap vuoto).
        public static SKBitmap CropToContent(SKBitmap source, byte alphaThreshold = 5)
        {
            int left = source.Width, right = -1, top = source.Height, bottom = -1;
            for (int y = 0; y < source.Height; y++)
            {
                for (int x = 0; x < source.Width; x++)
                {
                    if (source.GetPixel(x, y).Alpha <= alphaThreshold) continue;
                    if (x < left)   left = x;
                    if (x > right)  right = x;
                    if (y < top)    top = y;
                    if (y > bottom) bottom = y;
                }
            }
            if (right < left || bottom < top) return source;

            var srcRect = new SKRectI(left, top, right + 1, bottom + 1);
            var cropped = new SKBitmap(srcRect.Width, srcRect.Height, source.ColorType, source.AlphaType);
            using var canvas = new SKCanvas(cropped);
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(source, srcRect, new SKRect(0, 0, srcRect.Width, srcRect.Height));
            return cropped;
        }
    }
}
