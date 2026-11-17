using System;
using System.IO;
using FanControl.LianLi.Plugin;
using FanControl.LianLi.Tests.Fakes;
using Xunit;

namespace FanControl.LianLi.Tests.Plugin;

/// <summary>
/// Which copy of the plugin drives the fans when FanControl has loaded two, and what the winner does
/// to the other copy's file: it renames only a file provably this plugin's, inside the Plugins folder
/// it was loaded from, never its own, and a later start deletes what was renamed.
/// </summary>
public class PluginCopyGuardTests {
    private static readonly DateTime Earlier = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Version Current = new Version(1, 1, 40);
    private static readonly Version Older = new Version(1, 1, 39);
    private static readonly Version Newer = new Version(1, 1, 41);

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "lianli-guard", "FanControl");
    private static readonly string Plugins = Path.Combine(Root, "Plugins");
    private static readonly string OwnPath = Path.Combine(Plugins, "LianLi-Argb", "FanControl.LianLi-Argb.dll");
    private static readonly string OldPath = Path.Combine(Plugins, "FanControl.LianLi.Argb.dll");

    private readonly FakeLogger _log = new FakeLogger();
    private readonly FakePluginFileSystem _files = new FakePluginFileSystem();
    private readonly FakePluginCopyClaim _claim = new FakePluginCopyClaim();
    private readonly FakePluginCopyScanner _scanner;
    private readonly PluginCopyGuard _guard;
    private readonly object _owner = new object();

    public PluginCopyGuardTests() {
        _scanner = new FakePluginCopyScanner(new PluginCopy(OwnPath, Current, hasGuard: true));
        _guard = new PluginCopyGuard(_scanner, _files, _claim);
        _files.Add(OwnPath, Earlier);
    }

    private PluginCopy Other(string path, Version version, bool hasGuard, DateTime written) {
        _files.Add(path, written);
        var copy = new PluginCopy(path, version, hasGuard);
        _scanner.Copies.Add(copy);
        return copy;
    }

    private bool Activate() => _guard.TryActivate(_owner, _log);

    private string Removed(string path) => path + PluginCopyGuard.RemovedSuffix;

    [Fact]
    public void Constructor_RejectsNullDependencies() {
        Assert.Throws<ArgumentNullException>(() => new PluginCopyGuard(null!, _files, _claim));
        Assert.Throws<ArgumentNullException>(() => new PluginCopyGuard(_scanner, null!, _claim));
        Assert.Throws<ArgumentNullException>(() => new PluginCopyGuard(_scanner, _files, null!));
    }

    [Fact]
    public void TryActivate_RejectsNullArguments() {
        Assert.Throws<ArgumentNullException>(() => _guard.TryActivate(null!, _log));
        Assert.Throws<ArgumentNullException>(() => _guard.TryActivate(_owner, null!));
    }

    [Fact]
    public void Process_IsOneGuardForTheWholeCopy()
        => Assert.Same(PluginCopyGuard.Process, PluginCopyGuard.Process);

    [Fact]
    public void Alone_TakesTheClaimAndLogsNothing() {
        Assert.True(Activate());
        Assert.True(_claim.Held);
        Assert.Empty(_log.Messages);
    }

    [Fact]
    public void Alone_ButTheClaimIsHeldElsewhere_StaysIdleAndLogsOnce_ThenTakesOverWhenItIsFree() {
        _claim.HeldElsewhere = true;

        Assert.False(Activate());
        Assert.False(Activate());

        string idle = Assert.Single(_log.Messages);
        Assert.Contains("stays idle: another copy in this FanControl process is already driving the fans", idle, StringComparison.Ordinal);
        Assert.Contains(OwnPath, idle, StringComparison.Ordinal);

        _claim.HeldElsewhere = false;
        Assert.True(Activate());
        Assert.Equal(2, _log.Messages.Count);
        Assert.Contains("now drives the fans", _log.Messages[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherInstanceOfThisCopy_TakesTheClaimOver_WithoutTakingItAgain() {
        var next = new object();
        Assert.True(Activate());
        _claim.HeldElsewhere = true;

        Assert.True(_guard.TryActivate(next, _log));

        // Only the instance it is now held for gives it up.
        _guard.Release(_owner);
        Assert.True(_claim.Held);
        _guard.Release(next);
        Assert.False(_claim.Held);
    }

    [Fact]
    public void Release_IsSafeTwice_AndWithoutAClaim() {
        _guard.Release(_owner);
        Assert.Equal(0, _claim.Releases);

        Assert.True(Activate());
        _guard.Release(_owner);
        _guard.Release(_owner);
        Assert.Equal(1, _claim.Releases);
    }

    [Fact]
    public void ANewerCopyWithTheGuard_ThatIsDriving_LeavesThisOneIdle_AndThisOneTouchesNoFile() {
        Other(Path.Combine(Plugins, "LianLi", "FanControl.LianLi.dll"), Newer, hasGuard: true, Earlier);
        _claim.HeldElsewhere = true;

        Assert.False(Activate());

        Assert.Empty(_files.Moves);
        Assert.False(_claim.Held);
        string idle = Assert.Single(_log.Messages);
        Assert.Contains("stays idle: the copy at " + Path.Combine(Plugins, "LianLi", "FanControl.LianLi.dll") + " (version 1.1.41) is newer", idle, StringComparison.Ordinal);
        Assert.Contains("That copy removes this one", idle, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewerCopyWithTheGuard_ThatNeverStarted_LeavesThisOneDriving_AndItStepsAsideOnceItDoes() {
        // FanControl creates a plugin disabled in its settings but never initialises it.
        Other(Path.Combine(Plugins, "LianLi", "FanControl.LianLi.dll"), Newer, hasGuard: true, Earlier);

        Assert.True(Activate());
        Assert.True(Activate());
        Assert.True(Activate());

        Assert.True(_claim.Held);
        Assert.Empty(_files.Moves);

        // Beside it a second time, it has had its chance: disabled, or unable to remove this copy.
        Assert.Equal(2, _log.Messages.Count);
        Assert.Contains("is loaded but has not started", _log.Messages[0], StringComparison.Ordinal);
        Assert.Contains("has not taken over, because it is disabled in FanControl's plugin settings or could not remove this copy", _log.Messages[1], StringComparison.Ordinal);

        // Once it is the only copy, driving is no longer worth a word, but the change is logged.
        _scanner.Copies.Clear();
        Assert.True(Activate());
        Assert.Contains("now drives the fans", _log.Messages[2], StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderCopyWithoutTheGuard_ThatIsNewerThanThisOne_StaysAheadOnlyWhileFanControlStillCreatesIt() {
        Other(OldPath, Newer, hasGuard: false, Earlier);
        Assert.False(Activate());

        // Its file gone: it may have gone this pass, after FanControl created it, but FanControl
        // creates it no more after that, so this copy drives from the pass after.
        _files.Delete(OldPath);
        Assert.False(Activate());
        Assert.True(Activate());
        Assert.Contains("is loaded but has not started", _log.Messages[_log.Messages.Count - 1], StringComparison.Ordinal);
    }

    [Fact]
    public void ANewerCopyWithoutTheGuard_LeavesThisOneIdle_AndSaysWhichFileToDelete() {
        Other(OldPath, Newer, hasGuard: false, Earlier);

        Assert.False(Activate());

        Assert.Empty(_files.Moves);
        Assert.Contains("Close FanControl and delete " + OwnPath + " from its Plugins folder", Assert.Single(_log.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public void ANewerCopy_WhenThisOneIsInMemory_NamesNoFileOfItsOwn() {
        _scanner.Own = new PluginCopy(string.Empty, Current, hasGuard: true);
        Other(OldPath, Newer, hasGuard: false, Earlier);

        Assert.False(Activate());

        string idle = Assert.Single(_log.Messages);
        Assert.Contains("this copy of the plugin (a copy loaded from memory (version 1.1.40))", idle, StringComparison.Ordinal);
        Assert.Contains("delete this copy from its Plugins folder", idle, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderCopyRunningBeforeTheGuard_IsRemoved_AndThisOneTakesOverAtTheNextInitialize() {
        Other(OldPath, Older, hasGuard: false, Later);

        // FanControl created the older copy this time: this one renames it and waits.
        Assert.False(Activate());
        Assert.Equal(new[] { OldPath + " -> " + Removed(OldPath) }, _files.Moves);
        Assert.False(_claim.Held);
        Assert.Equal(2, _log.Messages.Count);
        Assert.Contains("removed the other copy of the plugin (" + OldPath + " (version 1.1.39))", _log.Messages[0], StringComparison.Ordinal);
        Assert.Contains("is the newest but stays idle for now", _log.Messages[1], StringComparison.Ordinal);
        Assert.Contains("takes over at FanControl's next refresh", _log.Messages[1], StringComparison.Ordinal);

        // A refresh rescans the Plugins folder and no longer creates it, though its assembly stays
        // loaded: this copy drives the fans.
        Assert.True(Activate());
        Assert.True(_claim.Held);
        Assert.Single(_files.Moves);
        Assert.Contains("now drives the fans", _log.Messages[2], StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderCopyRunningBeforeTheGuard_ThatCannotBeRemoved_KeepsThisOneIdle_AndSaysWhatToDo() {
        Other(OldPath, Older, hasGuard: false, Later);
        _files.MoveFailure = new UnauthorizedAccessException("Access to the path is denied.");

        Assert.False(Activate());
        Assert.False(Activate());

        Assert.Equal(2, _log.Messages.Count);
        Assert.Contains("could not be removed. Close FanControl, delete that file from its Plugins folder, then start FanControl again", _log.Messages[1], StringComparison.Ordinal);
        Assert.DoesNotContain("takes over", _log.Messages[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderCopyRunningFromMemory_CountsAsRunning() {
        _scanner.Copies.Add(new PluginCopy(string.Empty, Older, hasGuard: false));

        Assert.False(Activate());
        Assert.Contains("could not be removed", _log.Messages[_log.Messages.Count - 1], StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderCopyRunning_WhileANewerCopyIsTheWinner_LeavesThisOneIdle() {
        Other(OldPath, Older, hasGuard: false, Earlier);
        Other(Path.Combine(Plugins, "LianLi", "FanControl.LianLi.dll"), Newer, hasGuard: true, Earlier);

        Assert.False(Activate());
        Assert.Empty(_files.Moves);
        Assert.Contains("That copy removes this one", Assert.Single(_log.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderCopyWithTheGuard_IsRemoved_AndThisOneDrivesTheFans() {
        Other(OldPath, Older, hasGuard: true, Later);

        Assert.True(Activate());

        Assert.Single(_files.Moves);
        Assert.True(_claim.Held);
    }

    [Fact]
    public void AnOlderCopyWithTheGuard_StillHoldingTheClaim_LeavesThisOneIdleForNow() {
        Other(OldPath, Older, hasGuard: true, Later);
        _claim.HeldElsewhere = true;

        Assert.False(Activate());

        Assert.Single(_files.Moves);
        Assert.Contains("another copy is still driving the fans in this FanControl process", _log.Messages[_log.Messages.Count - 1], StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameVersion_TheFileInstalledLastWins() {
        Other(OldPath, Current, hasGuard: true, Later);
        _claim.HeldElsewhere = true;
        Assert.False(Activate());
        Assert.Empty(_files.Moves);
    }

    [Fact]
    public void TheSameVersion_ThisFileInstalledLast_Wins() {
        _files.Add(OwnPath, Later);
        Other(OldPath, Current, hasGuard: true, Earlier);
        Assert.True(Activate());
        Assert.Single(_files.Moves);
    }

    [Fact]
    public void TheSameVersionAndTime_ThePathDecides_TheSameWayForEitherCopy() {
        string before = Path.Combine(Plugins, "A", "FanControl.LianLi.dll");
        string after = Path.Combine(Plugins, "Z", "FanControl.LianLi.dll");
        _scanner.Own = new PluginCopy(before, Current, hasGuard: true);
        _files.Add(before, Earlier);
        Other(after, Current, hasGuard: true, Earlier);
        _claim.HeldElsewhere = true;

        Assert.False(Activate());

        var otherScanner = new FakePluginCopyScanner(new PluginCopy(after, Current, hasGuard: true));
        otherScanner.Copies.Add(new PluginCopy(before, Current, hasGuard: true));
        var otherGuard = new PluginCopyGuard(otherScanner, _files, new FakePluginCopyClaim());
        Assert.True(otherGuard.TryActivate(new object(), _log));
        Assert.Equal(new[] { before + " -> " + Removed(before) }, _files.Moves);
    }

    [Fact]
    public void AnOlderCopy_IsOnlyCompared_WhenAnotherCopyIsNewer() {
        Other(Path.Combine(Plugins, "FanControl.LianLi.Lighting.dll"), Older, hasGuard: true, Later);
        Other(OldPath, Newer, hasGuard: true, Earlier);
        _claim.HeldElsewhere = true;

        Assert.False(Activate());
        Assert.Contains("the copy at " + OldPath, Assert.Single(_log.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileOutsideAPluginsFolder_IsNeverTouched() {
        string own = Path.Combine(Path.GetTempPath(), "lianli-guard", "harness", "FanControl.LianLi.dll");
        string other = Path.Combine(Path.GetTempPath(), "lianli-guard", "harness", "FanControl.LianLi.Argb.dll");
        _scanner.Own = new PluginCopy(own, Current, hasGuard: true);
        Other(other, Older, hasGuard: true, Earlier);

        Assert.True(Activate());

        Assert.Empty(_files.Moves);
        Assert.Equal(0, _files.Finds);
        Assert.Contains("this copy was not loaded from a FanControl Plugins folder", Assert.Single(_log.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileAtTheTopOfTheDrive_HasNoPluginsFolder() {
        string own = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "FanControl.LianLi.dll");
        _scanner.Own = new PluginCopy(own, Current, hasGuard: true);
        Other(OldPath, Older, hasGuard: true, Earlier);

        Assert.True(Activate());
        Assert.Contains("this copy was not loaded from a FanControl Plugins folder", Assert.Single(_log.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public void ThisCopyInMemory_FindsNoPluginsFolder() {
        _scanner.Own = new PluginCopy(string.Empty, Newer, hasGuard: true);
        Other(OldPath, Older, hasGuard: true, Earlier);

        Assert.True(Activate());
        Assert.Empty(_files.Moves);
        Assert.Equal(0, _files.Finds);
    }

    [Fact]
    public void AnOtherCopyInMemory_IsLeftInPlace() {
        _scanner.Copies.Add(new PluginCopy(string.Empty, Older, hasGuard: true));

        Assert.True(Activate());

        Assert.Empty(_files.Moves);
        Assert.Contains("it was not loaded from a file", Assert.Single(_log.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public void ThisCopysOwnFile_IsNeverRenamed() {
        _scanner.Copies.Add(new PluginCopy(OwnPath, Older, hasGuard: true));

        Assert.True(Activate());

        Assert.Empty(_files.Moves);
        Assert.Contains("it is this copy's own file", Assert.Single(_log.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileOutsideThePluginsFolder_IsLeftInPlace() {
        Other(Path.Combine(Root, "FanControl.LianLi.dll"), Older, hasGuard: true, Earlier);

        Assert.True(Activate());

        Assert.Empty(_files.Moves);
        Assert.Contains("it is not in FanControl's Plugins folder", Assert.Single(_log.Messages), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LianLi.dll")]
    [InlineData("fancontrol.LianLi.dll")]
    [InlineData("FanControl.LianLi.dll.bak")]
    public void AFileNotNamedLikeAPlugin_IsLeftInPlace(string name) {
        Other(Path.Combine(Plugins, name), Older, hasGuard: true, Earlier);

        Assert.True(Activate());

        Assert.Empty(_files.Moves);
        Assert.Contains("its file is not named like a FanControl plugin", Assert.Single(_log.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsGone_IsLeftAlone_WithNothingToSay() {
        _scanner.Copies.Add(new PluginCopy(OldPath, Older, hasGuard: true));

        Assert.True(Activate());

        Assert.Empty(_files.Moves);
        Assert.Empty(_log.Messages);
    }

    [Fact]
    public void ARenameRefused_IsLoggedOnce_AndTheFansAreUnaffected() {
        Other(OldPath, Older, hasGuard: true, Earlier);
        _files.MoveFailure = new UnauthorizedAccessException("Access to the path is denied.");

        Assert.True(Activate());
        Assert.True(Activate());

        Assert.Contains(OldPath, _files.Files);
        string failure = Assert.Single(_log.Messages);
        Assert.Contains("could not remove the other copy of the plugin (" + OldPath, failure, StringComparison.Ordinal);
        Assert.Contains("Access to the path is denied.", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void ARenameThatFailsWithAnInputOutputError_IsLogged() {
        Other(OldPath, Older, hasGuard: true, Earlier);
        _files.MoveFailure = new IOException("Cannot create a file when that file already exists.");

        Assert.True(Activate());
        Assert.Contains("Cannot create a file when that file already exists.", Assert.Single(_log.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public void ARenameThatFailsAnyOtherWay_Propagates() {
        Other(OldPath, Older, hasGuard: true, Earlier);
        _files.MoveFailure = new InvalidOperationException("unexpected");

        Assert.Throws<InvalidOperationException>(() => Activate());
    }

    [Theory]
    [InlineData("FanControl.LianLi")]
    [InlineData("FanControl.LianLi.Argb")]
    [InlineData("FanControl.LianLi-Lighting")]
    public void ALeftoverOfThisPlugin_IsDeletedOnce(string assemblyName) {
        string leftover = Path.Combine(Plugins, "Old", "FanControl.LianLi.Lighting.dll.removed");
        _files.Add(leftover, Earlier, assemblyName);

        Assert.True(Activate());
        Assert.True(Activate());

        Assert.Equal(new[] { leftover }, _files.Deletes);
        Assert.Equal(1, _files.Finds);
        Assert.Contains("deleted " + leftover, Assert.Single(_log.Messages), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("FanControl.Other")]
    [InlineData("FanControl.LianLiX")]
    public void ALeftoverOfAnotherPlugin_IsLeftInPlace(string assemblyName) {
        string leftover = Path.Combine(Plugins, "FanControl.Other.dll.removed");
        _files.Add(leftover, Earlier, assemblyName);

        Assert.True(Activate());

        Assert.Empty(_files.Deletes);
        Assert.Contains("it is not a copy of this plugin", Assert.Single(_log.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public void ALeftoverStillLoaded_IsLeftForTheNextStart() {
        // Renamed this run (or by another copy) from a copy this process still has mapped, and this
        // copy's own file renamed by a newer copy: neither can be deleted until FanControl restarts.
        Other(OldPath, Older, hasGuard: true, Earlier);
        _files.Add(Removed(OldPath), Earlier);
        _files.Add(Removed(OwnPath), Earlier);
        string gone = Path.Combine(Plugins, "FanControl.LianLi-Lighting.dll.removed");
        _files.Add(gone, Earlier);

        Assert.True(Activate());

        Assert.Equal(new[] { gone }, _files.Deletes);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(BadImageFormatException))]
    public void ALeftoverThatCannotBeDeleted_IsLogged(Type failure) {
        string leftover = Removed(OldPath);
        _files.Add(leftover, Earlier);
        _files.DeleteFailure = (Exception)Activator.CreateInstance(failure, "refused")!;

        Assert.True(Activate());

        Assert.Contains("could not delete " + leftover + ", a removed copy of the plugin: refused", Assert.Single(_log.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public void ALeftoverThatIsNotAnAssembly_IsLogged() {
        string leftover = Removed(OldPath);
        _files.Add(leftover, Earlier);
        _files.ReadFailure = new BadImageFormatException("not an assembly");

        Assert.True(Activate());

        Assert.Empty(_files.Deletes);
        Assert.Contains("not an assembly", Assert.Single(_log.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public void ALeftoverThatFailsAnyOtherWay_Propagates() {
        _files.Add(Removed(OldPath), Earlier);
        _files.DeleteFailure = new InvalidOperationException("unexpected");

        Assert.Throws<InvalidOperationException>(() => Activate());
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void APluginsFolderThatCannotBeSearched_IsLogged_AndTheFansAreUnaffected(Type failure) {
        _files.FindFailure = (Exception)Activator.CreateInstance(failure, "unreadable")!;

        Assert.True(Activate());

        Assert.Contains("could not look for removed copies of the plugin in " + Plugins + ": unreadable", Assert.Single(_log.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public void APluginsFolderSearchThatFailsAnyOtherWay_Propagates() {
        _files.FindFailure = new InvalidOperationException("unexpected");

        Assert.Throws<InvalidOperationException>(() => Activate());
    }

    [Fact]
    public void OwnFileRemoved_IsFalse_ForALoneCopy_AndAnIdleOne() {
        Assert.True(Activate());
        Assert.False(_guard.OwnFileRemoved());

        Other(OldPath, Newer, hasGuard: false, Earlier);
        Assert.False(Activate());
        _files.Move(OwnPath, Removed(OwnPath));
        Assert.False(_guard.OwnFileRemoved());
    }

    [Fact]
    public void OwnFileRemoved_IsTrueOnce_WhenTheWinnerRenamesThisDrivingCopyAway() {
        // FanControl started this losing copy first, so it took the claim before the winner started.
        Other(Path.Combine(Plugins, "LianLi", "FanControl.LianLi.dll"), Newer, hasGuard: true, Earlier);
        Assert.True(Activate());
        Assert.Contains("is loaded but has not started", Assert.Single(_log.Messages), StringComparison.Ordinal);
        Assert.False(_guard.OwnFileRemoved());

        _files.Move(OwnPath, Removed(OwnPath));

        Assert.True(_guard.OwnFileRemoved());
        Assert.False(_guard.OwnFileRemoved());
    }

    [Fact]
    public void ThisCopyInMemory_HasNoFileToWatch() {
        _scanner.Own = new PluginCopy(string.Empty, Newer, hasGuard: true);
        Other(OldPath, Older, hasGuard: true, Earlier);

        Assert.True(Activate());
        Assert.False(_guard.OwnFileRemoved());
    }

    [Fact]
    public void AnOlderCopyRenamedByTheWinnerEarlierInThisPass_StillKeepsALoserIdle() {
        // FanControl created the guard-less copy this pass, then the winner renamed its file before
        // this copy started: the guard-less copy is still driving, so this copy must not take the claim.
        Other(OldPath, Older, hasGuard: false, Earlier);
        Other(Path.Combine(Plugins, "LianLi", "FanControl.LianLi.dll"), Newer, hasGuard: true, Earlier);
        _files.Move(OldPath, Removed(OldPath));

        Assert.False(Activate());

        Assert.False(_claim.Held);
        Assert.Contains("is newer and takes over the fans", Assert.Single(_log.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderCopyThisCopyCouldNotRename_StillCountsAsRunning_OnceAnotherCopyRenamesIt() {
        string own = Path.Combine(Path.GetTempPath(), "lianli-guard", "harness", "FanControl.LianLi.dll");
        _scanner.Own = new PluginCopy(own, Current, hasGuard: true);
        Other(OldPath, Older, hasGuard: false, Earlier);
        Assert.False(Activate());

        _files.Move(OldPath, Removed(OldPath));

        Assert.False(Activate());
        Assert.False(_claim.Held);
        Assert.Contains("may still be driving the fans", _log.Messages[_log.Messages.Count - 1], StringComparison.Ordinal);

        // The pass after it found the file gone, FanControl no longer created it.
        Assert.True(Activate());
    }

    [Fact]
    public void AnOlderCopyWhoseFileIsAlreadyGone_KeepsThisOneIdleForOnePassOnly() {
        _scanner.Copies.Add(new PluginCopy(OldPath, Older, hasGuard: false));

        Assert.False(Activate());
        Assert.DoesNotContain("could not be removed", _log.Messages[0], StringComparison.Ordinal);
        Assert.True(Activate());
    }

    [Fact]
    public void ADrivingCopyWhoseOwnFileIsAlreadyGone_AsksForOneRefreshPerProcess() {
        // Renamed by a winner scanned first, before this copy took the claim: one refresh hands the
        // winner the fans. Asked only once, since the same build loaded from a second path would
        // otherwise find its file gone on every pass.
        _files.Delete(OwnPath);
        Other(Path.Combine(Plugins, "LianLi", "FanControl.LianLi.dll"), Newer, hasGuard: true, Earlier);

        Assert.True(Activate());
        Assert.True(_guard.OwnFileRemoved());
        Assert.False(_guard.OwnFileRemoved());

        Assert.True(Activate());
        Assert.False(_guard.OwnFileRemoved());
    }

    [Fact]
    public void ARefreshWantedForAGoneFile_IsDropped_WhenThisCopyGoesIdle() {
        _files.Delete(OwnPath);
        Other(Path.Combine(Plugins, "LianLi", "FanControl.LianLi.dll"), Newer, hasGuard: true, Earlier);
        Assert.True(Activate());

        _claim.HeldElsewhere = true;
        _guard.Release(_owner);
        Assert.False(Activate());

        Assert.False(_guard.OwnFileRemoved());
    }

    [Fact]
    public void ALoserWhoseRenameIsRefused_KeepsTheFans_AndTheWinnerSaysWhichFileToDelete() {
        // The older copy starts first on every refresh, so it holds the claim when the winner starts.
        string loser = Path.Combine(Plugins, "LianLi", "FanControl.LianLi.dll");
        Other(loser, Older, hasGuard: true, Earlier);
        _files.MoveFailure = new UnauthorizedAccessException("Access to the path is denied.");
        _claim.HeldElsewhere = true;

        Assert.False(Activate());

        Assert.Equal(2, _log.Messages.Count);
        Assert.Contains("could not remove the other copy of the plugin", _log.Messages[0], StringComparison.Ordinal);
        Assert.Contains("the copy at " + loser + " (version 1.1.39) is driving the fans and could not be removed. Close FanControl, delete that file", _log.Messages[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherCopyInMemoryHoldingTheClaim_CannotBeRemoved_AndTheWinnerSaysSo() {
        _scanner.Copies.Add(new PluginCopy(string.Empty, Older, hasGuard: true));
        _claim.HeldElsewhere = true;

        Assert.False(Activate());
        Assert.Contains("is driving the fans and could not be removed", _log.Messages[_log.Messages.Count - 1], StringComparison.Ordinal);
    }

    // FanControl's passes over a Plugins folder holding an old hand-copied Lighting build, into which
    // the Standard build and then the Lighting build are installed with the button in one session.
    // Each pass closes the previous instances, then creates a plugin for each file still there, in
    // the order it finds them, and each copy's assembly stays loaded once it has been.
    [Fact]
    public void SequentialInstallsInOneSession_EndWithTheLastInstallDrivingTheFans() {
        string old = Path.Combine(Plugins, "FanControl.LianLi.Lighting.dll");
        string standard = Path.Combine(Plugins, "LianLi", "FanControl.LianLi.dll");
        string lighting = Path.Combine(Plugins, "LianLi-Lighting", "FanControl.LianLi-Lighting.dll");
        var files = new FakePluginFileSystem();
        var slot = new FakePluginCopyClaim.Slot();
        var oldCopy = new PluginCopy(old, Older, hasGuard: false);
        var standardCopy = new PluginCopy(standard, Current, hasGuard: true);
        var lightingCopy = new PluginCopy(lighting, Current, hasGuard: true);
        var standardScanner = new FakePluginCopyScanner(standardCopy);
        var lightingScanner = new FakePluginCopyScanner(lightingCopy);
        var standardGuard = new PluginCopyGuard(standardScanner, files, new FakePluginCopyClaim(slot));
        var lightingGuard = new PluginCopyGuard(lightingScanner, files, new FakePluginCopyClaim(slot));
        files.Add(old, Earlier);

        // Standard installed: the old copy is created first, then Standard, which renames it and waits.
        files.Add(standard, Earlier);
        standardScanner.Copies.Add(oldCopy);
        object standardFirst = new object();
        Assert.False(standardGuard.TryActivate(standardFirst, _log));
        Assert.False(files.Exists(old));

        // The next refresh no longer creates the old copy: Standard drives.
        object standardSecond = new object();
        Assert.True(standardGuard.TryActivate(standardSecond, _log));

        // Lighting installed, same version, later: the refresh closes Standard, then creates
        // Standard and Lighting in that order. Standard drives until Lighting starts; Lighting finds
        // the old copy's file gone for the first time, so it may still be running this pass, renames
        // Standard and waits, without asking anyone to delete a file that is not there.
        files.Add(lighting, Later);
        standardScanner.Copies.Add(lightingCopy);
        lightingScanner.Copies.Add(oldCopy);
        lightingScanner.Copies.Add(standardCopy);
        standardGuard.Release(standardSecond);
        object standardThird = new object();
        Assert.True(standardGuard.TryActivate(standardThird, _log));
        object lightingFirst = new object();
        Assert.False(lightingGuard.TryActivate(lightingFirst, _log));
        Assert.False(files.Exists(standard));
        Assert.DoesNotContain(_log.Messages, message => message.Contains("could not be removed", StringComparison.Ordinal));

        // Standard sees its file gone and asks for the refresh that hands Lighting the fans.
        Assert.True(standardGuard.OwnFileRemoved());

        // That refresh closes Standard and creates only Lighting, which now drives.
        standardGuard.Release(standardThird);
        Assert.True(lightingGuard.TryActivate(new object(), _log));
        Assert.Equal(new[] { old + " -> " + Removed(old), standard + " -> " + Removed(standard) }, files.Moves);
    }
}

