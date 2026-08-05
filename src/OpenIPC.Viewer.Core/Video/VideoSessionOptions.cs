using System;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Core.Video;

public sealed record VideoSessionOptions(
    Uri RtspUri,
    CameraCredentials? Credentials,
    RtspTransport Transport,
    HwAccelHint HwAccel,
    TimeSpan NetworkCaching,
    bool AutoReconnect = true,
    bool EnableAudio = false,
    // Opciones para suavizar reproducción y evitar desincronización/tirones
    bool LowDelay = true,
    bool FrameDrop = true)
{
    public static VideoSessionOptions Default(Uri uri, CameraCredentials? creds = null) =>
        new(
            uri, 
            creds, 
            RtspTransport.Tcp, 
            HwAccelHint.Auto, 
            TimeSpan.FromMilliseconds(150),
            AutoReconnect: true,
            EnableAudio: false,
            LowDelay: true,
            FrameDrop: true);
}
