using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Meetco.Core.Audio;
using Meetco.Core.Infrastructure;
using Meetco.Core.OpenAI;
using Whisper.net;

namespace Meetco.Core.Models
{
    /// <summary>Who turns speech into text.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<TranscriptionProvider>))]
    public enum TranscriptionProvider
    {
        /// <summary>OpenAI's speech-to-text models (live streaming or file uploads).</summary>
        OpenAI,
        /// <summary>Whisper running on this PC (whisper.cpp). Audio never leaves the computer.</summary>
        OnDevice,
    }

    [JsonConverter(typeof(JsonStringEnumConverter<WhisperModelSize>))]
    public enum WhisperModelSize { Base, Small, LargeTurbo }

    /// <summary>A downloadable Whisper model (ggml format, from the whisper.cpp project on Hugging Face).</summary>
    public sealed record WhisperModelInfo(WhisperModelSize Size, string FileName, long Bytes, string Sha256, string Title, string Note)
    {
        public Uri DownloadUrl => new("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + FileName);
        public string SizeText => $"{Bytes / 1_000_000} MB";

        public static readonly IReadOnlyList<WhisperModelInfo> All =
        [
            new(WhisperModelSize.Base, "ggml-base.bin", 147_951_465,
                "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
                "Fast", "Quickest on any PC; good for English, weaker for other languages"),
            new(WhisperModelSize.Small, "ggml-small-q5_1.bin", 190_085_487,
                "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb",
                "Balanced", "Good accuracy in most languages, still fast (recommended)"),
            new(WhisperModelSize.LargeTurbo, "ggml-large-v3-turbo-q5_0.bin", 574_041_195,
                "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2",
                "Accurate", "Best accuracy; needs a fast CPU to keep up with live speech"),
        ];

        public static WhisperModelInfo For(WhisperModelSize size) => All.First(m => m.Size == size);
    }
}

namespace Meetco.Core.Transcription
{
    using Meetco.Core.Models;

    /// <summary>Downloads and verifies Whisper models in a local folder (%LOCALAPPDATA%\Meetco\models in the app).</summary>
    public sealed class WhisperModelStore(string directory, HttpClient? http = null)
    {
        private readonly HttpClient _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        public string Directory { get; } = directory;

        public string PathFor(WhisperModelSize size) => Path.Combine(Directory, WhisperModelInfo.For(size).FileName);

        /// <summary>Installed means present with the expected size (the hash is checked when downloading).</summary>
        public bool IsInstalled(WhisperModelSize size)
        {
            var file = new FileInfo(PathFor(size));
            return file.Exists && file.Length == WhisperModelInfo.For(size).Bytes;
        }

        /// <summary>Downloads the model to a temporary file, verifies its SHA-256 and moves it into place.</summary>
        public async Task DownloadAsync(WhisperModelSize size, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            if (IsInstalled(size)) return;
            var model = WhisperModelInfo.For(size);
            System.IO.Directory.CreateDirectory(Directory);
            var temporary = PathFor(size) + ".download";
            Log.Info("transcription", $"Downloading Whisper model {model.FileName}");
            try
            {
                using var response = await _http.GetAsync(model.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new IOException($"Model download failed (HTTP {(int)response.StatusCode}).");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var target = File.Create(temporary))
                {
                    var buffer = new byte[1 << 20];
                    long received = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, read);
                        received += read;
                        progress?.Report(Math.Min(1, (double)received / model.Bytes));
                    }
                }
                var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (!string.Equals(digest, model.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The downloaded model is damaged (checksum mismatch). Try again.");
                }
                File.Move(temporary, PathFor(size), overwrite: true);
                Log.Info("transcription", $"Whisper model {model.FileName} installed");
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public void Delete(WhisperModelSize size)
        {
            if (File.Exists(PathFor(size))) File.Delete(PathFor(size));
        }
    }

    public sealed class OnDeviceTranscriptionException(string message) : Exception(message);

    /// <summary>
    /// Transcribes each utterance with Whisper on this PC. The model is loaded once and kept in memory;
    /// utterances are processed one at a time because each already uses several CPU cores.
    /// </summary>
    public sealed class WhisperTranscriptionService(WhisperModelStore store, Func<WhisperModelSize> modelSize) : ITranscriptionService, IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private WhisperFactory? _factory;
        private WhisperModelSize? _loadedSize;

        public async Task<TranscriptionResult> TranscribeAsync(AudioChunk chunk, TranscriptionOptions options, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var factory = Load(modelSize());
                var builder = factory.CreateBuilder()
                    .WithLanguage(options.LanguageCode ?? "auto")
                    .WithThreads(Math.Clamp(Environment.ProcessorCount - 1, 1, 8))
                    .WithNoContext()
                    .WithSingleSegment();
                if (!string.IsNullOrWhiteSpace(options.Prompt)) builder = builder.WithPrompt(options.Prompt);
                await using var processor = builder.Build();

                var text = new StringBuilder();
                string? language = null;
                await foreach (var segment in processor.ProcessAsync(chunk.Samples, cancellationToken).ConfigureAwait(false))
                {
                    text.Append(segment.Text);
                    language ??= segment.Language;
                }
                return new TranscriptionResult(text.ToString().Trim(), string.IsNullOrEmpty(language) || language == "auto" ? null : language);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Loads the model ahead of the first utterance (it takes a moment). Throws if it isn't downloaded.</summary>
        public async Task PrepareAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Load(modelSize());
            }
            finally
            {
                _gate.Release();
            }
        }

        private WhisperFactory Load(WhisperModelSize size)
        {
            if (_factory is not null && _loadedSize == size) return _factory;
            if (!store.IsInstalled(size))
            {
                throw new OnDeviceTranscriptionException(
                    $"The {WhisperModelInfo.For(size).Title} speech model is not downloaded yet. Download it in Settings → Audio, or choose OpenAI transcription.");
            }
            _factory?.Dispose();
            _factory = WhisperFactory.FromPath(store.PathFor(size));
            _loadedSize = size;
            return _factory;
        }

        public void Dispose()
        {
            _factory?.Dispose();
            _gate.Dispose();
        }
    }

    /// <summary>Sends each utterance to the transcription provider chosen in Settings.</summary>
    public sealed class TranscriptionRouter(ITranscriptionService openAI, ITranscriptionService onDevice) : ITranscriptionService
    {
        public Task<TranscriptionResult> TranscribeAsync(AudioChunk chunk, TranscriptionOptions options, CancellationToken cancellationToken) =>
            options.Provider == TranscriptionProvider.OnDevice
                ? onDevice.TranscribeAsync(chunk, options, cancellationToken)
                : openAI.TranscribeAsync(chunk, options, cancellationToken);
    }
}
