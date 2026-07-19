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
    }
}
