using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.IO;
using System.Text;

namespace Cursivis.Companion.Services;

public sealed class VoiceCaptureService
{
    public bool HasInputDevice => VoiceCaptureDeviceFactory.TryCreateCaptureDescriptor(out var descriptor) && DisposeCaptureDescriptor(descriptor);

    public VoiceCaptureSession CreateSession()
    {
        if (!VoiceCaptureDeviceFactory.TryCreateCaptureDescriptor(out var descriptor) || descriptor is null)
        {
            throw new InvalidOperationException("No microphone input device is available.");
        }

        return new VoiceCaptureSession(descriptor);
    }

    public async Task<byte[]?> CaptureWavAsync(TimeSpan maxDuration, CancellationToken cancellationToken)
    {
        if (!HasInputDevice)
        {
            return null;
        }

        using var session = CreateSession();
        session.Start();

        try
        {
            await Task.Delay(maxDuration, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Stopping capture due to cancellation is expected for hold-to-record UX.
        }

        return await session.StopAsync();
    }

    private static bool DisposeCaptureDescriptor(VoiceCaptureDescriptor? descriptor)
    {
        if (descriptor?.Device is IDisposable disposable)
        {
            disposable.Dispose();
        }

        return descriptor is not null;
    }
}

public sealed class VoiceCaptureSession : IDisposable
{
    private const double SpeechLevelThreshold = 0.0042;
    private const double SpeechPeakThreshold = 0.019;
    private readonly object _sync = new();
    private readonly IWaveIn _waveIn;
    private readonly WaveFormat _waveFormat;
    private readonly MemoryStream _memoryStream;
    private readonly WaveFileWriter _writer;
    private readonly TaskCompletionSource _stoppedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _sourceName;
    private bool _isStarted;
    private bool _isStopped;
    private bool _hasDetectedSpeech;
    private DateTime _startedUtc;
    private DateTime _lastSpeechDetectedUtc;
    private long _capturedBytes;
    private double _noiseFloorRms = 0.0014;
    private double _noiseFloorPeak = 0.009;
    private double _currentInputLevel;

    public event EventHandler<VoiceChunkEventArgs>? ChunkAvailable;
    public event EventHandler<VoiceLevelChangedEventArgs>? InputLevelChanged;

    public string SourceName => _sourceName;

    public bool HasDetectedSpeech
    {
        get
        {
            lock (_sync)
            {
                return _hasDetectedSpeech;
            }
        }
    }

    public DateTime LastSpeechDetectedUtc
    {
        get
        {
            lock (_sync)
            {
                return _lastSpeechDetectedUtc;
            }
        }
    }

    public TimeSpan CapturedDuration
    {
        get
        {
            lock (_sync)
            {
                if (_waveFormat.AverageBytesPerSecond <= 0)
                {
                    return TimeSpan.Zero;
                }

                return TimeSpan.FromSeconds(_capturedBytes / (double)_waveFormat.AverageBytesPerSecond);
            }
        }
    }

    public double CurrentInputLevel
    {
        get
        {
            lock (_sync)
            {
                return _currentInputLevel;
            }
        }
    }

    public VoiceCaptureSession(VoiceCaptureDescriptor descriptor)
    {
        _waveIn = descriptor.Device;
        _waveFormat = descriptor.Format;
        _sourceName = descriptor.SourceName;
        _memoryStream = new MemoryStream();
        _writer = new WaveFileWriter(_memoryStream, _waveFormat);

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_isStarted || _isStopped)
            {
                return;
            }

            _isStarted = true;
            _startedUtc = DateTime.UtcNow;
            _lastSpeechDetectedUtc = _startedUtc;
            _waveIn.StartRecording();
        }
    }

    public async Task<byte[]?> StopAsync()
    {
        lock (_sync)
        {
            if (_isStopped)
            {
                return ToWavBytes();
            }

            _isStopped = true;
            if (_isStarted)
            {
                try
                {
                    _waveIn.StopRecording();
                }
                catch
                {
                    _stoppedTcs.TrySetResult();
                }
            }
            else
            {
                _stoppedTcs.TrySetResult();
            }
        }

        await _stoppedTcs.Task;
        RaiseInputLevelChanged(0, false, 0, 0);
        return ToWavBytes();
    }

    public byte[]? GetSnapshot()
    {
        lock (_sync)
        {
            var bytes = _memoryStream.ToArray();
            if (bytes.Length <= 80)
            {
                return null;
            }

            return NormalizeWavHeader(bytes);
        }
    }

    public void Dispose()
    {
        try
        {
            _ = StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Swallow disposal-time capture errors.
        }

        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.RecordingStopped -= OnRecordingStopped;
        if (_waveIn is IDisposable disposable)
        {
            disposable.Dispose();
        }
        _writer.Dispose();
        _memoryStream.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        var metrics = AnalyzeAudioMetrics(e.Buffer, e.BytesRecorded);
        var looksLikeSpeech = false;
        var inputLevel = 0.0;

        lock (_sync)
        {
            if (_isStopped)
            {
                return;
            }

            _writer.Write(e.Buffer, 0, e.BytesRecorded);
            _writer.Flush();
            _capturedBytes += e.BytesRecorded;

            looksLikeSpeech = LooksLikeSpeech(metrics);
            _currentInputLevel = NormalizeInputLevel(metrics);
            inputLevel = _currentInputLevel;

            if (looksLikeSpeech)
            {
                _hasDetectedSpeech = true;
                _lastSpeechDetectedUtc = DateTime.UtcNow;
            }
            else
            {
                _noiseFloorRms = (_noiseFloorRms * 0.92) + (metrics.Rms * 0.08);
                _noiseFloorPeak = (_noiseFloorPeak * 0.92) + (metrics.Peak * 0.08);
            }
        }

        var chunk = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);
        ChunkAvailable?.Invoke(this, new VoiceChunkEventArgs(chunk, _waveFormat.SampleRate, _waveFormat.Channels));
        RaiseInputLevelChanged(inputLevel, looksLikeSpeech, metrics.Rms, metrics.Peak);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _stoppedTcs.TrySetResult();
    }

    private byte[]? ToWavBytes()
    {
        lock (_sync)
        {
            _writer.Flush();
            var bytes = _memoryStream.ToArray();
            if (bytes.Length <= 80)
            {
                return null;
            }

            return NormalizeWavHeader(bytes);
        }
    }

    private static byte[] NormalizeWavHeader(byte[] rawBytes)
    {
        if (rawBytes.Length < 44)
        {
            return rawBytes;
        }

        var normalized = (byte[])rawBytes.Clone();
        if (!Encoding.ASCII.GetString(normalized, 0, 4).Equals("RIFF", StringComparison.Ordinal) ||
            !Encoding.ASCII.GetString(normalized, 8, 4).Equals("WAVE", StringComparison.Ordinal))
        {
            return normalized;
        }

        var fileSizeMinusEight = normalized.Length - 8;
        BitConverter.GetBytes(fileSizeMinusEight).CopyTo(normalized, 4);

        var dataChunkOffset = FindDataChunkOffset(normalized);
        if (dataChunkOffset >= 0 && dataChunkOffset + 8 <= normalized.Length)
        {
            var dataSize = normalized.Length - (dataChunkOffset + 8);
            BitConverter.GetBytes(Math.Max(0, dataSize)).CopyTo(normalized, dataChunkOffset + 4);
        }

        return normalized;
    }

    private static int FindDataChunkOffset(byte[] bytes)
    {
        for (var i = 12; i + 8 <= bytes.Length; i++)
        {
            if (bytes[i] == (byte)'d' &&
                bytes[i + 1] == (byte)'a' &&
                bytes[i + 2] == (byte)'t' &&
                bytes[i + 3] == (byte)'a')
            {
                return i;
            }

            if (i + 8 > bytes.Length)
            {
                break;
            }

            var chunkSize = BitConverter.ToInt32(bytes, i + 4);
            if (chunkSize < 0)
            {
                break;
            }

            var nextOffset = i + 8 + chunkSize;
            if (nextOffset <= i)
            {
                break;
            }

            i = nextOffset - 1;
        }

        return -1;
    }

    private bool LooksLikeSpeech(AudioMetrics metrics)
    {
        var dynamicRmsThreshold = Math.Max(SpeechLevelThreshold, _noiseFloorRms * 2.3);
        var dynamicPeakThreshold = Math.Max(SpeechPeakThreshold, _noiseFloorPeak * 2.1);
        return metrics.Rms >= dynamicRmsThreshold || metrics.Peak >= dynamicPeakThreshold;
    }

    private static AudioMetrics AnalyzeAudioMetrics(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded < 2)
        {
            return AudioMetrics.Silent;
        }

        double sumSquares = 0;
        double peak = 0;
        var sampleCount = 0;
        for (var i = 0; i + 1 < bytesRecorded; i += 2)
        {
            var sample = BitConverter.ToInt16(buffer, i) / 32768.0;
            sumSquares += sample * sample;
            var absolute = Math.Abs(sample);
            if (absolute > peak)
            {
                peak = absolute;
            }

            sampleCount += 1;
        }

        if (sampleCount == 0)
        {
            return AudioMetrics.Silent;
        }

        return new AudioMetrics(Math.Sqrt(sumSquares / sampleCount), peak);
    }

    private double NormalizeInputLevel(AudioMetrics metrics)
    {
        var floor = Math.Max(0.0008, _noiseFloorRms);
        var dynamicRmsThreshold = Math.Max(SpeechLevelThreshold, _noiseFloorRms * 2.3);
        var dynamicPeakThreshold = Math.Max(SpeechPeakThreshold, _noiseFloorPeak * 2.1);
        var rmsRange = Math.Max(dynamicRmsThreshold * 5.5, 0.04) - floor;
        var peakRange = Math.Max(dynamicPeakThreshold * 3.2, 0.18) - _noiseFloorPeak;
        var rmsNormalized = rmsRange <= 0 ? 0 : (metrics.Rms - floor) / rmsRange;
        var peakNormalized = peakRange <= 0 ? 0 : (metrics.Peak - _noiseFloorPeak) / peakRange;
        return Math.Clamp(Math.Max(rmsNormalized * 0.78, peakNormalized), 0, 1);
    }

    private void RaiseInputLevelChanged(double level, bool speechDetected, double rms, double peak)
    {
        InputLevelChanged?.Invoke(this, new VoiceLevelChangedEventArgs(level, speechDetected, rms, peak));
    }

    private sealed record AudioMetrics(double Rms, double Peak)
    {
        public static AudioMetrics Silent { get; } = new(0, 0);
    }
}

public sealed class VoiceLevelChangedEventArgs : EventArgs
{
    public VoiceLevelChangedEventArgs(double level, bool speechDetected, double rms, double peak)
    {
        Level = level;
        SpeechDetected = speechDetected;
        Rms = rms;
        Peak = peak;
    }

    public double Level { get; }

    public bool SpeechDetected { get; }

    public double Rms { get; }

    public double Peak { get; }
}

public sealed record VoiceCaptureDescriptor(IWaveIn Device, WaveFormat Format, string SourceName);

internal static class VoiceCaptureDeviceFactory
{
    public static bool TryCreateCaptureDescriptor(out VoiceCaptureDescriptor? descriptor)
    {
        descriptor = TryCreateWasapiCapture();
        if (descriptor is not null)
        {
            return true;
        }

        descriptor = TryCreateWaveInCapture();
        return descriptor is not null;
    }

    private static VoiceCaptureDescriptor? TryCreateWasapiCapture()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = GetDefaultCaptureDevice(enumerator);
            if (device is null)
            {
                return null;
            }

            var capture = new WasapiCapture(device);
            return new VoiceCaptureDescriptor(capture, capture.WaveFormat, $"Default microphone ({device.FriendlyName})");
        }
        catch
        {
            return null;
        }
    }

    private static MMDevice? GetDefaultCaptureDevice(MMDeviceEnumerator enumerator)
    {
        foreach (var role in new[] { Role.Communications, Role.Multimedia, Role.Console })
        {
            try
            {
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
            }
            catch
            {
                // Try the next role.
            }
        }

        return null;
    }

    private static VoiceCaptureDescriptor? TryCreateWaveInCapture()
    {
        try
        {
            if (WaveInEvent.DeviceCount <= 0)
            {
                return null;
            }

            var deviceNumber = ResolveWaveInDeviceNumber();
            var waveIn = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 90
            };
            var capabilities = WaveIn.GetCapabilities(deviceNumber);
            return new VoiceCaptureDescriptor(waveIn, waveIn.WaveFormat, $"Microphone ({capabilities.ProductName})");
        }
        catch
        {
            return null;
        }
    }

    private static int ResolveWaveInDeviceNumber()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultCapture = GetDefaultCaptureDevice(enumerator);
            if (defaultCapture is null)
            {
                return 0;
            }

            var friendlyName = defaultCapture.FriendlyName;
            for (var i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var capabilities = WaveIn.GetCapabilities(i);
                if (friendlyName.Contains(capabilities.ProductName, StringComparison.OrdinalIgnoreCase) ||
                    capabilities.ProductName.Contains(friendlyName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }
        catch
        {
            // Fall back to the first wave input device.
        }

        return 0;
    }
}

public sealed class VoiceChunkEventArgs : EventArgs
{
    public VoiceChunkEventArgs(byte[] data, int sampleRate, int channels)
    {
        Data = data;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public byte[] Data { get; }

    public int SampleRate { get; }

    public int Channels { get; }

    public string MimeType => $"audio/pcm;rate={SampleRate}";
}
