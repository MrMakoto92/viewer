using Android.Context;
using Android.Views;
using Avalonia.Controls;
using Avalonia.Platform;

namespace OpenIPC.Viewer.Android.Controls;

/// <summary>
/// Control personalizado de Android para Avalonia UI.
/// Permite renderizar el video directamente usando la GPU del Xiaomi Mi Box S (Amlogic S905X4)
/// sin saturar el procesador ni la memoria RAM de la app.
/// </summary>
public class AndroidVideoSurfaceHost : NativeControlHost
{
    private SurfaceView? _surfaceView;

    /// <summary>
    /// Obtiene la vista nativa de Android creada por el sistema.
    /// </summary>
    public SurfaceView? SurfaceView => _surfaceView;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        // Obtenemos el contexto actual de la aplicación Android
        var context = Android.App.Application.Context;

        // Creamos una vista de superficie nativa (SurfaceView) de Android
        _surfaceView = new SurfaceView(context);

        // Devolvemos el puntero nativo para que Avalonia lo incruste en la pantalla
        return new PlatformHandle(_surfaceView.Handle, "AndroidSurfaceView");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        // Limpiamos la referencia para liberar memoria al cerrar la cámara
        _surfaceView = null;
        base.DestroyNativeControlCore(control);
    }
}
