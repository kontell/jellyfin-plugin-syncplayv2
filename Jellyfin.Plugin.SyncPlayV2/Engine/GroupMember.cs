#nullable disable

using System;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.SyncPlayV2.Engine
{
    /// <summary>
    /// Class GroupMember.
    /// </summary>
    public class GroupMember
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GroupMember"/> class.
        /// </summary>
        /// <param name="session">The session.</param>
        public GroupMember(SessionInfo session)
        {
            Session = session;
            SessionId = session.Id;
            UserId = session.UserId;
            UserName = session.UserName;
        }

        /// <summary>
        /// Gets or sets the session of the member. Updated when the member's device
        /// reconnects and gets a new session instance with the same identifier.
        /// </summary>
        /// <value>The session.</value>
        public SessionInfo Session { get; set; }

        /// <summary>
        /// Gets the identifier of the session.
        /// </summary>
        /// <value>The session identifier.</value>
        public string SessionId { get; }

        /// <summary>
        /// Gets the identifier of the user.
        /// </summary>
        /// <value>The user identifier.</value>
        public Guid UserId { get; }

        /// <summary>
        /// Gets the username.
        /// </summary>
        /// <value>The username.</value>
        public string UserName { get; }

        /// <summary>
        /// Gets or sets the ping, in milliseconds.
        /// </summary>
        /// <value>The ping.</value>
        public long Ping { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this member is buffering.
        /// </summary>
        /// <value><c>true</c> if member is buffering; <c>false</c> otherwise.</value>
        public bool IsBuffering { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this member is following group playback.
        /// </summary>
        /// <value><c>true</c> to ignore member on group wait; <c>false</c> if they're following group playback.</value>
        public bool IgnoreGroupWait { get; set; }

        /// <summary>
        /// Gets or sets the time at which this member last started buffering.
        /// </summary>
        /// <value>The time at which the member last started buffering.</value>
        public DateTime BufferingSince { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this member is being ignored because it
        /// kept the group waiting for too long. Cleared when the member reports again.
        /// </summary>
        /// <value><c>true</c> if the member is ignored due to a group-wait timeout; <c>false</c> otherwise.</value>
        public bool IgnoredByTimeout { get; set; }

        /// <summary>
        /// Gets or sets the SyncPlay protocol version the member's client speaks.
        /// Version 2 clients receive state snapshots and position beacons.
        /// </summary>
        /// <value>The protocol version, defaults to 1.</value>
        public int ProtocolVersion { get; set; } = 1;

        /// <summary>
        /// Gets or sets a value indicating whether the member's session is currently connected.
        /// A member whose session ended stays in the group for a grace window, during which
        /// the group does not wait on it and no messages are sent to it.
        /// </summary>
        /// <value><c>true</c> if the member is connected; <c>false</c> otherwise.</value>
        public bool IsConnected { get; set; } = true;

        /// <summary>
        /// Gets or sets the time at which the member's session ended.
        /// </summary>
        /// <value>The time at which the member disconnected.</value>
        public DateTime DisconnectedSince { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the member is catching a
        /// running playback (hot join): the group does not pause or wait for
        /// it, and its Ready is answered with a private scheduled Unpause.
        /// </summary>
        /// <value><c>true</c> while the member is hot-joining.</value>
        public bool HotJoining { get; set; }
    }
}
