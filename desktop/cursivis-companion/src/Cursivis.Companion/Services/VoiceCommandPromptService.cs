using Cursivis.Companion.Views;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace Cursivis.Companion.Services;

public sealed class VoiceCommandPromptService
{
    private readonly GeminiClient _geminiClient;
    private readonly VoiceCaptureService _voiceCaptureService;
    private readonly bool _enableStreamingTranscription;
    private readonly bool _requireVoiceConfirmation;
    private readonly bool _enableLiveVoiceApi;
    private readonly TimeSpan _maxVoiceDuration;
    private readonly TimeSpan _streamProbeEvery;
    private readonly TimeSpan _autoStopSilenceDuration;
    private readonly TimeSpan _initialSpeechTimeout;
    private readonly TimeSpan _voiceTranscriptionTimeout;
    private readonly string _voiceDebugDirectory;

    public VoiceCommandPromptService(GeminiClient geminiClient, VoiceCaptureService voiceCaptureService)
    {
        _geminiClient = geminiClient;
        _voiceCaptureService = voiceCaptureService;
        _enableStreamingTranscription = ParseBoolEnv("CURSIVIS_ENABLE_STREAMING_TRANSCRIPTION", defaultValue: false);
        _requireVoiceConfirmation = ParseBoolEnv("CURSIVIS_VOICE_CONFIRM", defaultValue: false);
        _enableLiveVoiceApi = ParseBoolEnv("CURSIVIS_ENABLE_LIVE_API_VOICE", defaultValue: false);
        _maxVoiceDuration = TimeSpan.FromSeconds(ParseIntEnv("CURSIVIS_MAX_VOICE_SECONDS", defaultValue: 45, min: 5, max: 180));
        _streamProbeEvery = TimeSpan.FromSeconds(ParseIntEnv("CURSIVIS_STREAM_PROBE_SECONDS", defaultValue: 2, min: 1, max: 8));
        _autoStopSilenceDuration = ResolveSilenceDuration();
        _initialSpeechTimeout = TimeSpan.FromMilliseconds(ParseIntEnv("CURSIVIS_INITIAL_SPEECH_TIMEOUT_MS", defaultValue: 9000, min: 2000, max: 20000));
        _voiceTranscriptionTimeout = TimeSpan.FromSeconds(ParseIntEnv("CURSIVIS_VOICE_TRANSCRIBE_TIMEOUT_SECONDS", defaultValue: 12, min: 5, max: 30));
        _voiceDebugDirectory = Path.Combine(Path.GetTempPath(), "Cursivis", "voice-debug");
    }

    public async Task<string?> PromptAsync(
        Action<Models.OrbState, string>? statusChanged = null,
        Action<double>? inputLevelChanged = null,
        CancellationToken cancellationToken = default)
    {
        void Report(Models.OrbState state, string message)
        {
            statusChanged?.Invoke(state, message);
        }

        void ReportInputLevel(double level)
        {
            inputLevelChanged?.Invoke(Math.Clamp(level, 0, 1));
        }

        string? partialTranscript = null;
        string? finalTranscript = null;
        byte[]? capturedAudio = null;
        var debugSession = new VoiceDebugSession
        {
            StartedAtUtc = DateTime.UtcNow,
            LiveVoiceEnabled = _enableLiveVoiceApi,
            StreamingTranscriptionEnabled = _enableStreamingTranscription,
            VoiceConfirmationRequired = _requireVoiceConfirmation,
            MaxVoiceDurationMs = (int)_maxVoiceDuration.TotalMilliseconds,
            SilenceDurationMs = (int)_autoStopSilenceDuration.TotalMilliseconds,
            InitialSpeechTimeoutMs = (int)_initialSpeechTimeout.TotalMilliseconds,
            TranscriptionTimeoutMs = (int)_voiceTranscriptionTimeout.TotalMilliseconds
        };

        void RecordOutcome(string disposition, string? command)
        {
            debugSession.ResultDisposition = disposition;
            debugSession.ReturnedCommand = command;
        }

        try
        {
        if (_voiceCaptureService.HasInputDevice)
        {
            if (_enableLiveVoiceApi)
            {
                try
                {
                    Report(Models.OrbState.Listening, "Recording... tap stop");
                    ReportInputLevel(0);
                    await using var liveClient = new LiveVoiceCommandClient();
                    await liveClient.ConnectAsync(cancellationToken);
                    liveClient.TranscriptUpdated += (_, text) => partialTranscript = text;

                    using var session = _voiceCaptureService.CreateSession();
                    debugSession.SourceName = session.SourceName;
                    session.InputLevelChanged += OnInputLevelChanged;
                    session.ChunkAvailable += OnVoiceChunkAvailable;
                    session.Start();

                    var startedAt = DateTime.UtcNow;
                    var processingMessage = "Processing voice...";
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        if (DateTime.UtcNow - startedAt >= _maxVoiceDuration)
                        {
                            processingMessage = "Voice hold limit reached. Processing...";
                            break;
                        }

                        if (!session.HasDetectedSpeech &&
                            DateTime.UtcNow - startedAt >= _initialSpeechTimeout)
                        {
                            processingMessage = "No clear speech detected yet. Checking captured audio...";
                            break;
                        }

                        if (session.HasDetectedSpeech &&
                            DateTime.UtcNow - session.LastSpeechDetectedUtc >= _autoStopSilenceDuration)
                        {
                            processingMessage = "Speech pause detected. Processing...";
                            break;
                        }

                        await Task.Delay(100, CancellationToken.None);
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        processingMessage = session.HasDetectedSpeech
                            ? "Stopped listening. Processing..."
                            : "Stopped listening. Checking captured audio...";
                    }

                    session.InputLevelChanged -= OnInputLevelChanged;
                    session.ChunkAvailable -= OnVoiceChunkAvailable;
                    ReportInputLevel(0);
                    Report(Models.OrbState.Processing, processingMessage);
                    capturedAudio = await session.StopAsync();
                    debugSession.CapturedAudioBytes = capturedAudio?.Length ?? 0;
                    debugSession.CapturedDurationMs = (int)session.CapturedDuration.TotalMilliseconds;
                    debugSession.DetectedSpeech = session.HasDetectedSpeech;
                    await liveClient.CompleteAudioAsync(CancellationToken.None);
                    finalTranscript = await liveClient.WaitForFinalTranscriptAsync(TimeSpan.FromSeconds(4), CancellationToken.None);

                    if (!string.IsNullOrWhiteSpace(finalTranscript) || !string.IsNullOrWhiteSpace(partialTranscript))
                    {
                        var liveTranscript = !string.IsNullOrWhiteSpace(finalTranscript) ? finalTranscript : partialTranscript;
                        if (!string.IsNullOrWhiteSpace(liveTranscript) && !_requireVoiceConfirmation)
                        {
                            RecordOutcome("live-direct", liveTranscript.Trim());
                            Report(Models.OrbState.Completed, "Voice command ready");
                            return liveTranscript.Trim();
                        }

                        Report(Models.OrbState.Completed, "Voice captured. Review before running.");
                        var reviewedCommand = await ShowDialogAsync(
                            liveTranscript,
                            "Voice captured",
                            "Review or refine the spoken command before Cursivis runs it.");
                        RecordOutcome("live-reviewed", reviewedCommand);
                        return reviewedCommand;
                    }

                    void OnVoiceChunkAvailable(object? sender, VoiceChunkEventArgs args)
                    {
                        _ = liveClient.SendAudioChunkAsync(args.Data, args.MimeType, CancellationToken.None);
                    }

                    void OnInputLevelChanged(object? sender, VoiceLevelChangedEventArgs args)
                    {
                        debugSession.MaxObservedLevel = Math.Max(debugSession.MaxObservedLevel, args.Level);
                        debugSession.MaxObservedRms = Math.Max(debugSession.MaxObservedRms, args.Rms);
                        debugSession.MaxObservedPeak = Math.Max(debugSession.MaxObservedPeak, args.Peak);
                        debugSession.LevelSampleCount += 1;
                        if (args.SpeechDetected)
                        {
                            debugSession.SpeechSampleCount += 1;
                        }

                        ReportInputLevel(args.Level);
                    }
                }
                catch (Exception ex)
                {
                    debugSession.Notes.Add($"Live voice fallback: {ex.Message}");
                    ReportInputLevel(0);
                    Report(Models.OrbState.Processing, "Realtime voice unavailable. Falling back...");
                }
            }

            try
            {
            Report(Models.OrbState.Listening, "Recording... tap stop");
                ReportInputLevel(0);
                using var session = _voiceCaptureService.CreateSession();
                debugSession.SourceName ??= session.SourceName;
                session.InputLevelChanged += OnInputLevelChanged;
                session.Start();

                var startedAt = DateTime.UtcNow;
                var lastProbeAt = DateTime.MinValue;
                var processingMessage = "Transcribing voice...";

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (DateTime.UtcNow - startedAt >= _maxVoiceDuration)
                    {
                        processingMessage = "Voice hold limit reached. Transcribing...";
                        break;
                    }

                    if (!session.HasDetectedSpeech &&
                        DateTime.UtcNow - startedAt >= _initialSpeechTimeout)
                    {
                        processingMessage = "No clear speech detected yet. Checking captured audio...";
                        break;
                    }

                    if (session.HasDetectedSpeech &&
                        DateTime.UtcNow - session.LastSpeechDetectedUtc >= _autoStopSilenceDuration)
                    {
                        processingMessage = "Speech pause detected. Transcribing...";
                        break;
                    }

                    await Task.Delay(150);
                    if (!_enableStreamingTranscription)
                    {
                        continue;
                    }

                    if (DateTime.UtcNow - lastProbeAt < _streamProbeEvery)
                    {
                        continue;
                    }

                    lastProbeAt = DateTime.UtcNow;
                    var snapshot = session.GetSnapshot();
                    if (snapshot is null)
                    {
                        continue;
                    }

                    try
                    {
                        var candidate = await _geminiClient.TranscribeVoiceAsync(snapshot, "audio/wav", CancellationToken.None);
                        if (!string.IsNullOrWhiteSpace(candidate))
                        {
                            partialTranscript = candidate;
                        }
                    }
                    catch
                    {
                        // Continue recording and attempt final transcription.
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    processingMessage = session.HasDetectedSpeech
                        ? "Stopped listening. Transcribing..."
                        : "Stopped listening. Checking captured audio...";
                }

                session.InputLevelChanged -= OnInputLevelChanged;
                var captured = await session.StopAsync();
                capturedAudio = captured;
                debugSession.CapturedAudioBytes = captured?.Length ?? 0;
                debugSession.CapturedDurationMs = (int)session.CapturedDuration.TotalMilliseconds;
                debugSession.DetectedSpeech = session.HasDetectedSpeech;
                ReportInputLevel(0);
                if (captured is not null)
                {
                    try
                    {
                        Report(Models.OrbState.Processing, processingMessage);
                        finalTranscript = await TranscribeBufferedAudioAsync(captured, debugSession);
                    }
                    catch (Exception ex)
                    {
                        debugSession.Notes.Add($"Buffered transcription failed: {ex.Message}");
                        Report(Models.OrbState.Completed, "Couldn't transcribe clearly. Switching to text.");
                    }
                }
                else
                {
                    debugSession.Notes.Add("No buffered audio bytes were captured.");
                }

                void OnInputLevelChanged(object? sender, VoiceLevelChangedEventArgs args)
                {
                    debugSession.MaxObservedLevel = Math.Max(debugSession.MaxObservedLevel, args.Level);
                    debugSession.MaxObservedRms = Math.Max(debugSession.MaxObservedRms, args.Rms);
                    debugSession.MaxObservedPeak = Math.Max(debugSession.MaxObservedPeak, args.Peak);
                    debugSession.LevelSampleCount += 1;
                    if (args.SpeechDetected)
                    {
                        debugSession.SpeechSampleCount += 1;
                    }

                    ReportInputLevel(args.Level);
                }
            }
            catch (Exception ex)
            {
                debugSession.Notes.Add($"Voice capture fallback: {ex.Message}");
                ReportInputLevel(0);
                Report(Models.OrbState.Completed, "Voice capture unavailable. Switching to text.");
            }
        }
        else
        {
            debugSession.Notes.Add("No microphone detected.");
            ReportInputLevel(0);
            Report(Models.OrbState.Completed, "No microphone detected. Type your command instead.");
        }

        var initialCommand = !string.IsNullOrWhiteSpace(finalTranscript) ? finalTranscript : partialTranscript;
        if (!string.IsNullOrWhiteSpace(initialCommand) && !_requireVoiceConfirmation)
        {
            RecordOutcome("transcribed-direct", initialCommand.Trim());
            Report(Models.OrbState.Completed, "Voice command ready");
            return initialCommand.Trim();
        }

        Report(
            Models.OrbState.Completed,
            string.IsNullOrWhiteSpace(initialCommand)
                ? "Voice input wasn't captured. Type the command instead."
                : "Voice captured. Review or refine it.");
        ReportInputLevel(0);

        var fallbackCommand = await ShowDialogAsync(
            initialCommand,
            string.IsNullOrWhiteSpace(initialCommand) ? "Voice fallback" : "Voice command ready",
            string.IsNullOrWhiteSpace(initialCommand)
                ? "Cursivis could not confidently capture a voice command this time. Type it below instead."
                : "Edit the spoken command if you want to be more specific before Cursivis runs it.");
        RecordOutcome(
            string.IsNullOrWhiteSpace(initialCommand) ? "typed-fallback" : "transcribed-reviewed",
            fallbackCommand);

        return fallbackCommand;
        }
        finally
        {
            debugSession.CompletedAtUtc = DateTime.UtcNow;
            debugSession.PartialTranscript = partialTranscript;
            debugSession.FinalTranscript = finalTranscript;
            await PersistVoiceDebugArtifactAsync(debugSession, capturedAudio);
        }
    }

    private static Task<string?> ShowDialogAsync(string? initialCommand, string? statusTitle, string? statusMessage)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new VoiceCommandWindow(initialCommand, statusTitle, statusMessage);
            var accepted = dialog.ShowDialog();
            return accepted == true ? dialog.VoiceCommand : null;
        }).Task;
    }

    private async Task<string?> TranscribeBufferedAudioAsync(byte[] captured, VoiceDebugSession debugSession)
    {
        var transcript = await TryTranscribeAsync(captured, "audio/wav", _voiceTranscriptionTimeout, debugSession);
        if (!string.IsNullOrWhiteSpace(transcript))
        {
            debugSession.Notes.Add("Transcription succeeded with raw captured audio.");
            return transcript;
        }

        await Task.Delay(120);
        transcript = await TryTranscribeAsync(captured, "audio/x-wav", TimeSpan.FromSeconds(Math.Max(6, _voiceTranscriptionTimeout.TotalSeconds - 2)), debugSession);
        if (!string.IsNullOrWhiteSpace(transcript))
        {
            debugSession.Notes.Add("Transcription succeeded with raw captured audio using audio/x-wav.");
            return transcript;
        }

        var normalizedAudio = NormalizeCapturedAudioForTranscription(captured, debugSession);
        transcript = await TryTranscribeAsync(normalizedAudio, "audio/wav", _voiceTranscriptionTimeout, debugSession);
        if (!string.IsNullOrWhiteSpace(transcript))
        {
            debugSession.Notes.Add("Transcription succeeded after audio normalization.");
            return transcript;
        }

        await Task.Delay(120);
        transcript = await TryTranscribeAsync(normalizedAudio, "audio/x-wav", TimeSpan.FromSeconds(Math.Max(6, _voiceTranscriptionTimeout.TotalSeconds - 2)), debugSession);
        if (!string.IsNullOrWhiteSpace(transcript))
        {
            debugSession.Notes.Add("Transcription succeeded after audio normalization using audio/x-wav.");
            return transcript;
        }

        debugSession.Notes.Add("Transcription attempts failed for both raw and normalized audio.");
        return null;
    }

    private static byte[] NormalizeCapturedAudioForTranscription(byte[] captured, VoiceDebugSession debugSession)
    {
        try
        {
            using var inputStream = new MemoryStream(captured, writable: false);
            using var waveReader = new WaveFileReader(inputStream);
            var inputFormat = waveReader.WaveFormat;

            ISampleProvider sampleProvider = waveReader.ToSampleProvider();
            if (sampleProvider.WaveFormat.Channels > 1)
            {
                sampleProvider = sampleProvider.WaveFormat.Channels == 2
                    ? new StereoToMonoSampleProvider(sampleProvider)
                    {
                        LeftVolume = 0.5f,
                        RightVolume = 0.5f
                    }
                    : new MultiplexingSampleProvider(
                        Enumerable.Repeat(sampleProvider, sampleProvider.WaveFormat.Channels),
                        1);
            }

            var resampled = sampleProvider.WaveFormat.SampleRate == 16000
                ? sampleProvider
                : new WdlResamplingSampleProvider(sampleProvider, 16000);

            using var normalizedStream = new MemoryStream();
            WaveFileWriter.WriteWavFileToStream(normalizedStream, resampled.ToWaveProvider16());
            var normalizedBytes = normalizedStream.ToArray();

            debugSession.InputSampleRate = inputFormat.SampleRate;
            debugSession.InputBitsPerSample = inputFormat.BitsPerSample;
            debugSession.InputChannels = inputFormat.Channels;
            debugSession.NormalizedAudioBytes = normalizedBytes.Length;
            debugSession.NormalizedSampleRate = 16000;
            debugSession.NormalizedBitsPerSample = 16;
            debugSession.NormalizedChannels = 1;

            return normalizedBytes;
        }
        catch (Exception ex)
        {
            debugSession.Notes.Add($"Audio normalization skipped: {ex.Message}");
            return captured;
        }
    }

    private async Task<string?> TryTranscribeAsync(byte[] captured, string mimeType, TimeSpan timeout, VoiceDebugSession debugSession)
    {
        var startedAt = DateTime.UtcNow;
        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            var transcript = await _geminiClient.TranscribeVoiceAsync(captured, mimeType, timeoutCts.Token);
            debugSession.TranscriptionAttempts.Add(new VoiceTranscriptionAttempt
            {
                MimeType = mimeType,
                DurationMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds,
                Success = !string.IsNullOrWhiteSpace(transcript),
                TimedOut = false,
                TranscriptLength = transcript?.Length ?? 0
            });
            return transcript;
        }
        catch (OperationCanceledException)
        {
            debugSession.TranscriptionAttempts.Add(new VoiceTranscriptionAttempt
            {
                MimeType = mimeType,
                DurationMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds,
                Success = false,
                TimedOut = true,
                Error = $"Timed out after {timeout.TotalSeconds:0.#}s"
            });
            return null;
        }
        catch (Exception ex)
        {
            debugSession.TranscriptionAttempts.Add(new VoiceTranscriptionAttempt
            {
                MimeType = mimeType,
                DurationMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds,
                Success = false,
                TimedOut = false,
                Error = ex.Message
            });
            return null;
        }
    }

    private async Task PersistVoiceDebugArtifactAsync(VoiceDebugSession session, byte[]? capturedAudio)
    {
        try
        {
            Directory.CreateDirectory(_voiceDebugDirectory);
            CleanupOldVoiceDebugArtifacts();

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var baseName = $"{stamp}-{suffix}";
            var metadataPath = Path.Combine(_voiceDebugDirectory, $"{baseName}.json");

            if (capturedAudio is { Length: > 0 })
            {
                var audioPath = Path.Combine(_voiceDebugDirectory, $"{baseName}.wav");
                await File.WriteAllBytesAsync(audioPath, capturedAudio);
                session.AudioPath = audioPath;
            }

            await File.WriteAllTextAsync(
                metadataPath,
                JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Voice debugging should never break the trigger flow.
        }
    }

    private void CleanupOldVoiceDebugArtifacts()
    {
        try
        {
            var directory = new DirectoryInfo(_voiceDebugDirectory);
            if (!directory.Exists)
            {
                return;
            }

            foreach (var file in directory.GetFiles()
                         .OrderByDescending(file => file.CreationTimeUtc)
                         .Skip(24))
            {
                file.Delete();
            }
        }
        catch
        {
            // Ignore best-effort cleanup failures.
        }
    }

    private static bool ParseBoolEnv(string name, bool defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" => true,
            "true" => true,
            "yes" => true,
            "on" => true,
            "0" => false,
            "false" => false,
            "no" => false,
            "off" => false,
            _ => defaultValue
        };
    }

    private static int ParseIntEnv(string name, int defaultValue, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (!int.TryParse(raw, out var parsed))
        {
            return defaultValue;
        }

        if (parsed < min)
        {
            return min;
        }

        if (parsed > max)
        {
            return max;
        }

        return parsed;
    }

    private static TimeSpan ResolveSilenceDuration()
    {
        var milliseconds = ParseIntEnv("CURSIVIS_VOICE_SILENCE_MS", defaultValue: 1100, min: 600, max: 8000);
        var legacySeconds = Environment.GetEnvironmentVariable("CURSIVIS_VOICE_SILENCE_SECONDS");
        if (int.TryParse(legacySeconds, out var seconds))
        {
            milliseconds = Math.Clamp(seconds * 1000, 600, 8000);
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private sealed class VoiceDebugSession
    {
        public DateTime StartedAtUtc { get; init; }

        public DateTime CompletedAtUtc { get; set; }

        public string? SourceName { get; set; }

        public bool LiveVoiceEnabled { get; init; }

        public bool StreamingTranscriptionEnabled { get; init; }

        public bool VoiceConfirmationRequired { get; init; }

        public int MaxVoiceDurationMs { get; init; }

        public int SilenceDurationMs { get; init; }

        public int InitialSpeechTimeoutMs { get; init; }

        public int TranscriptionTimeoutMs { get; init; }

        public int CapturedAudioBytes { get; set; }

        public int CapturedDurationMs { get; set; }

        public bool DetectedSpeech { get; set; }

        public int InputSampleRate { get; set; }

        public int InputBitsPerSample { get; set; }

        public int InputChannels { get; set; }

        public int NormalizedAudioBytes { get; set; }

        public int NormalizedSampleRate { get; set; }

        public int NormalizedBitsPerSample { get; set; }

        public int NormalizedChannels { get; set; }

        public double MaxObservedLevel { get; set; }

        public double MaxObservedRms { get; set; }

        public double MaxObservedPeak { get; set; }

        public int LevelSampleCount { get; set; }

        public int SpeechSampleCount { get; set; }

        public string? PartialTranscript { get; set; }

        public string? FinalTranscript { get; set; }

        public string? ReturnedCommand { get; set; }

        public string? ResultDisposition { get; set; }

        public string? AudioPath { get; set; }

        public List<string> Notes { get; } = [];

        public List<VoiceTranscriptionAttempt> TranscriptionAttempts { get; } = [];
    }

    private sealed class VoiceTranscriptionAttempt
    {
        public string MimeType { get; init; } = "audio/wav";

        public int DurationMs { get; init; }

        public bool Success { get; init; }

        public bool TimedOut { get; init; }

        public int TranscriptLength { get; init; }

        public string? Error { get; init; }
    }
}
