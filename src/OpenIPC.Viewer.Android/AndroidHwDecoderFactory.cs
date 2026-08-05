using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Android;

public sealed class AndroidHwDecoderFactory : IHwDecoderFactory
{
    public HwAccelHint Kind => HwAccelHint.MediaCodec;

    public HwDecoderProbeResult Probe()
    {
        return HwDecoderProbeResult.Success();
    }
}
