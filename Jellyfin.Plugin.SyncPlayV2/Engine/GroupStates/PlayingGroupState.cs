#nullable disable

using System;
using System.Threading;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using MediaBrowser.Model.SyncPlay;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SyncPlayV2.Engine.GroupStates
{
    /// <summary>
    /// Class PlayingGroupState.
    /// </summary>
    /// <remarks>
    /// Class is not thread-safe, external locking is required when accessing methods.
    /// </remarks>
    public class PlayingGroupState : AbstractGroupState
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PlayingGroupState"/> class.
        /// </summary>
        /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
        public PlayingGroupState(ILoggerFactory loggerFactory)
            : base(loggerFactory)
        {
        }

        /// <inheritdoc />
        public override GroupStateType Type { get; } = GroupStateType.Playing;

        /// <summary>
        /// Gets or sets a value indicating whether requests for buffering should be ignored.
        /// </summary>
        public bool IgnoreBuffering { get; set; }

        /// <inheritdoc />
        public override void SessionJoined(IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Hot join: a v2 member catches the running playback without
            // pausing anyone — flagged as not-waited-on, it rendezvouses via
            // a private scheduled Unpause when it reports Ready. v1 members
            // get the classic barrier below: the whole group waits for them.
            if (context is IGroupStateContextV2 v2
                && v2.IsV2Member(session.Id)
                && SyncPlayV2Plugin.Instance?.Configuration.HotJoin != false)
            {
                v2.BeginHotJoin(session, cancellationToken);
                return;
            }

            // Wait for session to be ready.
            var waitingState = new WaitingGroupState(LoggerFactory);
            context.SetState(waitingState);
            waitingState.SessionJoined(context, Type, session, cancellationToken);
        }

        /// <inheritdoc />
        public override void SessionLeaving(IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Do nothing.
        }

        /// <inheritdoc />
        public override void HandleRequest(PlayGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Change state.
            var waitingState = new WaitingGroupState(LoggerFactory);
            context.SetState(waitingState);
            waitingState.HandleRequest(request, context, Type, session, cancellationToken);
        }

        /// <inheritdoc />
        public override void HandleRequest(UnpauseGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            if (!prevState.Equals(Type))
            {
                // Pick a suitable time that accounts for latency.
                var delayMillis = Math.Max(context.GetHighestPing() * 2, context.DefaultPing);

                // Unpause group and set starting point in future.
                // Clients will start playback at LastActivity (datetime) from PositionTicks (playback position).
                // The added delay does not guarantee, of course, that the command will be received in time.
                // Playback synchronization will mainly happen client side.
                context.LastActivity = DateTime.UtcNow.AddMilliseconds(delayMillis);

                var command = context.NewSyncPlayCommand(SendCommandType.Unpause);
                context.SendCommand(session, SyncPlayBroadcastType.AllGroup, command, cancellationToken);

                // Notify relevant state change event.
                SendGroupStateUpdate(context, request, session, cancellationToken);
            }
            else
            {
                // Client got lost, sending current state.
                var command = context.NewSyncPlayCommand(SendCommandType.Unpause);
                context.SendCommand(session, SyncPlayBroadcastType.CurrentSession, command, cancellationToken);
            }
        }

        /// <inheritdoc />
        public override void HandleRequest(PauseGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Change state.
            var pausedState = new PausedGroupState(LoggerFactory);
            context.SetState(pausedState);
            pausedState.HandleRequest(request, context, Type, session, cancellationToken);
        }

        /// <inheritdoc />
        public override void HandleRequest(StopGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Change state.
            var idleState = new IdleGroupState(LoggerFactory);
            context.SetState(idleState);
            idleState.HandleRequest(request, context, Type, session, cancellationToken);
        }

        /// <inheritdoc />
        public override void HandleRequest(SeekGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Change state.
            var waitingState = new WaitingGroupState(LoggerFactory);
            context.SetState(waitingState);
            waitingState.HandleRequest(request, context, Type, session, cancellationToken);
        }

        /// <inheritdoc />
        public override void HandleRequest(BufferGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            if (IgnoreBuffering)
            {
                return;
            }

            if (context is IGroupStateContextV2 v2 && v2.IsHotJoining(session.Id))
            {
                // A hot-joining member's stalls are its own; the group keeps
                // playing and the member re-reports Ready when it recovers.
                context.SetBuffering(session, true);
                return;
            }

            // Change state.
            var waitingState = new WaitingGroupState(LoggerFactory);
            context.SetState(waitingState);
            waitingState.HandleRequest(request, context, Type, session, cancellationToken);
        }

        /// <inheritdoc />
        public override void HandleRequest(ReadyGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            if (context is IGroupStateContextV2 v2 && v2.IsHotJoining(session.Id))
            {
                v2.CompleteHotJoin(session, cancellationToken);
                return;
            }

            ResumeIgnoredMember(context, session);

            if (prevState.Equals(Type))
            {
                // Group was not waiting, make sure client has latest state.
                var command = context.NewSyncPlayCommand(SendCommandType.Unpause);
                context.SendCommand(session, SyncPlayBroadcastType.CurrentSession, command, cancellationToken);
            }
            else if (prevState.Equals(GroupStateType.Waiting))
            {
                // Notify relevant state change event.
                SendGroupStateUpdate(context, request, session, cancellationToken);
            }
        }

        /// <inheritdoc />
        public override void HandleRequest(NextItemGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Change state.
            var waitingState = new WaitingGroupState(LoggerFactory);
            context.SetState(waitingState);
            waitingState.HandleRequest(request, context, Type, session, cancellationToken);
        }

        /// <inheritdoc />
        public override void HandleRequest(PreviousItemGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Change state.
            var waitingState = new WaitingGroupState(LoggerFactory);
            context.SetState(waitingState);
            waitingState.HandleRequest(request, context, Type, session, cancellationToken);
        }
    }
}
