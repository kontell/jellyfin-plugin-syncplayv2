using System;
using Jellyfin.Plugin.SyncPlayV2.Engine;
using Xunit;

namespace Jellyfin.Plugin.SyncPlayV2.Tests;

public class CorrectionPolicyTests
{
    private static long Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds).Ticks;

    [Fact]
    public void FirstCorrectionAlwaysGetsItsChance()
    {
        // Most members are simply late. One seek fixes them, and rendezvousing
        // on the first report would reload streams that had no need of it.
        Assert.False(CorrectionPolicy.CannotConverge(attempts: 1, Ms(0), Ms(9000)));
    }

    [Fact]
    public void AConvergingMemberIsCorrectedAgain()
    {
        // 4s out, then 1.2s out: the seek is landing, so let it finish.
        Assert.False(CorrectionPolicy.CannotConverge(attempts: 2, Ms(4000), Ms(1200)));
    }

    [Fact]
    public void AMemberThatStaysPutIsRendezvoused()
    {
        // The measured shape of the transcode livelock: each correction comes
        // back about as far out as the last, because the seek lands on the
        // segment boundary rather than the target.
        Assert.True(CorrectionPolicy.CannotConverge(attempts: 2, Ms(4200), Ms(4100)));
    }

    [Fact]
    public void ImprovementIsMeasuredInAbsoluteTerms()
    {
        // 3s behind becomes 3s ahead: the gap is the same size, the seek simply
        // overshot. Treating that as progress is how a livelock hides.
        Assert.True(CorrectionPolicy.CannotConverge(attempts: 2, Ms(3000), Ms(-3000)));
    }

    [Fact]
    public void ImprovementIsMeasuredAgainstTheThreshold()
    {
        // Just under 250ms of progress is not progress.
        Assert.True(CorrectionPolicy.CannotConverge(attempts: 2, Ms(4000), Ms(3800)));

        // Just over it is.
        Assert.False(CorrectionPolicy.CannotConverge(attempts: 2, Ms(4000), Ms(3700)));
    }

    [Fact]
    public void SteadyButHopelessProgressStillHitsTheCap()
    {
        // Improving by 300ms a round from 9s would take thirty rounds with the
        // group waiting throughout. The cap is what bounds that.
        Assert.True(CorrectionPolicy.CannotConverge(
            attempts: CorrectionPolicy.MaxAttempts, Ms(9000), Ms(8700)));
    }

    [Fact]
    public void TheCapWinsEvenOverRealProgress()
    {
        Assert.True(CorrectionPolicy.CannotConverge(
            attempts: CorrectionPolicy.MaxAttempts, Ms(9000), Ms(10)));
    }
}
