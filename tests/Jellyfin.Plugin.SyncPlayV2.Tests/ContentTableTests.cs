using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SyncPlayV2.Engine;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

public class ContentTableTests
{
    private static ContentDescriptor Descriptor(long runtime = 0)
    {
        Assert.True(ContentDescriptor.TryCreate("test", "key-1", "Name", runtime, null, out var descriptor));
        return descriptor!;
    }

    [Fact]
    public void ARegisteredRuntimeOfZeroIsARealAnswerNotAMiss()
    {
        // The whole point of the runtime sourcing: a live entry's 0 must
        // reach Positions.Sanitize as 0 (unbounded), never fall through to
        // a library lookup for a sentinel the library has never heard of.
        var table = new ContentTable();
        var sentinel = Guid.NewGuid();
        table.Register(new Dictionary<Guid, ContentDescriptor> { [sentinel] = Descriptor(runtime: 0) });

        Assert.Equal(0L, table.RuntimeOf(sentinel));
        Assert.Null(table.RuntimeOf(Guid.NewGuid()));
    }

    [Fact]
    public void RegistrationReplacesAndContainsAnswers()
    {
        var table = new ContentTable();
        var sentinel = Guid.NewGuid();
        table.Register(new Dictionary<Guid, ContentDescriptor> { [sentinel] = Descriptor(runtime: 10) });
        table.Register(new Dictionary<Guid, ContentDescriptor> { [sentinel] = Descriptor(runtime: 20) });

        Assert.True(table.Contains(sentinel));
        Assert.Equal(20L, table.RuntimeOf(sentinel));
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void PruneDropsWhatTheQueueNoLongerHolds()
    {
        var table = new ContentTable();
        var kept = Guid.NewGuid();
        var dropped = Guid.NewGuid();
        table.Register(new Dictionary<Guid, ContentDescriptor>
        {
            [kept] = Descriptor(),
            [dropped] = Descriptor(),
        });

        table.PruneTo(new[] { kept, Guid.NewGuid() });

        Assert.True(table.Contains(kept));
        Assert.False(table.Contains(dropped));
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void PruneToAnEmptyQueueEmptiesTheTable()
    {
        var table = new ContentTable();
        table.Register(new Dictionary<Guid, ContentDescriptor> { [Guid.NewGuid()] = Descriptor() });

        table.PruneTo(Array.Empty<Guid>());

        Assert.Equal(0, table.Count);
    }
}
