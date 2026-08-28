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
        /// Gets or sets a value indicating whether the member itself asked not
        /// to be waited for, as opposed to the group having given up on it.
        ///
        /// Both states set <see cref="IgnoreGroupWait"/>, but they must not be
        /// undone by the same event: the group is entitled to start waiting for
        /// a member again once it reports, and is not entitled to overrule a
        /// user who chose to be a spectator. One field carrying both meanings
        /// silently cleared the user's choice on the next report — measured
        /// against jellyfin-web, which sets IgnoreWait when local playback
        /// resumes.
        /// </summary>
        /// <value><c>true</c> when the member requested it via SetIgnoreWait.</value>
        public bool IgnoreGroupWaitByRequest { get; set; }

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
        /// The group waits for this member again, undoing its own decision to stop.
        ///
        /// Only the group's decision is reversed. A member that asked not to be
        /// waited for keeps that until it asks otherwise, which is why the two
        /// meanings are carried by separate fields (see
        /// <see cref="IgnoreGroupWaitByRequest"/>).
        /// </summary>
        /// <returns><c>true</c> if the member had been ignored and is now waited for again.</returns>
        public bool ResumeWaiting()
        {
            if (!IgnoredByTimeout)
            {
                return false;
            }

            IgnoredByTimeout = false;

            if (!IgnoreGroupWaitByRequest)
            {
                IgnoreGroupWait = false;
            }

            return true;
        }

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

        /// <summary>
        /// Gets or sets how many position corrections in a row the group has
        /// sent this member without it arriving. Reset the moment it reports
        /// ready.
        /// </summary>
        /// <value>The number of consecutive uncorrected attempts.</value>
        public int CorrectionAttempts { get; set; }

        /// <summary>
        /// Gets or sets how far out of position the member was at the previous
        /// correction, so the next one can tell "still moving" from "as close
        /// as a seek will ever get it".
        /// </summary>
        /// <value>The previous correction's delay, in ticks.</value>
        public long LastCorrectionDelayTicks { get; set; }
    }
}
