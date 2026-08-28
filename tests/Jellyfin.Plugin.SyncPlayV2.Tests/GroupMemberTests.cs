using Jellyfin.Plugin.SyncPlayV2.Engine;
using MediaBrowser.Controller.Session;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

public class GroupMemberTests
{
    private static GroupMember Ignored(bool byRequest = false)
    {
        var member = new GroupMember(new SessionInfo(null, null) { Id = "s1" })
        {
            IsBuffering = true,
            IgnoreGroupWait = true,
            IgnoredByTimeout = true,
            IgnoreGroupWaitByRequest = byRequest,
        };

        return member;
    }

    [Fact]
    public void AMemberTheGroupGaveUpOnIsWaitedForAgain()
    {
        var member = Ignored();

        Assert.True(member.ResumeWaiting());
        Assert.False(member.IgnoredByTimeout);
        Assert.False(member.IgnoreGroupWait);
    }

    [Fact]
    public void ASpectatorsOwnChoiceSurvivesTheGroupChangingItsMind()
    {
        // The group's timeout and the member's own SetIgnoreWait both raise
        // IgnoreGroupWait. Reversing the first must not reverse the second.
        var member = Ignored(byRequest: true);

        Assert.True(member.ResumeWaiting());
        Assert.False(member.IgnoredByTimeout);
        Assert.True(member.IgnoreGroupWait);
    }

    [Fact]
    public void AMemberTheGroupNeverGaveUpOnIsUntouched()
    {
        var member = new GroupMember(new SessionInfo(null, null) { Id = "s1" })
        {
            IgnoreGroupWait = true,
            IgnoreGroupWaitByRequest = true,
        };

        Assert.False(member.ResumeWaiting());
        Assert.True(member.IgnoreGroupWait);
    }
}
