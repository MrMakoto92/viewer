using System;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
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
    
    // Indicador atómico para evitar saturar el UI Thread (descarta frames si la UI está ocupada)
    private int _isRendering = 0;

    public RtspVideoView()
    {
        InitializeComponent();
        _image = this.FindControl<Image>("PART_Image")
                 ?? throw new InvalidOperationException("PART_Image missing");
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
        // Si el UI Thread sigue ocupado procesando el frame previo, descartamos este para no acumular latencia
        if (Interlocked.CompareExchange(ref _isRendering, 1, 0) != 0)
        {
            return;
        }

        // Usamos Post con la máxima prioridad posible para sincronizar inmediatamente con el refresco de pantalla
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (Session == null) return;

                EnsureBitmap(frame.Width, frame.Height);
                if (_bitmap is null) return;

                using (var locked = _bitmap.Lock())
                {
                    Marshal.Copy(frame.Bgra, 0, locked.Address, frame.Stride * frame.Height);
                }
                // Se removió InvalidateVisual() explícito para evitar repintados dobles en la GPU de Android
            }
            finally
            {
                Interlocked.Exchange(ref _isRendering, 0);
            }
        }, DispatcherPriority.MaxValue);
    }

    private void EnsureBitmap(int width, int height)
    {
        if (_bitmap is not null && _bitmap.PixelSize.Width == width && _bitmap.PixelSize.Height == height)
            return;

        _bitmap?.Dispose();

        _bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

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
