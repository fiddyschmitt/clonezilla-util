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
        [TestMethod] public void LeadingSeparator_Ignored() => Assert.IsTrue(Match(@"\Windows\Logs\CBS.log", Id));
        [TestMethod] public void LeadingForwardSlash_Ignored() => Assert.IsTrue(Match("/Windows/Logs/CBS.log", Id));
        [TestMethod] public void TrailingSeparator_MeansEverythingUnder() => Assert.IsTrue(Match(@"Windows\Logs\", Id));
        [TestMethod] public void TrailingSeparator_StillScoped() => Assert.IsFalse(Match(@"Program Files\", Id));

        [TestMethod]
        public void CaseInsensitive_UnderTurkishCulture()
        {
            // under tr-TR "I" folds to dotless "ı", so culture-sensitive IgnoreCase made *.LOG miss cbs.log (gh review F34)
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
                Assert.IsTrue(new PathGlobFilter(new[] { "*.LOG" }, null).Matches(@"c\sda2\Windows\Logs\cbs.log"));
                Assert.IsTrue(new PathGlobFilter(new[] { "*.ini" }, null).Matches(@"c\sda2\BOOT.INI"));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = previous;
            }
        }

        [TestMethod]
        public void UnmatchedIncludes_ReportsOnlyPatternsThatMatchedNothing()
        {
            var f = new PathGlobFilter(new[] { "*.log", "*.nosuch" }, null);
            f.Matches(Id);
            f.Matches(@"c\sda2\Program\app.exe");
            CollectionAssert.AreEqual(new[] { "*.nosuch" }, f.UnmatchedIncludes().ToArray());
        }

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
