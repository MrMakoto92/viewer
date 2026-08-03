using System;
using System.Threading;
using Android.App;
using Android.Runtime;
using Android.Util;
using Avalonia;
using Avalonia.Android;
using Avalonia.Skia;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Persistence;

namespace OpenIPC.Viewer.Android;

// Avalonia 12 Android entry: AvaloniaAndroidApplication<TApp> is the bridge
// between Android's Application lifecycle and Avalonia's AppBuilder.
[Application]
public sealed class MainApplication : AvaloniaAndroidApplication<App.App>
{
    private const string Tag = "OpenIPC";

    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    public override void OnCreate()
    {
        try
        {
            var services = Composition.Build(this);
            App.App.Services = services;

            services.GetRequiredService<IMigrationRunner>()
                .MigrateAsync(CancellationToken.None)
                .GetAwaiter().GetResult();

            services.GetRequiredService<EventIngestionService>()
                .StartAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error(Tag, Java.Lang.Throwable.FromException(ex), "Composition.Build failed");
            throw;
        }

        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .With(new SkiaOptions
            {
                // Subimos el caché de texturas/GPU a 128 MB para eliminar el lag en transmisiones
                MaxGpuResourceSizeBytes = 128 * 1024 * 1024
            });
}
