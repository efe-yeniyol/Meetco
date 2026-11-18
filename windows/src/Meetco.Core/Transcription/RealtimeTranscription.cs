using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Meetco.Core.Audio;
using Meetco.Core.Infrastructure;
using Meetco.Core.Models;
using Meetco.Core.OpenAI;
using Meetco.Core.Settings;

namespace Meetco.Core.Transcription;

/// <summary>
/// One live transcription connection for one audio source.
///
/// Audio is streamed while the person is still speaking, so when Meetco detects the end of
/// the utterance the model has already heard it: the transcript comes back right after the
/// commit instead of after a file upload. Every failure is reported per utterance, and the
/// caller falls back to file transcription for that utterance.
/// </summary>
public sealed class RealtimeTranscriptionSession : ILiveAudioSink
{
    private sealed record LiveResult(string? Text, LiveTranscriptionException? Error);

    private readonly AudioSourceKind _source;
    private readonly string _model;
    private readonly string? _languageCode;
    private readonly ApiKeyProvider _keyProvider;
    private readonly Func<OpenAIConfiguration> _configuration;
    private readonly Channel<LiveAudioEvent> _events = Channel.CreateUnbounded<LiveAudioEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _gate = new();

    private ClientWebSocket? _socket;
    /// <summary>Identifies the current connection so late callbacks from an old one are ignored.</summary>
    private int _generation;
    private bool _isReady;
    private bool _isClosed;
    /// <summary>Set after too many errors: every utterance then goes straight to file transcription.</summary>
    private bool _isDisabled;
    /// <summary>True while the utterance in progress was only partly sent and must not be committed.</summary>
    private bool _skipUntilBoundary;
    private bool _hasUncommittedAudio;
    private LinearUpsampler _upsampler = new();

    /// <summary>Chunks whose commit was sent, in order, waiting for the server to name their item.</summary>
    private readonly Queue<Guid> _awaitingItemId = new();
    private readonly Dictionary<string, Guid> _chunkForItem = [];
    private readonly Dictionary<Guid, LiveResult> _results = [];
    private readonly Dictionary<Guid, TaskCompletionSource<string>> _waiters = [];
    private readonly HashSet<Guid> _abandoned = [];
    private int _failuresWithoutSuccess;

    public RealtimeTranscriptionSession(AudioSourceKind source, string model, string? languageCode, ApiKeyProvider keyProvider, Func<OpenAIConfiguration> configuration)
    {
        _source = source;
        _model = model;
        _languageCode = languageCode;
        _keyProvider = keyProvider;
        _configuration = configuration;
        _ = Task.Run(RunAsync);
    }

    public void Send(LiveAudioEvent audioEvent) => _events.Writer.TryWrite(audioEvent);

    public void Close() => _events.Writer.TryComplete();

    /// <summary>The transcript of a committed utterance. Throws when the live stream could not provide it.</summary>
    public async Task<string> TranscriptAsync(Guid chunkId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        TaskCompletionSource<string> waiter;
        lock (_gate)
        {
            if (_results.Remove(chunkId, out var result))
            {
                return result.Error is null ? result.Text ?? "" : throw result.Error;
            }
            if (_isClosed || _isDisabled) throw new LiveTranscriptionException(LiveTranscriptionFailure.Unavailable);
            waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters[chunkId] = waiter;
        }
        _ = Task.Delay(timeout, CancellationToken.None).ContinueWith(_ => Expire(chunkId), TaskScheduler.Default);
        using (cancellationToken.Register(() => Expire(chunkId)))
        {
            return await waiter.Task.ConfigureAwait(false);
        }
    }

    // MARK: Lifecycle

    private async Task RunAsync()
    {
        try
        {
            await ConnectAsync().ConfigureAwait(false);
            await foreach (var audioEvent in _events.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await HandleAsync(audioEvent).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            Log.Error("transcription", "Live transcription loop failed: " + error.Message);
        }
        finally
        {
            Shutdown();
        }
    }

    private void Shutdown()
    {
        lock (_gate)
        {
            _isClosed = true;
            Disconnect();
            FailEverything();
        }
        _lifetime.Cancel();
    }

    // MARK: Client events

    private async Task HandleAsync(LiveAudioEvent audioEvent)
    {
        switch (audioEvent)
        {
            case LiveAudioEvent.Audio audio:
            {
                ClientWebSocket socket;
                int connection;
                string payload;
                lock (_gate)
                {
                    if (!_isReady || _skipUntilBoundary || _socket is null)
                    {
                        _skipUntilBoundary = true;
                        return;
                    }
                    socket = _socket;
                    connection = _generation;
                    payload = RealtimeWire.Append(_upsampler.Process(audio.Samples));
                }
                try
                {
                    await SendAsync(socket, payload).ConfigureAwait(false);
                    lock (_gate) _hasUncommittedAudio = true;
                }
                catch (Exception error)
                {
                    ConnectionLost(connection, error.Message);
                }
                break;
            }
            case LiveAudioEvent.Commit commit:
            {
                ClientWebSocket socket;
                int connection;
                lock (_gate)
                {
                    if (!_isReady || _skipUntilBoundary || !_hasUncommittedAudio || _socket is null)
                    {
                        Resolve(commit.ChunkId, new LiveResult(null, new LiveTranscriptionException(LiveTranscriptionFailure.Unavailable)));
                        EndUtterance();
                        return;
                    }
                    socket = _socket;
                    connection = _generation;
                    _awaitingItemId.Enqueue(commit.ChunkId);
                }
                try
                {
                    await SendAsync(socket, RealtimeWire.Commit).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    ConnectionLost(connection, error.Message);
                }
                finally
                {
                    lock (_gate) EndUtterance();
                }
                break;
            }
            case LiveAudioEvent.Discard:
            {
                ClientWebSocket? socket;
                lock (_gate)
                {
                    socket = _isReady && _hasUncommittedAudio ? _socket : null;
                    if (socket is null)
                    {
                        EndUtterance();
                        return;
                    }
                }
                try
                {
                    await SendAsync(socket, RealtimeWire.Clear).ConfigureAwait(false);
                }
                catch
                {
                    // Clearing is best effort; a lost connection is noticed by the receive loop.
                }
                finally
                {
                    lock (_gate) EndUtterance();
                }
                break;
            }
        }
    }

    private void EndUtterance()
    {
        _skipUntilBoundary = false;
        _hasUncommittedAudio = false;
        _upsampler = new LinearUpsampler();
    }

    // MARK: Server events

    private void HandleServer(string text, int connection)
    {
        lock (_gate)
        {
            if (connection != _generation) return;
            switch (RealtimeWire.Parse(text))
            {
                case RealtimeWire.ServerEvent.Committed committed:
                    if (_awaitingItemId.Count == 0) return;
                    _chunkForItem[committed.ItemId] = _awaitingItemId.Dequeue();
                    break;
                case RealtimeWire.ServerEvent.Completed completed:
                    if (!_chunkForItem.Remove(completed.ItemId, out var completedChunk)) return;
                    _failuresWithoutSuccess = 0;
                    Resolve(completedChunk, new LiveResult(completed.Transcript, null));
                    break;
                case RealtimeWire.ServerEvent.Failed failed:
                    Log.Error("transcription", "Live transcription failed: " + failed.Message);
                    if (!_chunkForItem.Remove(failed.ItemId, out var failedChunk)) return;
                    Resolve(failedChunk, new LiveResult(null, new LiveTranscriptionException(LiveTranscriptionFailure.Failed, failed.Message)));
                    NoteFailure();
                    break;
                case RealtimeWire.ServerEvent.Error error:
                    Log.Error("transcription", "Live transcription error: " + error.Message);
                    if (_awaitingItemId.Count > 0)
                    {
                        Resolve(_awaitingItemId.Dequeue(), new LiveResult(null, new LiveTranscriptionException(LiveTranscriptionFailure.Failed, error.Message)));
                    }
                    NoteFailure();
                    break;
            }
        }
    }

    private void NoteFailure()
    {
        _failuresWithoutSuccess += 1;
        if (_failuresWithoutSuccess < 4 || _isDisabled) return;
        // The account or endpoint does not support live transcription: stop trying.
        Log.Notice("transcription", "Live transcription disabled for this session; using file transcription");
        _isDisabled = true;
        Disconnect();
        FailEverything();
    }

    // MARK: Connection

    private async Task ConnectAsync()
    {
        lock (_gate)
        {
            if (_isClosed || _isDisabled || _socket is not null) return;
        }
        var key = _keyProvider.CurrentKey();
        if (string.IsNullOrEmpty(key)) return;
        var url = RealtimeWire.Url(_configuration().BaseUrl);

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + key);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        int connection;
        lock (_gate)
        {
            _generation += 1;
            connection = _generation;
            _socket = socket;
        }

        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                await socket.ConnectAsync(url, timeout.Token).ConfigureAwait(false);
            }
            _ = Task.Run(() => ReceiveLoopAsync(socket, connection));
            await SendAsync(socket, RealtimeWire.SessionUpdate(_model, _languageCode)).ConfigureAwait(false);
            lock (_gate)
            {
                if (connection != _generation || _isClosed) return;
                _isReady = true;
            }
            Log.Info("transcription", $"Live transcription connected ({_source})");
        }
        catch (Exception error)
        {
            ConnectionLost(connection, error.Message);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, int connection)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, _lifetime.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    ConnectionLost(connection, result.CloseStatusDescription ?? "closed by server");
                    return;
                }
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                HandleServer(text, connection);
            }
        }
        catch (Exception error)
        {
            ConnectionLost(connection, error.Message);
        }
    }

    private async Task SendAsync(ClientWebSocket socket, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, _lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void ConnectionLost(int connection, string reason)
    {
        TimeSpan delay;
        lock (_gate)
        {
            if (connection != _generation || _socket is null) return;
            Log.Notice("transcription", "Live transcription connection lost: " + reason);
            Disconnect();
            FailPending();
            // An utterance that was being streamed is incomplete on the next connection.
            _skipUntilBoundary = true;
            if (_isClosed || _isDisabled) return;
            _failuresWithoutSuccess += 1;
            if (_failuresWithoutSuccess >= 6)
            {
                _isDisabled = true;
                FailEverything();
                return;
            }
            delay = TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, _failuresWithoutSuccess - 1) * 0.5));
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
                await ConnectAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    /// <summary>Must be called under the lock.</summary>
    private void Disconnect()
    {
        var socket = _socket;
        _socket = null;
        _isReady = false;
        _hasUncommittedAudio = false;
        if (socket is null) return;
        try
        {
            socket.Abort();
            socket.Dispose();
        }
        catch
        {
            // Already closed.
        }
    }

    // MARK: Results (all called under the lock)

    private void Resolve(Guid chunkId, LiveResult result)
    {
        if (_abandoned.Remove(chunkId)) return;
        if (_waiters.Remove(chunkId, out var waiter))
        {
            if (result.Error is null) waiter.TrySetResult(result.Text ?? "");
            else waiter.TrySetException(result.Error);
        }
        else
        {
            _results[chunkId] = result;
        }
    }

    private void Expire(Guid chunkId)
    {
        lock (_gate)
        {
            if (!_waiters.Remove(chunkId, out var waiter)) return;
            _abandoned.Add(chunkId);
            waiter.TrySetException(new LiveTranscriptionException(LiveTranscriptionFailure.TimedOut));
        }
    }

    /// <summary>Fails utterances that were committed on a connection that no longer exists.</summary>
    private void FailPending()
    {
        var pending = _awaitingItemId.Concat(_chunkForItem.Values).ToList();
        _awaitingItemId.Clear();
        _chunkForItem.Clear();
        foreach (var chunkId in pending)
        {
            Resolve(chunkId, new LiveResult(null, new LiveTranscriptionException(LiveTranscriptionFailure.Unavailable)));
        }
    }

    private void FailEverything()
    {
        FailPending();
        var waiting = _waiters.Values.ToList();
        _waiters.Clear();
        foreach (var waiter in waiting) waiter.TrySetException(new LiveTranscriptionException(LiveTranscriptionFailure.Unavailable));
    }
}

/// <summary>Owns the live transcription connections of the current listening session (one per source).</summary>
public sealed class LiveTranscriptionHub(ApiKeyProvider keyProvider, Func<OpenAIConfiguration> configuration)
{
    private readonly object _gate = new();
    private readonly Dictionary<AudioSourceKind, RealtimeTranscriptionSession> _sessions = [];

    public ILiveAudioSink Open(AudioSourceKind source, string model, string? languageCode)
    {
        var session = new RealtimeTranscriptionSession(source, model, languageCode, keyProvider, configuration);
        RealtimeTranscriptionSession? previous;
        lock (_gate)
        {
            _sessions.TryGetValue(source, out previous);
            _sessions[source] = session;
        }
        previous?.Close();
        return session;
    }

    public RealtimeTranscriptionSession? Session(AudioSourceKind source)
    {
        lock (_gate) return _sessions.GetValueOrDefault(source);
    }

    public void CloseAll()
    {
        List<RealtimeTranscriptionSession> closing;
        lock (_gate)
        {
            closing = [.. _sessions.Values];
            _sessions.Clear();
        }
        foreach (var session in closing) session.Close();
    }
}

/// <summary>Transcribes with the live stream when it has the utterance, and with a file upload otherwise.</summary>
public sealed class LiveFirstTranscriptionService(LiveTranscriptionHub hub, ITranscriptionService file) : ITranscriptionService
{
    /// <summary>How long to wait for the live transcript after the utterance ended.</summary>
    public TimeSpan LiveTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public async Task<TranscriptionResult> TranscribeAsync(AudioChunk chunk, TranscriptionOptions options, CancellationToken cancellationToken)
    {
        if (ModelCatalog.IsLiveTranscriptionModel(options.Model) && hub.Session(chunk.Source) is { } session)
        {
            try
            {
                var text = await session.TranscriptAsync(chunk.Id, LiveTimeout, cancellationToken).ConfigureAwait(false);
                return new TranscriptionResult(text, null);
            }
            catch (LiveTranscriptionException error)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Log.Notice("transcription", $"Live transcript unavailable ({error.Failure}); uploading the audio instead");
            }
        }
        var fileOptions = options with { Model = ModelCatalog.FileTranscriptionModel(options.Model) };
        return await file.TranscribeAsync(chunk, fileOptions, cancellationToken).ConfigureAwait(false);
    }
}
