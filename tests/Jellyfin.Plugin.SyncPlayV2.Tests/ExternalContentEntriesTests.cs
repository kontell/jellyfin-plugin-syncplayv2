using System;
using System.Linq;
using Jellyfin.Plugin.SyncPlayV2.Api;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

public class ExternalContentEntriesTests
{
    private static QueueEntryDto Item(Guid id) => new QueueEntryDto { ItemId = id };

    private static QueueEntryDto Content(string provider = "youtube", string key = "vid-1")
        => new QueueEntryDto { Content = new ContentDescriptorDto { Provider = provider, Key = key } };

    [Fact]
    public void AMixedQueueTranslatesInOrderWithFreshSentinels()
    {
        var library = Guid.NewGuid();

        var ok = ExternalContent.TryBuildEntries(
            new[] { Item(library), Content(), Content(key: "vid-2") },
            out var itemIds,
            out var content,
            out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(3, itemIds.Count);
        Assert.Equal(library, itemIds[0]);
        Assert.Equal(2, content.Count);
        Assert.True(content.ContainsKey(itemIds[1]));
        Assert.True(content.ContainsKey(itemIds[2]));
        Assert.NotEqual(itemIds[1], itemIds[2]);
        Assert.Equal("vid-2", content[itemIds[2]].Key);
    }

    [Fact]
    public void AnEntryMustBeExactlyOneOfItemAndContent()
    {
        Assert.False(ExternalContent.TryBuildEntries(
            new[] { new QueueEntryDto() }, out _, out _, out var neither));
        Assert.Contains("exactly one", neither);

        var both = Item(Guid.NewGuid());
        both.Content = new ContentDescriptorDto { Provider = "youtube", Key = "k" };
        Assert.False(ExternalContent.TryBuildEntries(new[] { both }, out _, out _, out _));
    }

    [Fact]
    public void AnEmptyItemGuidIsNotALibraryEntry()
    {
        Assert.False(ExternalContent.TryBuildEntries(
            new[] { Item(Guid.Empty) }, out _, out _, out _));
    }

    [Fact]
    public void AnInvalidDescriptorNamesItsEntry()
    {
        var ok = ExternalContent.TryBuildEntries(
            new[] { Content(), Content(provider: "BAD NAME") }, out _, out _, out var error);

        Assert.False(ok);
        Assert.Contains("entry 1", error);
    }

    [Fact]
    public void EmptyAndOversizeRequestsAreRefused()
    {
        Assert.False(ExternalContent.TryBuildEntries(null, out _, out _, out _));
        Assert.False(ExternalContent.TryBuildEntries(Array.Empty<QueueEntryDto>(), out _, out _, out _));
        Assert.False(ExternalContent.TryBuildEntries(
            Enumerable.Repeat(Content(), ExternalContent.MaxEntries + 1).ToList(), out _, out _, out _));
    }
}
