using System;
using System.Buffers;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Video;
using SkiaSharp;

namespace OpenIPC.Viewer.Video.Pipeline;

internal sealed class FfmpegVideoSession : IVideoSession
{
    private readonly VideoSessionOptions _options;
    private readonly IHwDecoderFactory? _hwFactory;
    private readonly ILogger<FfmpegVideoSession> _logger;

    private readonly Subject<VideoFrame> _frames = new();
    private readonly Subject<AudioFrame> _audioFrames = new();
    private readonly Subject<SessionState> _stateChanged = new();
    private readonly Subject<SessionTelemetry> _telemetry = new();

    private const int AudioOutSampleRate = 48000;
    private const int AudioOutChannels = 2;

    private readonly object _stateLock = new();
    private readonly object _snapshotLock = new();

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private SessionState _state = SessionState.Idle;
    private string? _lastError;

    private readonly ManualResetEventSlim _decodeGate = new(true);
    private volatile bool _paused;

    private volatile bool _audioEnabled;

    private int _framesDecoded;
    private DateTime _lastFpsTick;
    private int _framesSinceFpsTick;
    private long _bytesSinceFpsTick;
    private string? _codecName;
    private int _width;
    private int _height;

    private AVCodecContext_get_format? _getFormatDelegate;
    private AVPixelFormat _selectedHwPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;

    private byte[]? _snapshotBgra;
    private int _snapshotWidth;
    private int _snapshotHeight;
    private int _snapshotStride;

    public FfmpegVideoSession(VideoSessionOptions options, IHwDecoderFactory? hwFactory, ILogger<FfmpegVideoSession> logger)
    {
        _options = options;
        _hwFactory = hwFactory;
        _logger = logger;
        _audioEnabled = options.EnableAudio;
    }

    public void SetAudioEnabled(bool enabled) => _audioEnabled = enabled;

    public SessionState State
    {
        get { lock (_stateLock) return _state; }
    }

    public string? LastError
    {
        get { lock (_stateLock) return _lastError; }
    }

    public IObservable<VideoFrame> Frames => _frames;
    public IObservable<AudioFrame> AudioFrames => _audioFrames;
    public IObservable<SessionState> StateChanged => _stateChanged;
    public IObservable<SessionTelemetry> Telemetry => _telemetry;

    public Task StartAsync(CancellationToken ct)
    {
        if (_thread is not null)
            throw new InvalidOperationException("Session already started");

        FfmpegRuntime.EnsureInitialized();
        SetState(SessionState.Connecting);

        _cts = new CancellationTokenSource();
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = $"rtsp-{_options.RtspUri.Host}",
        };
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task<byte[]> SnapshotAsync(SnapshotFormat format, CancellationToken ct)
    {
        byte[]? bgra;
        int w, h, stride;
        lock (_snapshotLock)
        {
            if (_snapshotBgra is null)
                return Task.FromResult(Array.Empty<byte>());
            bgra = (byte[])_snapshotBgra.Clone();
            w = _snapshotWidth;
            h = _snapshotHeight;
            stride = _snapshotStride;
        }

        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(bgra, 0, bitmap.GetPixels(), stride * h);

        using var image = SKImage.FromBitmap(bitmap);
        var skFormat = format == SnapshotFormat.Png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
        using var data = image.Encode(skFormat, quality: 92);
        return Task.FromResult(data.ToArray());
    }

    public void PauseDecode()
    {
        if (_thread is null || _paused) return;
        _paused = true;
        _decodeGate.Reset();
        SetState(SessionState.Paused);
    }

    public void Resume()
    {
        if (_thread is null || !_paused) return;
        _paused = false;
        _decodeGate.Set();
        SetState(SessionState.Playing);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _decodeGate.Set();
        if (_thread is { IsAlive: true })
        {
            await Task.Run(() => _thread.Join(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }
        _frames.OnCompleted();
        _audioFrames.OnCompleted();
        _stateChanged.OnCompleted();
        _telemetry.OnCompleted();
        _cts?.Dispose();
        _decodeGate.Dispose();
    }

    private unsafe void Run()
    {
        AVFormatContext* fmtCtx = null;
        AVCodecContext* codecCtx = null;
        AVFrame* frame = null;
        AVFrame* swFrame = null;
        AVPacket* packet = null;
        SwsContext* sws = null;
        AVDictionary* opts = null;
        AVBufferRef* hwDeviceCtx = null;
        AVCodecContext* audioCtx = null;
        AVFrame* audioFrame = null;
        SwrContext* swr = null;
        var videoStreamIndex = -1;
        var audioStreamIndex = -1;
        var audioProbedNoTrack = false;
        var swsSrcPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
        var hwActive = false;

        try
        {
            BuildOpts(&opts);
            fmtCtx = ffmpeg.avformat_alloc_context();
            var url = BuildUrlWithCredentials(_options.RtspUri, _options.Credentials);

            var ret = ffmpeg.avformat_open_input(&fmtCtx, url, null, &opts);
            FfmpegError.ThrowIfError(ret, "avformat_open_input");

            ret = ffmpeg.avformat_find_stream_info(fmtCtx, null);
            FfmpegError.ThrowIfError(ret, "avformat_find_stream_info");

            for (var i = 0; i < (int)fmtCtx->nb_streams; i++)
            {
                if (fmtCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    videoStreamIndex = i;
                    break;
                }
            }
            if (videoStreamIndex < 0)
                throw new InvalidOperationException("No video stream in input");

            var codecpar = fmtCtx->streams[videoStreamIndex]->codecpar;
            var codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id);
            if (codec == null)
                throw new InvalidOperationException($"No decoder available for codec id {codecpar->codec_id}");

            codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            ret = ffmpeg.avcodec_parameters_to_context(codecCtx, codecpar);
            FfmpegError.ThrowIfError(ret, "avcodec_parameters_to_context");

            var resolved = HwAccelSelector.Resolve(_options.HwAccel, _hwFactory, _logger);
            if (resolved != HwAccelHint.None)
                hwActive = TryEnableHw(codecCtx, resolved, &hwDeviceCtx);

            ret = ffmpeg.avcodec_open2(codecCtx, codec, null);
            if (ret < 0 && hwActive)
            {
                _logger.LogWarning("HW-accelerated avcodec_open2 failed: {Err}; retrying software", FfmpegError.Describe(ret));
                TearDownHw(codecCtx, &hwDeviceCtx);
                hwActive = false;
                ret = ffmpeg.avcodec_open2(codecCtx, codec, null);
            }
            FfmpegError.ThrowIfError(ret, "avcodec_open2");

            _width = codecCtx->width;
            _height = codecCtx->height;
            var baseName = Marshal.PtrToStringAnsi((IntPtr)codec->name);
            _codecName = hwActive ? $"{baseName} ({resolved})" : baseName;
            if (hwActive)
                _logger.LogInformation("HW decode active: {Codec} via {Hint}", baseName, resolved);

            packet = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            if (hwActive) swFrame = ffmpeg.av_frame_alloc();

            SetState(SessionState.Playing);
            _lastFpsTick = DateTime.UtcNow;

            var ct = _cts!.Token;
            while (!ct.IsCancellationRequested)
            {
                if (_paused)
                {
                    try { _decodeGate.Wait(ct); }
                    catch (OperationCanceledException) { break; }
                }

                if (_audioEnabled && audioStreamIndex < 0 && !audioProbedNoTrack)
                {
                    audioStreamIndex = SetupAudio(fmtCtx, &audioCtx, &swr, &audioFrame);
                    if (audioStreamIndex < 0)
                    {
                        audioProbedNoTrack = true;
                        _logger.LogInformation("No audio track for {Host}", _options.RtspUri.Host);
                    }
                }
                else if (!_audioEnabled && audioStreamIndex >= 0)
                {
                    if (swr != null) { var p = swr; ffmpeg.swr_free(&p); swr = null; }
                    if (audioFrame != null) { var p = audioFrame; ffmpeg.av_frame_free(&p); audioFrame = null; }
                    if (audioCtx != null) { var p = audioCtx; ffmpeg.avcodec_free_context(&p); audioCtx = null; }
                    audioStreamIndex = -1;
                    audioProbedNoTrack = false;
                }

                ret = ffmpeg.av_read_frame(fmtCtx, packet);
                if (ret < 0)
                {
                    if (ret == ffmpeg.AVERROR_EOF)
                    {
                        _logger.LogInformation("RTSP stream EOF");
                        break;
                    }
                    _logger.LogWarning("av_read_frame failed: {Err}", FfmpegError.Describe(ret));
                    break;
                }

                if (packet->stream_index == audioStreamIndex)
                {
                    DecodeAudio(audioCtx, swr, audioFrame, packet, ct);
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                if (packet->stream_index != videoStreamIndex)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                Interlocked.Add(ref _bytesSinceFpsTick, packet->size);

                ret = ffmpeg.avcodec_send_packet(codecCtx, packet);
                ffmpeg.av_packet_unref(packet);
                if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    _logger.LogWarning("avcodec_send_packet failed: {Err}", FfmpegError.Describe(ret));
                    continue;
                }

                while (!ct.IsCancellationRequested)
                {
                    ret = ffmpeg.avcodec_receive_frame(codecCtx, frame);
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                        break;
                    if (ret < 0)
                    {
                        _logger.LogWarning("avcodec_receive_frame failed: {Err}", FfmpegError.Describe(ret));
                        break;
                    }

                    AVFrame* presentable = frame;
                    if (hwActive && frame->hw_frames_ctx != null)
                    {
                        var transferRet = ffmpeg.av_hwframe_transfer_data(swFrame, frame, 0);
                        if (transferRet < 0)
                        {
                            _logger.LogWarning("av_hwframe_transfer_data failed: {Err}", FfmpegError.Describe(transferRet));
                            ffmpeg.av_frame_unref(frame);
                            continue;
                        }
                        presentable = swFrame;
                    }

                    var framePixFmt = (AVPixelFormat)presentable->format;
                    if (sws == null || framePixFmt != swsSrcPixFmt)
                    {
                        if (sws != null) ffmpeg.sws_freeContext(sws);
                        sws = ffmpeg.sws_getContext(
                            _width, _height, framePixFmt,
                            _width, _height, AVPixelFormat.AV_PIX_FMT_BGRA,
                            ffmpeg.SWS_BILINEAR, null, null, null);
                        if (sws == null)
                            throw new InvalidOperationException($"sws_getContext returned null for {framePixFmt}");
                        swsSrcPixFmt = framePixFmt;
                    }

                    EmitFrame(sws, presentable);
                    ffmpeg.av_frame_unref(frame);
                    if (presentable == swFrame) ffmpeg.av_frame_unref(swFrame);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video session loop failed");
            SetState(SessionState.Failed, ex.Message);
            return;
        }
        finally
        {
            if (sws != null) ffmpeg.sws_freeContext(sws);
            if (swr != null) { var p = swr; ffmpeg.swr_free(&p); }
            if (frame != null) { var p = frame; ffmpeg.av_frame_free(&p); }
            if (swFrame != null) { var p = swFrame; ffmpeg.av_frame_free(&p); }
            if (audioFrame != null) { var p = audioFrame; ffmpeg.av_frame_free(&p); }
            if (packet != null) { var p = packet; ffmpeg.av_packet_free(&p); }
            if (codecCtx != null) { var p = codecCtx; ffmpeg.avcodec_free_context(&p); }
            if (audioCtx != null) { var p = audioCtx; ffmpeg.avcodec_free_context(&p); }
            if (hwDeviceCtx != null) { var p = hwDeviceCtx; ffmpeg.av_buffer_unref(&p); }
            if (fmtCtx != null) ffmpeg.avformat_close_input(&fmtCtx);
            if (opts != null) ffmpeg.av_dict_free(&opts);
        }

        SetState(SessionState.Idle);
    }

    private unsafe bool TryEnableHw(AVCodecContext* ctx, HwAccelHint hint, AVBufferRef** outDeviceCtx)
    {
        var (deviceType, hwPixFmt) = HwAccelSelector.MapToFfmpeg(hint);
        AVBufferRef* device = null;
        var ret = ffmpeg.av_hwdevice_ctx_create(&device, deviceType, null, null, 0);
        if (ret < 0)
        {
            _logger.LogWarning("av_hwdevice_ctx_create({Type}) failed: {Err}", deviceType, FfmpegError.Describe(ret));
            return false;
        }

        ctx->hw_device_ctx = ffmpeg.av_buffer_ref(device);
        *outDeviceCtx = device;

        _selectedHwPixFmt = hwPixFmt;
        _getFormatDelegate = GetFormatCallback;
        ctx->get_format = new AVCodecContext_get_format_func
        {
            Pointer = Marshal.GetFunctionPointerForDelegate(_getFormatDelegate),
        };
        return true;
    }

    private unsafe AVPixelFormat GetFormatCallback(AVCodecContext* ctx, AVPixelFormat* fmts)
    {
        for (var p = fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            if (*p == _selectedHwPixFmt) return *p;

        var fallback = *fmts;
        _logger.LogWarning("get_format: HW pixfmt {Fmt} not offered; falling back to software {Sw}",
            _selectedHwPixFmt, fallback);
        return fallback;
    }

    private static unsafe void TearDownHw(AVCodecContext* ctx, AVBufferRef** deviceCtx)
    {
        if (ctx->hw_device_ctx != null)
        {
            var p = ctx->hw_device_ctx;
            ffmpeg.av_buffer_unref(&p);
            ctx->hw_device_ctx = null;
        }
        ctx->get_format = default;
        if (*deviceCtx != null)
        {
            ffmpeg.av_buffer_unref(deviceCtx);
        }
    }

    private unsafe void EmitFrame(SwsContext* sws, AVFrame* frame)
    {
        var stride = _width * 4;
        var bufSize = stride * _height;
        var bgra = ArrayPool<byte>.Shared.Rent(bufSize);
        try
        {
            fixed (byte* dst = bgra)
            {
                var dstData = new byte_ptr4 { [0] = dst };
                var dstLinesize = new int4 { [0] = stride };
                ffmpeg.sws_scale(sws, frame->data, frame->linesize, 0, _height, dstData, dstLinesize);
            }

            UpdateSnapshotBuffer(bgra, stride);

            var vf = new VideoFrame(bgra, _width, _height, stride, frame->pts, DateTime.UtcNow);

            try
            {
                _frames.OnNext(vf);
                Interlocked.Increment(ref _framesDecoded);
                Interlocked.Increment(ref _framesSinceFpsTick);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Subscriber threw in frame OnNext");
            }

            MaybePublishTelemetry();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bgra);
        }
    }

    private unsafe int SetupAudio(AVFormatContext* fmtCtx, AVCodecContext** outCtx, SwrContext** outSwr, AVFrame** outFrame)
    {
        var idx = -1;
        for (var i = 0; i < (int)fmtCtx->nb_streams; i++)
        {
            if (fmtCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
            {
                idx = i;
                break;
            }
        }
        if (idx < 0) return -1;

        var codecpar = fmtCtx->streams[idx]->codecpar;
        var codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id);
        if (codec == null)
        {
            _logger.LogWarning("No audio decoder for codec id {Id}", codecpar->codec_id);
            return -1;
        }

        var ctx = ffmpeg.avcodec_alloc_context3(codec);
        var ret = ffmpeg.avcodec_parameters_to_context(ctx, codecpar);
        if (ret < 0)
        {
            _logger.LogWarning("audio avcodec_parameters_to_context failed: {Err}", FfmpegError.Describe(ret));
            ffmpeg.avcodec_free_context(&ctx);
            return -1;
        }

        ret = ffmpeg.avcodec_open2(ctx, codec, null);
        if (ret < 0)
        {
            _logger.LogWarning("audio avcodec_open2 failed: {Err}", FfmpegError.Describe(ret));
            ffmpeg.avcodec_free_context(&ctx);
            return -1;
        }

        AVChannelLayout defaultIn = default;
        AVChannelLayout* inLayout;
        if (ctx->ch_layout.nb_channels > 0)
        {
            inLayout = &ctx->ch_layout;
        }
        else
        {
            ffmpeg.av_channel_layout_default(&defaultIn, 1);
            inLayout = &defaultIn;
        }

        AVChannelLayout outLayout = default;
        ffmpeg.av_channel_layout_default(&outLayout, AudioOutChannels);

        SwrContext* swr = null;
        ret = ffmpeg.swr_alloc_set_opts2(
            &swr,
            &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, AudioOutSampleRate,
            inLayout, ctx->sample_fmt, ctx->sample_rate,
            0, null);
        if (ret < 0 || swr == null)
        {
            _logger.LogWarning("swr_alloc_set_opts2 failed: {Err}", FfmpegError.Describe(ret));
            ffmpeg.av_channel_layout_uninit(&outLayout);
            ffmpeg.avcodec_free_context(&ctx);
            return -1;
        }

        ret = ffmpeg.swr_init(swr);
        ffmpeg.av_channel_layout_uninit(&outLayout);
        if (ret < 0)
        {
            _logger.LogWarning("swr_init failed: {Err}", FfmpegError.Describe(ret));
            ffmpeg.swr_free(&swr);
            ffmpeg.avcodec_free_context(&ctx);
            return -1;
        }

        var baseName = Marshal.PtrToStringAnsi((IntPtr)codec->name);
        _logger.LogInformation("Audio decode active: {Codec} {Rate}Hz {Ch}ch → 48kHz stereo S16",
            baseName, ctx->sample_rate, ctx->ch_layout.nb_channels);

        *outCtx = ctx;
        *outSwr = swr;
        *outFrame = ffmpeg.av_frame_alloc();
        return idx;
    }

    private unsafe void DecodeAudio(AVCodecContext* ctx, SwrContext* swr, AVFrame* frame, AVPacket* packet, CancellationToken ct)
    {
        if (ctx == null || swr == null || frame == null) return;

        var ret = ffmpeg.avcodec_send_packet(ctx, packet);
        if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            _logger.LogDebug("audio send_packet failed: {Err}", FfmpegError.Describe(ret));
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            ret = ffmpeg.avcodec_receive_frame(ctx, frame);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                break;
            if (ret < 0)
            {
                _logger.LogDebug("audio receive_frame failed: {Err}", FfmpegError.Describe(ret));
                break;
            }

            EmitAudioFrame(swr, frame, ctx->sample_rate);
            ffmpeg.av_frame_unref(frame);
        }
    }

    private unsafe void EmitAudioFrame(SwrContext* swr, AVFrame* frame, int inRate)
    {
        if (inRate <= 0) return;

        var delay = ffmpeg.swr_get_delay(swr, inRate);
        var maxOut = (int)ffmpeg.av_rescale_rnd(delay + frame->nb_samples, AudioOutSampleRate, inRate, AVRounding.AV_ROUND_UP);
        if (maxOut <= 0) return;

        var pcm = new byte[maxOut * AudioOutChannels * 2];
        int produced;
        fixed (byte* dst = pcm)
        {
            var outPlane = dst;
            produced = ffmpeg.swr_convert(swr, &outPlane, maxOut, frame->extended_data, frame->nb_samples);
        }
        if (produced <= 0) return;

        var bytes = produced * AudioOutChannels * 2;
        var payload = pcm;
        if (bytes != pcm.Length)
        {
            payload = new byte[bytes];
            Buffer.BlockCopy(pcm, 0, payload, 0, bytes);
        }

        var af = new AudioFrame(payload, AudioOutSampleRate, AudioOutChannels, 0);
        try
        {
            _audioFrames.OnNext(af);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Subscriber threw in audio OnNext");
        }
    }

    private void UpdateSnapshotBuffer(byte[] sourceBgra, int stride)
    {
        var size = stride * _height;
        lock (_snapshotLock)
        {
            if (_snapshotBgra is null || _snapshotBgra.Length < size)
                _snapshotBgra = new byte[size];
            Buffer.BlockCopy(sourceBgra, 0, _snapshotBgra, 0, size);
            _snapshotWidth = _width;
            _snapshotHeight = _height;
            _snapshotStride = stride;
        }
    }

    private void MaybePublishTelemetry()
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastFpsTick;
        if (elapsed.TotalSeconds < 1)
            return;

        var sinceLast = Interlocked.Exchange(ref _framesSinceFpsTick, 0);
        var fps = sinceLast / elapsed.TotalSeconds;
        var bytes = Interlocked.Exchange(ref _bytesSinceFpsTick, 0);
        var bitrateKbps = bytes * 8.0 / 1000.0 / elapsed.TotalSeconds;
        _lastFpsTick = now;

        _telemetry.OnNext(new SessionTelemetry(
            FramesDecoded: _framesDecoded,
            FramesDropped: 0,
            Fps: fps,
            AverageLatency: TimeSpan.Zero,
            Codec: _codecName,
            Width: _width,
            Height: _height,
            CapturedAt: now,
            BitrateKbps: bitrateKbps));
    }

    private void SetState(SessionState newState, string? error = null)
    {
        lock (_stateLock)
        {
            _state = newState;
            _lastError = error;
        }
        _stateChanged.OnNext(newState);
    }

    private unsafe void BuildOpts(AVDictionary** opts)
    {
        var transport = _options.Transport switch
        {
            RtspTransport.Tcp => "tcp",
            RtspTransport.Udp => "udp",
            _ => "tcp",
        };
        ffmpeg.av_dict_set(opts, "rtsp_transport", transport, 0);
        ffmpeg.av_dict_set(opts, "stimeout", "3000000", 0);           // Timeout a 3s (µs)
        ffmpeg.av_dict_set(opts, "max_delay", "100000", 0);           // Reducido a 100ms de latencia máxima
        ffmpeg.av_dict_set(opts, "buffer_size", "524288", 0);         // Reducido a 512KB para evitar acumulación
        ffmpeg.av_dict_set(opts, "reorder_queue_size", "0", 0);
        ffmpeg.av_dict_set(opts, "fflags", "nobuffer+discardcorrupt", 0); // Descarte de corrupciones sin buffering
        ffmpeg.av_dict_set(opts, "flags", "low_delay", 0);            // Modo ultra baja latencia
        ffmpeg.av_dict_set(opts, "framedrop", "1", 0);                // Habilita descarte automático de frames atrasados
        ffmpeg.av_dict_set(opts, "probesize", "32768", 0);            // 32KB para análisis ultrarrápido del stream
        ffmpeg.av_dict_set(opts, "analyzeduration", "100000", 0);     // 100ms de análisis de duración de stream
    }

    private static string BuildUrlWithCredentials(Uri uri, CameraCredentials? creds)
    {
        if (creds is null || !string.IsNullOrEmpty(uri.UserInfo))
            return uri.ToString();

        var builder = new UriBuilder(uri)
        {
            UserName = Uri.EscapeDataString(creds.Username),
            Password = Uri.EscapeDataString(creds.Password),
        };
        return builder.Uri.ToString();
    }
}
