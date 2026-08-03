using System;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia.Android;

namespace OpenIPC.Viewer.Android;

[Activity(
    Label = "OpenIPC Viewer",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@mipmap/icon",
    MainLauncher = true,
    WindowSoftInputMode = SoftInput.AdjustResize | SoftInput.StateHidden,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize
                          | ConfigChanges.UiMode | ConfigChanges.Density)]
public sealed class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Aceleración gráfica por hardware para la tele
        Window?.SetFlags(WindowManagerFlags.HardwareAccelerated, WindowManagerFlags.HardwareAccelerated);

        var wanted = new System.Collections.Generic.List<string>();
        if (CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) != Permission.Granted)
            wanted.Add(global::Android.Manifest.Permission.RecordAudio);
        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            wanted.Add(global::Android.Manifest.Permission.PostNotifications);
        if (wanted.Count > 0)
            RequestPermissions(wanted.ToArray(), 1906);
    }
}
