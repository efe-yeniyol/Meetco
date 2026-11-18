using Meetco.Core.Audio;
using Meetco.Core.OpenAI;
using Meetco.Core.Text;

namespace Meetco.Core.Transcription;

/// <summary>Converts captured audio chunks into text.</summary>
public interface ITranscriptionService
{
    Task<TranscriptionResult> TranscribeAsync(AudioChunk chunk, TranscriptionOptions options, CancellationToken cancellationToken);
}

/// <summary>Sends audio chunks to OpenAI's /audio/transcriptions endpoint.</summary>
public sealed class OpenAITranscriptionService(IOpenAIClient client) : ITranscriptionService
{
    public async Task<TranscriptionResult> TranscribeAsync(AudioChunk chunk, TranscriptionOptions options, CancellationToken cancellationToken)
    {
        var wav = WavEncoder.Encode(chunk.Samples, chunk.SampleRate);
        // Only whisper-1 supports verbose_json (which reports the detected language).
        var verbose = options.Model.StartsWith("whisper", StringComparison.OrdinalIgnoreCase);
        var filename = $"chunk-{chunk.Sequence}.wav";
        TranscriptionResponse response;
        try
        {
            response = await client.CreateTranscriptionAsync(wav, filename, options, verbose, cancellationToken).ConfigureAwait(false);
        }
        catch (OpenAIException error) when (error.Kind == OpenAIErrorKind.Http && error.Status == 400 && options.LanguageCode is not null)
        {
            // The model rejected the language hint: let it detect the language itself.
            response = await client.CreateTranscriptionAsync(wav, filename, options with { LanguageCode = null }, verbose, cancellationToken).ConfigureAwait(false);
        }
        var languageCode = (response.Language is { } name ? LanguageDetector.CodeForLanguageName(name) : null)
            ?? (response.Languages?.FirstOrDefault()?.Code is { Length: >= 2 } code ? code[..2].ToLowerInvariant() : null);
        return new TranscriptionResult(response.Text, languageCode);
    }
}
