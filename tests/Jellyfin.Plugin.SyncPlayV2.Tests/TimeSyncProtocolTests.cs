using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.SyncPlayV2.Ws;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

public class TimeSyncProtocolTests
{
    private static ReadOnlyMemory<byte> Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void ARequestCarriesTheClientsSendTime()
    {
        Assert.True(TimeSyncProtocol.TryReadT0(Utf8("{\"Data\":1790378743978}"), out var t0));
        Assert.Equal(1790378743978, t0);
    }

    [Theory]
    [InlineData("{\"Data\":1.5}")]
    [InlineData("{\"Data\":1e30}")]
    public void ANumberThatIsNotAnInt64IsIgnoredNotThrown(string request)
    {
        // GetInt64 throws FormatException here, which the socket loop did not
        // catch (it caught JsonException): the exception escaped into the
        // server's WebSocket middleware and the socket was torn down.
        Assert.False(TimeSyncProtocol.TryReadT0(Utf8(request), out _));
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"Data\"")]
    [InlineData("{\"Data\":\"1\"}")]
    [InlineData("{\"Other\":1}")]
    [InlineData("{\"Data\":")]
    [InlineData("not json")]
    public void AnythingElseIsIgnored(string request)
    {
        Assert.False(TimeSyncProtocol.TryReadT0(Utf8(request), out _));
    }

    [Fact]
    public void TheReplyEchoesT0WithTheServersTimes()
    {
        using var doc = JsonDocument.Parse(TimeSyncProtocol.Reply(1, 2, 3));

        Assert.Equal("TimeSync", doc.RootElement.GetProperty("MessageType").GetString());
        var data = doc.RootElement.GetProperty("Data");
        Assert.Equal(1, data.GetProperty("T0").GetInt64());
        Assert.Equal(2, data.GetProperty("T1").GetInt64());
        Assert.Equal(3, data.GetProperty("T2").GetInt64());
    }

    [Fact]
    public void AMessageSplitAcrossFramesIsReassembled()
    {
        // It used to be parsed as two half-messages, both rejected.
        var assembler = new MessageAssembler(4096);
        var request = Encoding.UTF8.GetBytes("{\"Data\":1790378743978}");

        Assert.False(assembler.Append(request.AsSpan(0, 10), false, out _));
        Assert.True(assembler.Append(request.AsSpan(10), true, out var message));

        Assert.True(TimeSyncProtocol.TryReadT0(message, out var t0));
        Assert.Equal(1790378743978, t0);
    }

    [Fact]
    public void AnOversizedMessageIsDroppedWholeAndTheNextOneIsFine()
    {
        var assembler = new MessageAssembler(16);

        Assert.False(assembler.Append(new byte[10], false, out _));
        Assert.False(assembler.Append(new byte[10], false, out _));
        Assert.False(assembler.Append(new byte[10], true, out _));

        Assert.True(assembler.Append(Encoding.UTF8.GetBytes("{\"Data\":7}"), true, out var next));
        Assert.True(TimeSyncProtocol.TryReadT0(next, out var t0));
        Assert.Equal(7, t0);
    }

    [Fact]
    public void TheLimiterCapsConnectionsPerKeyAndFreesThem()
    {
        var limiter = new ConnectionLimiter(2, int.MaxValue);

        Assert.True(limiter.TryEnter("web|device-1"));
        Assert.True(limiter.TryEnter("web|device-1"));
        Assert.False(limiter.TryEnter("web|device-1"));
        Assert.True(limiter.TryEnter("web|device-2"));

        limiter.Exit("web|device-1");
        Assert.True(limiter.TryEnter("WEB|DEVICE-1"));

        limiter.Exit("web|device-1");
        limiter.Exit("web|device-1");
        limiter.Exit("web|device-2");
        Assert.Equal(0, limiter.Count("web|device-1"));
        Assert.Equal(0, limiter.Count("web|device-2"));
    }

    [Fact]
    public void TheLimiterHoldsUnderContention()
    {
        var limiter = new ConnectionLimiter(4, int.MaxValue);

        var entered = Enumerable.Range(0, 200)
            .AsParallel()
            .Count(_ => limiter.TryEnter("web|device-1"));

        Assert.Equal(4, entered);

        Parallel.For(0, 4, _ => limiter.Exit("web|device-1"));
        Assert.Equal(0, limiter.Count("web|device-1"));
    }

    [Fact]
    public void TheLimiterCapsTheTotalOverAllKeys()
    {
        // Many tokens together are bounded too: a slot refused for the
        // total is not taken from the key either.
        var limiter = new ConnectionLimiter(4, 3);

        Assert.True(limiter.TryEnter("a"));
        Assert.True(limiter.TryEnter("b"));
        Assert.True(limiter.TryEnter("c"));
        Assert.False(limiter.TryEnter("d"));
        Assert.Equal(0, limiter.Count("d"));

        limiter.Exit("b");
        Assert.True(limiter.TryEnter("d"));
        Assert.Equal(3, limiter.Total);
    }

    [Fact]
    public async Task AKeyAtItsCapDoesNotCrowdOutAnotherKey()
    {
        // One slot left in total. Attempts on a key already at its cap, however
        // many and however concurrent, never take it from another key.
        var limiter = new ConnectionLimiter(1, 2);
        Assert.True(limiter.TryEnter("capped"));

        using var stop = new System.Threading.CancellationTokenSource();
        var spam = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                Assert.False(limiter.TryEnter("capped"));
            }
        })).ToList();

        try
        {
            for (var i = 0; i < 20000; i++)
            {
                Assert.True(limiter.TryEnter("other"), "refused on attempt " + i);
                limiter.Exit("other");
            }
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(spam);
        }

        Assert.Equal(1, limiter.Total);
    }
}
