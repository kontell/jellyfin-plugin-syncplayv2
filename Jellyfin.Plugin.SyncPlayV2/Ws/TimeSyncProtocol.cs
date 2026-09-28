using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.SyncPlayV2.Ws;

/// <summary>
/// The time-sync socket's message handling, apart from the socket so it can
/// be tested: a request is <c>{"Data": t0}</c>, the reply echoes t0 with the
/// server's receive and send times (spec §3, NTP-style).
/// </summary>
public static class TimeSyncProtocol
{
    /// <summary>
    /// The largest request accepted. A request is a few dozen bytes; anything
    /// near this is not a time-sync request.
    /// </summary>
    public const int MaxMessageBytes = 4096;

    /// <summary>Reads the client's send time from a request.</summary>
    /// <param name="message">The complete request.</param>
    /// <param name="t0">The client's send time.</param>
    /// <returns>Whether the request was a time-sync request.</returns>
    public static bool TryReadT0(ReadOnlyMemory<byte> message, out long t0)
    {
        t0 = 0;
        try
        {
            using var doc = JsonDocument.Parse(message);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("Data", out var data)
                || data.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            return data.TryGetInt64(out t0);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The reply to a request.</summary>
    /// <param name="t0">The client's send time.</param>
    /// <param name="t1">The server's receive time.</param>
    /// <param name="t2">The server's send time.</param>
    /// <returns>The reply, UTF-8 JSON.</returns>
    public static byte[] Reply(long t0, long t1, long t2)
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            MessageType = "TimeSync",
            Data = new { T0 = t0, T1 = t1, T2 = t2 },
        });
}

/// <summary>
/// Reassembles a WebSocket message from its frames. A receive returns at
/// most one buffer's worth and says whether the message ended; a message
/// that did not fit used to be parsed as two half-messages, both rejected.
/// Messages over the size limit are dropped whole, however many frames they
/// span, so a client cannot make the server buffer without bound.
/// </summary>
public sealed class MessageAssembler
{
    private readonly int _maxBytes;
    private readonly MemoryStream _buffer = new();
    private bool _overflowed;

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageAssembler"/> class.
    /// </summary>
    /// <param name="maxBytes">The largest message kept.</param>
    public MessageAssembler(int maxBytes)
    {
        _maxBytes = maxBytes;
    }

    /// <summary>Adds a received frame.</summary>
    /// <param name="chunk">The frame's bytes.</param>
    /// <param name="endOfMessage">Whether the frame ends the message.</param>
    /// <param name="message">The complete message, when one is ready.</param>
    /// <returns>Whether a complete message within the limit is ready.</returns>
    public bool Append(ReadOnlySpan<byte> chunk, bool endOfMessage, out ReadOnlyMemory<byte> message)
    {
        message = ReadOnlyMemory<byte>.Empty;

        if (!_overflowed)
        {
            if (_buffer.Length + chunk.Length > _maxBytes)
            {
                _overflowed = true;
                _buffer.SetLength(0);
            }
            else
            {
                _buffer.Write(chunk);
            }
        }

        if (!endOfMessage)
        {
            return false;
        }

        var complete = !_overflowed;
        if (complete)
        {
            message = _buffer.ToArray();
        }

        _buffer.SetLength(0);
        _overflowed = false;
        return complete;
    }
}

/// <summary>
/// Caps concurrent connections per key. A client needs one time-sync socket;
/// a few allow a reconnect to overlap the socket it replaces. A client that
/// opens them in a loop without closing any is refused instead of holding a
/// server connection each until the idle timeout reaps it.
/// </summary>
public sealed class ConnectionLimiter
{
    private readonly int _perKey;
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionLimiter"/> class.
    /// </summary>
    /// <param name="perKey">The most concurrent connections per key.</param>
    public ConnectionLimiter(int perKey)
    {
        _perKey = perKey;
    }

    /// <summary>Takes a slot for the key, if one is free.</summary>
    /// <param name="key">The key (client and device).</param>
    /// <returns>Whether a slot was taken; each true must be matched by <see cref="Exit"/>.</returns>
    public bool TryEnter(string key)
    {
        while (true)
        {
            var current = _counts.GetOrAdd(key, 0);
            if (current >= _perKey)
            {
                return false;
            }

            if (_counts.TryUpdate(key, current + 1, current))
            {
                return true;
            }
        }
    }

    /// <summary>Releases a slot taken by <see cref="TryEnter"/>.</summary>
    /// <param name="key">The key.</param>
    public void Exit(string key)
    {
        while (_counts.TryGetValue(key, out var current))
        {
            if (current <= 1)
            {
                if (_counts.TryRemove(new System.Collections.Generic.KeyValuePair<string, int>(key, current)))
                {
                    return;
                }
            }
            else if (_counts.TryUpdate(key, current - 1, current))
            {
                return;
            }
        }
    }

    /// <summary>The connections currently held for a key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The count.</returns>
    public int Count(string key) => _counts.TryGetValue(key, out var count) ? count : 0;
}
