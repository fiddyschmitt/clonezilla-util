using libCommon;

namespace clonezilla_util_tests.Extract
{
    // Fast, in-process tests for the extract verb's include/exclude matcher. No exe, no test assets.
    [TestClass]
    public class PathGlobFilterTests
    {
        // a file's identity, exactly as the `list` verb prints it: container\partition\path
        const string Id = @"2022-07-16-22-img_pb-devops1_bzip2\sda2\Windows\Logs\CBS.log";

        static bool Match(string? include, string id, string? exclude = null)
        {
            var inc = include == null ? Array.Empty<string>() : new[] { include };
            var exc = exclude == null ? Array.Empty<string>() : new[] { exclude };
            return new PathGlobFilter(inc, exc).Matches(id);
        }

        [TestMethod] public void FilenameGlob_MatchesAnywhere() => Assert.IsTrue(Match("*.log", Id));
        [TestMethod] public void FilenameGlob_NonMatch() => Assert.IsFalse(Match("*.xml", Id));
        [TestMethod] public void ScopedPath_MatchesAtAnyDepth() => Assert.IsTrue(Match(@"Windows\Logs\*.log", Id));
        [TestMethod] public void PartitionQualifiedPath_Matches() => Assert.IsTrue(Match(@"sda2\Windows\Logs\CBS.log", Id));
        [TestMethod] public void FullListLine_Matches() => Assert.IsTrue(Match(Id, Id));
        [TestMethod] public void ForwardSlashes_Normalised() => Assert.IsTrue(Match("Windows/Logs/*.log", Id));
        [TestMethod] public void BareName_MatchesOnSegmentBoundary() => Assert.IsTrue(Match("CBS.log", Id));
        [TestMethod] public void BareName_DoesNotMatchMidSegment() => Assert.IsFalse(Match("BS.log", Id));
        [TestMethod] public void CaseInsensitive() => Assert.IsTrue(Match("cbs.LOG", Id));
        [TestMethod] public void EmptyInclude_MatchesEverything() => Assert.IsTrue(Match(null, Id));
        [TestMethod] public void QuestionMark_MatchesOneChar() => Assert.IsTrue(Match("CB?.log", Id));
        [TestMethod] public void QuestionMark_IsExactlyOneChar() => Assert.IsFalse(Match("C?.log", Id));

        [TestMethod]
        public void ScopedGlob_DoesNotMatchOtherDirectory()
        {
            var f = new PathGlobFilter(new[] { @"Windows\System32\*.dll" }, Array.Empty<string>());
            Assert.IsTrue(f.Matches(@"c\sda2\Windows\System32\hal.dll"));
            Assert.IsTrue(f.Matches(@"c\sda2\Windows\System32\drivers\usb.dll"), "should match at any depth under System32");
            Assert.IsFalse(f.Matches(@"c\sda2\Windows\Other\hal.dll"));
        }

        [TestMethod]
        public void Exclude_BeatsInclude()
        {
            Assert.IsFalse(Match("*.log", Id, exclude: "*.log"));
        }

        [TestMethod]
        public void Exclude_IsScopedByDirectory()
        {
            var f = new PathGlobFilter(new[] { "*.log" }, new[] { @"Windows\Logs\*" });
            Assert.IsFalse(f.Matches(Id), "a .log under Windows\\Logs is excluded");
            Assert.IsTrue(f.Matches(@"c\sda2\Program\app.log"), "a .log elsewhere survives");
        }
    }
}
