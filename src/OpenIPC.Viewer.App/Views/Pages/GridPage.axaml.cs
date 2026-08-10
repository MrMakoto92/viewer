using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

namespace OpenIPC.Viewer.App.Views.Pages;

/// <summary>
/// Vista principal de la grilla de cámaras optimizada para Android TV.
/// Elimina efectos gráficos pesados de Avalonia y optimiza la distribución en pantalla
/// para reducir drásticamente el consumo de procesador en la Xiaomi Mi Box S.
/// </summary>
public partial class GridPage : UserControl
{
    public GridPage()
    {
        InitializeComponent();
        this.AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // Detectamos si se ejecuta en Android para aplicar configuraciones de bajo consumo
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("ANDROID")))
        {
            ApplyAndroidPerformanceTuning();
        }
    }

    /// <summary>
    /// Desactiva opciones complejas de layout de Avalonia que provocan lag al actualizar celdas.
    /// </summary>
    private void ApplyAndroidPerformanceTuning()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                // Desactivamos el renderizado de alta precisión que satura la CPU de la TV Box
                RenderOptions.SetRequiresHighQualityLayoutMessages(this, false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error aplicando optimización en GridPage: {ex.Message}");
            }
        });
    }
}
