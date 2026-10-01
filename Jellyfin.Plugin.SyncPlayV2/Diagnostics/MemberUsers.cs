using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SyncPlayV2.Diagnostics;

/// <summary>
/// What the report shows from each member's user. Read after the group locks
/// are released: a user lookup can be a database query, and the settings page
/// asks for the report every 5 seconds.
/// </summary>
internal static class MemberUsers
{
    /// <summary>Sets each member's autoplay flag, null where the user cannot be read.</summary>
    /// <param name="groups">The groups, built under their locks.</param>
    /// <param name="users">The user manager.</param>
    public static void FillAutoplay(IEnumerable<GroupDiagnostics> groups, IUserManager users)
    {
        var known = new Dictionary<Guid, bool?>();
        foreach (var group in groups)
        {
            foreach (var member in group.Members)
            {
                if (!known.TryGetValue(member.UserId, out var autoplay))
                {
                    autoplay = ReadAutoplay(users, member.UserId);
                    known[member.UserId] = autoplay;
                }

                member.AutoplayNextEpisode = autoplay;
            }
        }
    }

    private static bool? ReadAutoplay(IUserManager users, Guid userId)
    {
        if (userId.Equals(Guid.Empty))
        {
            return null;
        }

        try
        {
            return users.GetUserById(userId)?.EnableNextEpisodeAutoPlay;
        }
        catch (Exception)
        {
            // One unreadable user leaves its flag unknown, not the report failed.
            return null;
        }
    }
}
