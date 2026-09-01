using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SyncPlayV2.Engine;
using MediaBrowser.Model.SyncPlay;

namespace Jellyfin.Plugin.SyncPlayV2.Api;

/// <summary>
/// The wire shapes of the external-content queue routes (plan G3.3), and the
/// translation from entries to the sentinel guid list the stock request types
/// carry. Documented for clients in plugin.video.kofin's
/// docs/syncplay-provider-contract.md once G3.4 completes the loop.
/// </summary>
public static class ExternalContent
{
    /// <summary>
    /// The most entries an Ex request may carry. A queue is a playlist, not
    /// a library dump; the stock routes carry no cap, but their entries cost
    /// one library lookup each — a descriptor entry is attacker-priced text.
    /// </summary>
    public const int MaxEntries = 200;

    /// <summary>
    /// Translates mixed entries into the guid list the stock request types
    /// carry, minting a fresh sentinel guid per descriptor entry.
    /// </summary>
    /// <param name="entries">The request's entries.</param>
    /// <param name="itemIds">The guid list, sentinels included, in order.</param>
    /// <param name="content">Sentinel guid to validated descriptor.</param>
    /// <param name="error">Why the entries were refused, or null.</param>
    /// <returns>Whether every entry was valid.</returns>
    public static bool TryBuildEntries(
        IReadOnlyList<QueueEntryDto>? entries,
        out List<Guid> itemIds,
        out Dictionary<Guid, ContentDescriptor> content,
        out string? error)
    {
        itemIds = new List<Guid>();
        content = new Dictionary<Guid, ContentDescriptor>();
        error = null;

        if (entries is null || entries.Count == 0)
        {
            error = "no entries";
            return false;
        }

        if (entries.Count > MaxEntries)
        {
            error = "too many entries";
            return false;
        }

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var hasItem = entry.ItemId.HasValue && !entry.ItemId.Value.Equals(default);
            var hasContent = entry.Content is not null;

            if (hasItem == hasContent)
            {
                error = $"entry {index}: exactly one of ItemId and Content";
                return false;
            }

            if (hasItem)
            {
                itemIds.Add(entry.ItemId!.Value);
                continue;
            }

            var dto = entry.Content!;
            if (!ContentDescriptor.TryCreate(
                    dto.Provider, dto.Key, dto.Name, dto.RunTimeTicks, dto.ImageUrl, out var descriptor))
            {
                error = $"entry {index}: invalid content descriptor";
                return false;
            }

            var sentinel = Guid.NewGuid();
            itemIds.Add(sentinel);
            content[sentinel] = descriptor!;
        }

        return true;
    }
}

/// <summary>One queue entry: a library item or an external-content descriptor.</summary>
public class QueueEntryDto
{
    /// <summary>Gets or sets the library item id, for a library entry.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>Gets or sets the content descriptor, for an external entry.</summary>
    public ContentDescriptorDto? Content { get; set; }
}

/// <summary>The wire shape of an external-content descriptor.</summary>
public class ContentDescriptorDto
{
    /// <summary>Gets or sets the provider namespace.</summary>
    public string? Provider { get; set; }

    /// <summary>Gets or sets the content key.</summary>
    public string? Key { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the runtime in ticks; 0 or absent means unknown or unbounded.</summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>Gets or sets the artwork URL.</summary>
    public string? ImageUrl { get; set; }
}

/// <summary>The SetNewQueueEx body, mirroring the stock SetNewQueue shape.</summary>
public class SetNewQueueExRequestDto
{
    /// <summary>Gets or sets the queue entries, in play order.</summary>
    public IReadOnlyList<QueueEntryDto>? PlayingQueue { get; set; }

    /// <summary>Gets or sets the index of the entry to play.</summary>
    public int PlayingItemPosition { get; set; }

    /// <summary>Gets or sets the start position in ticks.</summary>
    public long StartPositionTicks { get; set; }
}

/// <summary>The QueueEx body, mirroring the stock Queue shape.</summary>
public class QueueExRequestDto
{
    /// <summary>Gets or sets the entries to append.</summary>
    public IReadOnlyList<QueueEntryDto>? Entries { get; set; }

    /// <summary>Gets or sets where they go.</summary>
    public GroupQueueMode Mode { get; set; }
}
