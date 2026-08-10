using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenIPC.Viewer.App.Views.Pages;

/// <summary>
/// Vista principal de la grilla de cámaras.
/// </summary>
public partial class GridPage : UserControl
{
    public GridPage()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
