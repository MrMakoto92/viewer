using System;
using System.Reactive.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.Controls;

public sealed partial class RtspVideoView : UserControl
{
    public static readonly StyledProperty<IVideoSession?> SessionProperty =
        AvaloniaProperty.Register<RtspVideoView, IVideoSession?>(nameof(Session));

    public IVideoSession? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    private readonly Image _image;
    private WriteableBitmap? _bitmap;
    private IDisposable? _frameSub;
    
    // Indicador atómico para evitar saturar el UI Thread (Drop de frames si el UI está ocupado)
    private int _isRendering = 0;

    public RtspVideoView()
    {
        InitializeComponent();
        _image = this.FindControl<Image>("PART_Image")
                 ?? throw new InvalidOperationException("PART_Image missing");
        
        // Desactivar alpha blending a nivel de plataforma para ahorrar ciclos de Shader en GPU
        RenderOptions.SetRequiresAlpha(_image, false);
    }

    static RtspVideoView()
    {
        SessionProperty.Changed.AddClassHandler<RtspVideoView>((view, _) => view.OnSessionChanged());
    }

    private void OnSessionChanged()
    {
        _frameSub?.Dispose();
        _frameSub = Session?.Frames.Subscribe(OnFrame);
    }

    private void OnFrame(VideoFrame frame)
    {
        // Si el UI Thread está ocupado procesando el fotograma anterior, descartamos este frame
        // Esto evita que la cola del Dispatcher colapse la memoria RAM y el hilo principal
        if (Interlocked.CompareExchange(ref _isRendering, 1, 0) != 0)
        {
            return;
        }

        // Post asíncrono para liberar inmediatamente el hilo del decodificador de video
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (Session == null) return;

                EnsureBitmap(frame.Width, frame.Height);
                if (_bitmap is null) return;

                using (var locked = _bitmap.Lock())
                {
                    unsafe
                    {
                        fixed (byte* srcPtr = frame.Bgra)
                        {
                            byte* dstPtr = (byte*)locked.Address;
                            int bytesToCopy = frame.Stride * frame.Height;
                            
                            // Copia por bloques de memoria directa a nivel de CPU/Punteros
                            Buffer.MemoryCopy(srcPtr, dstPtr, bytesToCopy, bytesToCopy);
                        }
                    }
                }

                _image.InvalidateVisual();
            }
            finally
            {
                // Liberar el flag atómico para permitir el siguiente frame
                Interlocked.Exchange(ref _isRendering, 0);
            }
        }, DispatcherPriority.Render);
    }

    private void EnsureBitmap(int width, int height)
    {
        if (_bitmap is not null && _bitmap.PixelSize.Width == width && _bitmap.PixelSize.Height == height)
            return;

        _bitmap?.Dispose(); // Liberar memoria nativa del bitmap previo si cambia la resolución

        _bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque); // Cambiado a Opaque para evitar cálculos de transparencia inutiles

        _image.Source = _bitmap;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _frameSub?.Dispose();
        _frameSub = null;
        
        _bitmap?.Dispose();
        _bitmap = null;

        base.OnDetachedFromVisualTree(e);
    }
}
