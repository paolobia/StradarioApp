using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;

namespace StradarioApp.UI
{
    public class SkiaPaintEventArgs : EventArgs
    {
        public SKCanvas Canvas { get; }
        public int Width  { get; }
        public int Height { get; }

        public SkiaPaintEventArgs(SKCanvas canvas, int width, int height)
        {
            Canvas = canvas;
            Width  = width;
            Height = height;
        }
    }

    public class MapDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly Action<SKCanvas, int, int> _render;

        public MapDrawOperation(Rect bounds, Action<SKCanvas, int, int> render)
        {
            _bounds = bounds;
            _render = render;
        }

        public Rect Bounds => _bounds;

        public bool HitTest(Point p) => _bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
            if (leaseFeature == null) return;

            using var lease = leaseFeature.Lease();
            if (lease?.SkCanvas == null) return;

            _render(lease.SkCanvas, (int)_bounds.Width, (int)_bounds.Height);
        }

        public void Dispose() { }
    }

    public class MapCanvas : Control
    {
        public event EventHandler<SkiaPaintEventArgs>? PaintSkia;

        public override void Render(DrawingContext context)
        {
            context.Custom(new MapDrawOperation(Bounds, (canvas, w, h) =>
                PaintSkia?.Invoke(this, new SkiaPaintEventArgs(canvas, w, h))));

            // Request continuous redraw so async tile downloads are shown immediately
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        }
    }
}
