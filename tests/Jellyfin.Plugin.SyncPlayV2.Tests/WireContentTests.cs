using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.SyncPlayV2.Engine;
using Jellyfin.Plugin.SyncPlayV2.Wire;
using MediaBrowser.Model.SyncPlay;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

public class WireContentTests
{
    private static ContentDescriptor Descriptor(string key)
    {
        Assert.True(ContentDescriptor.TryCreate("youtube", key, "A Name", 123, null, out var descriptor));
        return descriptor!;
    }

    private static (PlayQueueUpdate Update, Guid Sentinel, ContentTable Table) MixedQueue()
    {
        var library = new SyncPlayQueueItem(Guid.NewGuid());
        var external = new SyncPlayQueueItem(Guid.NewGuid());
        var table = new ContentTable();
        table.Register(new Dictionary<Guid, ContentDescriptor> { [external.ItemId] = Descriptor("vid-1") });

        var update = new PlayQueueUpdate(
            PlayQueueUpdateReason.NewPlaylist,
            DateTime.UtcNow,
            new[] { library, external },
            1,
            42,
            true,
            GroupShuffleMode.Sorted,
            GroupRepeatMode.RepeatNone);
        return (update, external.ItemId, table);
    }

    [Fact]
    public void EnrichmentAttachesContentOnlyWhereTheTableHoldsIt()
    {
        var (update, sentinel, table) = MixedQueue();

        var wire = WirePlayQueueUpdate.From(update, table);

        Assert.Equal(2, wire.Playlist.Count);
        Assert.Null(wire.Playlist[0].Content);
        Assert.NotNull(wire.Playlist[1].Content);
        Assert.Equal(sentinel, wire.Playlist[1].ItemId);
        Assert.Equal("vid-1", wire.Playlist[1].Content!.Key);
        Assert.Equal(update.Playlist[1].PlaylistItemId, wire.Playlist[1].PlaylistItemId);
        Assert.Equal(update.PlayingItemIndex, wire.PlayingItemIndex);
        Assert.Equal(update.StartPositionTicks, wire.StartPositionTicks);
        Assert.Equal(update.IsPlaying, wire.IsPlaying);
    }

    [Fact]
    public void AnEntryWithoutContentSerializesInTheStockShape()
    {
        // The enriched DTO must be JSON byte-equivalent to stock for library
        // entries, or a capability member's parser sees a shape nobody
        // documented: Content is omitted, never null.
        var (update, _, table) = MixedQueue();

        var json = JsonSerializer.Serialize(WirePlayQueueUpdate.From(update, table));
        using var parsed = JsonDocument.Parse(json);
        var playlist = parsed.RootElement.GetProperty("Playlist");

        Assert.False(playlist[0].TryGetProperty("Content", out _));
        Assert.True(playlist[1].TryGetProperty("Content", out var content));
        Assert.Equal("youtube", content.GetProperty("Provider").GetString());
    }
}
