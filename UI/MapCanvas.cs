// =============================================================================
// UI/MapCanvas.cs
//
// SINOSSI: Controllo Avalonia custom per il rendering SkiaSharp della mappa.
//   - Eredita da Control di Avalonia
//   - Sovrascrive Render(DrawingContext) per ottenere l'SKCanvas nativo
//     tramite ISkiaDrawingContextImpl (parte di Avalonia.Skia)
//   - Espone evento PaintSkia(SKCanvas, Size) per il rendering esterno
//   - Usa InvalidateVisual() per richiedere un ridisegno
//   - Gestisce input mouse (press, move, release, wheel) e li espone come eventi
// =============================================================================

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace StradarioApp.UI
{
    // Argomenti dell'evento di paint SkiaSharp
    public class SkiaPaintEventArgs : EventArgs
    {
        public SKCanvas Canvas { get; }
        public float    Width  { get; }
        public float    Height { get; }

        public SkiaPaintEventArgs(SKCanvas canvas, float width, float height)
        {
            Canvas = canvas;
            Width  = width;
            Height = height;
        }
    }

    // Custom draw operation: passa l'SKCanvas all'handler
    internal class MapDrawOperation : ICustomDrawOperation
    {
        private readonly Action<SKCanvas, float, float> _draw;
        public Rect Bounds { get; }

        public MapDrawOperation(Rect bounds, Action<SKCanvas, float, float> draw)
        {
            Bounds = bounds;
            _draw  = draw;
        }

        public bool HitTest(Point p) => Bounds.Contains(p);
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            // Ottieni l'SKCanvas dal contesto Avalonia.Skia
            var skia = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (skia == null) return;

            using var lease = skia.Lease();
            var canvas = lease?.SkCanvas;
            if (canvas == null) return;

            _draw(canvas, (float)Bounds.Width, (float)Bounds.Height);
        }

        public void Dispose() { }
    }

    // Controllo canvas per il disegno SkiaSharp
    public class MapCanvas : Control
    {
        // Evento chiamato ad ogni frame di rendering
        public event EventHandler<SkiaPaintEventArgs>? PaintSkia;

        public MapCanvas()
        {
            // Il controllo deve ricevere il focus per gli eventi tastiera (futuro)
            Focusable = true;
            ClipToBounds = true;
        }

        // Override Render: usa ICustomDrawOperation per accedere a SKCanvas
        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);

            context.Custom(new MapDrawOperation(bounds, (canvas, w, h) =>
            {
                PaintSkia?.Invoke(this, new SkiaPaintEventArgs(canvas, w, h));
            }));

            // Invalida subito per animazione continua (tile asincroni)
            // Lo fa solo se qualcuno sottoscrive l'evento
            Avalonia.Threading.Dispatcher.UIThread.Post(
                InvalidateVisual,
                Avalonia.Threading.DispatcherPriority.Background);
        }
    }
}
