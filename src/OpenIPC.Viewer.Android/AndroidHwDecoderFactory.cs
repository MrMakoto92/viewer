using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.Android;

public sealed class AndroidHwDecoderFactory : IHwDecoderFactory
{
    public HwAccelHint Kind => HwAccelHint.MediaCodec;

    public HwProbeResult Probe()
    {
        return new HwProbeResult(Available: true, Reason: null);
    }
}
