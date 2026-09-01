using System;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SyncPlayV2.Engine
{
    /// <summary>
    /// An external-content queue entry: content the server never resolves,
    /// coordinated for clients that resolve it themselves (plan G3.3 in
    /// plugin.video.kofin's docs/syncplay-generic-backend-plan.md).
    ///
    /// <c>Provider:Key</c> is the identity — an opaque namespace and an
    /// opaque key inside it, e.g. <c>youtube:dQw4w9WgXcQ</c>. The server
    /// carries the descriptor and its runtime and validates nothing beyond
    /// shape: resolvability is the members' problem by design.
    /// </summary>
    public sealed class ContentDescriptor
    {
        /// <summary>
        /// The provider-name shape, matching the client contract
        /// (plugin.video.kofin docs/syncplay-provider-contract.md §3).
        /// </summary>
        private static readonly Regex ProviderName = new Regex(
            "^[a-z0-9][a-z0-9._-]{1,39}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private ContentDescriptor(string provider, string key, string name, long runTimeTicks, string imageUrl)
        {
            Provider = provider;
            Key = key;
            Name = name;
            RunTimeTicks = runTimeTicks;
            ImageUrl = imageUrl;
        }

        /// <summary>Gets the provider namespace.</summary>
        public string Provider { get; }

        /// <summary>Gets the content key inside the provider's namespace.</summary>
        public string Key { get; }

        /// <summary>Gets the display name, possibly empty.</summary>
        public string Name { get; }

        /// <summary>Gets the runtime in ticks; 0 means unknown or unbounded (live).</summary>
        public long RunTimeTicks { get; }

        /// <summary>Gets the artwork URL, possibly empty.</summary>
        public string ImageUrl { get; }

        /// <summary>
        /// Validates and builds a descriptor. A payload is a notification,
        /// not a document — the caps refuse anything that could not be a
        /// real descriptor rather than trying to repair it.
        /// </summary>
        /// <param name="provider">The provider namespace.</param>
        /// <param name="key">The content key.</param>
        /// <param name="name">The display name, if any.</param>
        /// <param name="runTimeTicks">The runtime in ticks, if known.</param>
        /// <param name="imageUrl">The artwork URL, if any.</param>
        /// <param name="descriptor">The built descriptor, or null.</param>
        /// <returns>Whether the descriptor was valid.</returns>
        public static bool TryCreate(
            string? provider,
            string? key,
            string? name,
            long? runTimeTicks,
            string? imageUrl,
            out ContentDescriptor? descriptor)
        {
            descriptor = null;

            if (provider is null || !ProviderName.IsMatch(provider))
            {
                return false;
            }

            if (string.IsNullOrEmpty(key) || key.Length > 512)
            {
                return false;
            }

            if (name is not null && name.Length > 256)
            {
                return false;
            }

            if (runTimeTicks is < 0)
            {
                return false;
            }

            if (imageUrl is not null && imageUrl.Length > 1024)
            {
                return false;
            }

            descriptor = new ContentDescriptor(
                provider,
                key,
                name ?? string.Empty,
                runTimeTicks ?? 0,
                imageUrl ?? string.Empty);
            return true;
        }
    }
}
