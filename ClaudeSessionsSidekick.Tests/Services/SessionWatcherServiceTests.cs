using ClaudeSessionsSidekick.Services;
using Xunit;

namespace ClaudeSessionsSidekick.Tests.Services;

public class SessionWatcherServiceTests
{
    // ── ExtractProjectName ─────────────────────────────────────────
    //
    // Claude Code stores session JSONLs under
    //   ~\.claude\projects\<encoded-folder-key>\<session-id>.jsonl
    // where the encoded key replaces both the drive colon AND every
    // path separator AND every literal "-" in the original folder
    // path with the SAME character: a single "-". That encoding is
    // lossy by design (we can't tell a literal dash from a path
    // separator without consulting the filesystem), and the side-
    // effect is that two distinct real folders can collide on the
    // same encoded key. ExtractProjectName operates on the encoded
    // key alone, so when it sees a collision both sessions end up
    // sharing a ProjectName and the widget's "active session header"
    // can latch onto either one — the symptom Ewa reported.

    public class ExtractProjectNameTests
    {
        [Theory]
        [InlineData("D--src-Dev3", "src-Dev3")]
        [InlineData("C--Users-Ewa-MyApp", "Ewa-MyApp")]
        [InlineData("D--Projects-MyApp-Main", "MyApp-Main")]
        public void ExtractsLastTwoSegmentsFromEncodedKey(string encodedKey, string expected)
        {
            var path = $@"C:\Users\foo\.claude\projects\{encodedKey}\abc123.jsonl";
            Assert.Equal(expected, SessionWatcherService.ExtractProjectName(path));
        }

        [Theory]
        // Drive-letter-only key: split yields a single segment, so the
        // last-two branch is skipped and the raw key is returned as-is.
        [InlineData("D--", "D--")]
        // Drive + one path segment: the drive letter is dropped (single-
        // char letter is treated as a drive marker), leaving just "foo".
        [InlineData("D--foo", "foo")]
        public void DegradesGracefullyOnShortKeys(string encodedKey, string expected)
        {
            var path = $@"C:\Users\foo\.claude\projects\{encodedKey}\abc123.jsonl";
            Assert.Equal(expected, SessionWatcherService.ExtractProjectName(path));
        }

        /// <summary>
        /// Collision regression — locks in the project-key ambiguity
        /// behavior so we don't accidentally "fix" it in a way that
        /// silently breaks downstream callers. The proper fix is to
        /// stop deriving ProjectName from the encoded key alone and
        /// instead use the session's recorded Cwd (parsed from the
        /// JSONL itself, unambiguous). Until that ships, these two
        /// distinct real folders both surface as "cash-invoice" in
        /// the widget — the shape of Ewa's "active session header
        /// shows different session" complaint.
        /// </summary>
        [Fact]
        public void DistinctRealFolders_CanCollideOnSameProjectName()
        {
            // Real folder A: D:\Users\Ewa\cash-invoice
            //   → encoded key D--Users-Ewa-cash-invoice
            // Real folder B: D:\Users\Ewa\cash\invoice
            //   → encoded key D--Users-Ewa-cash-invoice
            // (Claude Code uses the same encoding for path separators
            //  and literal dashes — both folders map to the same key.)
            var keyA = "D--Users-Ewa-cash-invoice";
            var keyB = "D--Users-Ewa-cash-invoice";

            var nameA = SessionWatcherService.ExtractProjectName($@"X:\fake\{keyA}\a.jsonl");
            var nameB = SessionWatcherService.ExtractProjectName($@"X:\fake\{keyB}\b.jsonl");

            Assert.Equal(nameA, nameB);
            Assert.Equal("cash-invoice", nameA);
        }

        /// <summary>
        /// The dash-in-folder-name case in isolation: D:\foo-bar encodes
        /// to D--foo-bar, and the extractor returns "foo-bar" — looks
        /// fine to the user, but the same key would result from
        /// D:\foo\bar (literal path separator). Both routes are
        /// represented in the test above; this one documents that the
        /// dash-only case alone produces the expected visible name.
        /// </summary>
        [Fact]
        public void DashInFolderName_ExtractsVisibleName()
        {
            var path = @"X:\fake\D--foo-bar\session.jsonl";
            Assert.Equal("foo-bar", SessionWatcherService.ExtractProjectName(path));
        }
    }
}
