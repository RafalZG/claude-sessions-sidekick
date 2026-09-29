using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick.Tests.Services;

public class FocusedSessionResolverTests
{
    private static SessionTokenData MakeSession(
        string id
        , string? cwd = null
        , string? slug = null
        , string? customName = null
        , string projectName = ""
        , int minutesAgo = 0
        , string? firstMessage = null)
    {
        var session = new SessionTokenData
        {
            SessionId = id,
            FilePath = $@"C:\fake\{id}.jsonl",
            Cwd = cwd,
            Slug = slug,
            CustomName = customName,
            ProjectName = projectName,
            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
            FirstMessage = firstMessage,
        };
        return session;
    }

    // ── ExtractResumeSessionId ─────────────────────────────────────

    public class ExtractResumeSessionIdTests
    {
        [Theory]
        [InlineData(@"node cli.js --resume 11111111-2222-3333-4444-555555555555",
                    "11111111-2222-3333-4444-555555555555")]
        [InlineData(@"""C:\node.exe"" ""cli.js"" --resume ""aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"" --model opus",
                    "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
        [InlineData(@"node cli.js --resume 'AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE'",
                    "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE")]
        [InlineData(@"node cli.js --resume=11111111-2222-3333-4444-555555555555",
                    "11111111-2222-3333-4444-555555555555")]
        public void ReturnsSessionId_WhenResumeArgumentPresent(string commandLine, string expected)
        {
            // Act
            var id = FocusedSessionResolver.ExtractResumeSessionId(commandLine);

            // Assert
            Assert.Equal(expected, id);
        }

        [Theory]
        [InlineData(@"node cli.js")]
        [InlineData(@"node cli.js --model opus")]
        [InlineData(@"node cli.js --resume")]
        [InlineData(@"node cli.js --resume not-a-guid")]
        [InlineData("")]
        public void ReturnsNull_WhenNoResumeId(string commandLine)
        {
            // Act
            var id = FocusedSessionResolver.ExtractResumeSessionId(commandLine);

            // Assert
            Assert.Null(id);
        }
    }

    // ── IsClaudeCliProcess ─────────────────────────────────────────

    public class IsClaudeCliProcessTests
    {
        [Theory]
        // npm install: node.exe running the CLI script
        [InlineData("node.exe", @"node C:\npm\@anthropic-ai\claude-code\cli.js --resume abc", true)]
        [InlineData("NODE.EXE", @"node claude-code/cli.js", true)]
        // native launcher (winget): claude.exe, with or without arguments
        [InlineData("claude.exe", @"""C:\WinGet\...\claude.exe""  --resume 11111111-2222-3333-4444-555555555555", true)]
        [InlineData("claude.exe", @"""C:\WinGet\...\claude.exe""", true)]
        [InlineData("claude.exe", null, true)]
        // Claude DESKTOP app helper processes carry --type=
        [InlineData("claude.exe", @"""C:\AnthropicClaude\claude.exe"" --type=renderer --field-trial…", false)]
        [InlineData("claude.exe", @"""C:\AnthropicClaude\claude.exe"" --type=gpu-process", false)]
        // plain node without the CLI script is an MCP server or anything else
        [InlineData("node.exe", @"node C:\XGGMCPServers\SomeMCP\dist\index.js", false)]
        [InlineData("node.exe", null, false)]
        // unrelated processes
        [InlineData("cmd.exe", @"cmd.exe", false)]
        [InlineData("powershell.exe", null, false)]
        public void RecognizesClaudeCliFlavors(string exeName, string? commandLine, bool expected)
        {
            // Act
            var result = FocusedSessionResolver.IsClaudeCliProcess(exeName, commandLine);

            // Assert
            Assert.Equal(expected, result);
        }
    }

    // ── IsAncestor ─────────────────────────────────────────────────

    public class IsAncestorTests
    {
        [Fact]
        public void FindsDirectParent()
        {
            // Arrange — terminal(100) -> shell(200) -> node(300)
            var parentOf = new Dictionary<int, int> { [300] = 200, [200] = 100, [100] = 4 };

            // Act & Assert
            Assert.True(FocusedSessionResolver.IsAncestor(200, 300, parentOf));
        }

        [Fact]
        public void FindsGrandparentThroughChain()
        {
            // Arrange
            var parentOf = new Dictionary<int, int> { [300] = 200, [200] = 100, [100] = 4 };

            // Act & Assert
            Assert.True(FocusedSessionResolver.IsAncestor(100, 300, parentOf));
        }

        [Fact]
        public void ReturnsFalse_ForUnrelatedProcess()
        {
            // Arrange — 300 hangs under 100, 999 is elsewhere
            var parentOf = new Dictionary<int, int> { [300] = 200, [200] = 100, [999] = 4 };

            // Act & Assert
            Assert.False(FocusedSessionResolver.IsAncestor(999, 300, parentOf));
        }

        [Fact]
        public void ReturnsFalse_WhenPidMissingFromSnapshot()
        {
            // Arrange
            var parentOf = new Dictionary<int, int> { [200] = 100 };

            // Act & Assert
            Assert.False(FocusedSessionResolver.IsAncestor(100, 12345, parentOf));
        }

        [Fact]
        public void SurvivesParentLoop_FromPidReuse()
        {
            // Arrange — stale snapshot where 200 and 300 point at each other
            var parentOf = new Dictionary<int, int> { [300] = 200, [200] = 300 };

            // Act & Assert — must terminate and say no
            Assert.False(FocusedSessionResolver.IsAncestor(100, 300, parentOf));
        }

        [Fact]
        public void SelfParentDoesNotLoopForever()
        {
            // Arrange
            var parentOf = new Dictionary<int, int> { [300] = 300 };

            // Act & Assert
            Assert.False(FocusedSessionResolver.IsAncestor(100, 300, parentOf));
        }
    }

    // ── MatchProcessToSession ──────────────────────────────────────

    public class MatchProcessToSessionTests
    {
        [Fact]
        public void ResumeId_WinsOverWorkingDirectory()
        {
            // Arrange — process resumed session B but sits in project A's folder
            var sessionA = MakeSession("aaaaaaaa-1111-4111-8111-111111111111", cwd: @"D:\projA");
            var sessionB = MakeSession("bbbbbbbb-2222-4222-8222-222222222222", cwd: @"D:\projB");
            var sessions = new List<SessionTokenData> { sessionA, sessionB };
            var proc = new FocusedClaudeProcess(300,
                @"node cli.js --resume bbbbbbbb-2222-4222-8222-222222222222", @"D:\projA");

            // Act
            var matched = FocusedSessionResolver.MatchProcessToSession(sessions, proc);

            // Assert
            Assert.Same(sessionB, matched);
        }

        [Fact]
        public void MatchesByWorkingDirectory_CaseAndTrailingSlashInsensitive()
        {
            // Arrange
            var session = MakeSession("aaaaaaaa-1111-4111-8111-111111111111", cwd: @"D:\Projects\App");
            var sessions = new List<SessionTokenData> { session };
            var proc = new FocusedClaudeProcess(300, "node cli.js", @"d:\projects\app\");

            // Act
            var matched = FocusedSessionResolver.MatchProcessToSession(sessions, proc);

            // Assert
            Assert.Same(session, matched);
        }

        [Fact]
        public void SameFolderTwice_PicksNewestSession()
        {
            // Arrange — two sessions of the same project; the fresh one wins
            var older = MakeSession("aaaaaaaa-1111-4111-8111-111111111111", cwd: @"D:\proj", minutesAgo: 60);
            var newer = MakeSession("bbbbbbbb-2222-4222-8222-222222222222", cwd: @"D:\proj", minutesAgo: 1);
            var sessions = new List<SessionTokenData> { older, newer };
            var proc = new FocusedClaudeProcess(300, "node cli.js", @"D:\proj");

            // Act
            var matched = FocusedSessionResolver.MatchProcessToSession(sessions, proc);

            // Assert
            Assert.Same(newer, matched);
        }

        [Fact]
        public void ReturnsNull_WhenNothingMatches()
        {
            // Arrange
            var session = MakeSession("aaaaaaaa-1111-4111-8111-111111111111", cwd: @"D:\projA");
            var sessions = new List<SessionTokenData> { session };
            var proc = new FocusedClaudeProcess(300, "node cli.js", @"D:\other");

            // Act
            var matched = FocusedSessionResolver.MatchProcessToSession(sessions, proc);

            // Assert
            Assert.Null(matched);
        }

        [Fact]
        public void ReturnsNull_WhenProcessHasNoCwdAndNoResumeId()
        {
            // Arrange
            var session = MakeSession("aaaaaaaa-1111-4111-8111-111111111111", cwd: @"D:\projA");
            var sessions = new List<SessionTokenData> { session };
            var proc = new FocusedClaudeProcess(300, "node cli.js", null);

            // Act
            var matched = FocusedSessionResolver.MatchProcessToSession(sessions, proc);

            // Assert
            Assert.Null(matched);
        }

        [Fact]
        public void UnknownResumeId_FallsBackToWorkingDirectory()
        {
            // Arrange — resumed session isn't tracked (e.g. older than the
            // watcher's window) but the folder still identifies the project
            var session = MakeSession("aaaaaaaa-1111-4111-8111-111111111111", cwd: @"D:\projA");
            var sessions = new List<SessionTokenData> { session };
            var proc = new FocusedClaudeProcess(300,
                @"node cli.js --resume 99999999-9999-4999-8999-999999999999", @"D:\projA");

            // Act
            var matched = FocusedSessionResolver.MatchProcessToSession(sessions, proc);

            // Assert
            Assert.Same(session, matched);
        }
    }

    // ── Resolve ────────────────────────────────────────────────────

    public class ResolveTests
    {
        [Fact]
        public void SingleMatch_ReturnsThatSession_EvenWhenAnotherIsNewer()
        {
            // Arrange — the exact bug from issue #2/#3: the OTHER window's
            // session is newer, but focus decides
            var focusedSession = MakeSession("aaaaaaaa-1111-4111-8111-111111111111",
                cwd: @"D:\projA", minutesAgo: 30);
            var newerElsewhere = MakeSession("bbbbbbbb-2222-4222-8222-222222222222",
                cwd: @"D:\projB", minutesAgo: 1);
            var sessions = new List<SessionTokenData> { focusedSession, newerElsewhere };
            var procs = new List<FocusedClaudeProcess>
            {
                new(300, "node cli.js", @"D:\projA"),
            };

            // Act
            var resolved = FocusedSessionResolver.Resolve(sessions, procs, windowTitle: null);

            // Assert
            Assert.Same(focusedSession, resolved);
        }

        [Fact]
        public void MultipleMatches_WindowTitlePicksTheVisibleTab()
        {
            // Arrange — two Claude tabs under one Windows Terminal window;
            // the title shows the active tab's session slug
            var tabA = MakeSession("aaaaaaaa-1111-4111-8111-111111111111",
                cwd: @"D:\projA", slug: "fix-login-bug", minutesAgo: 1);
            var tabB = MakeSession("bbbbbbbb-2222-4222-8222-222222222222",
                cwd: @"D:\projB", slug: "write-report", minutesAgo: 30);
            var sessions = new List<SessionTokenData> { tabA, tabB };
            var procs = new List<FocusedClaudeProcess>
            {
                new(300, "node cli.js", @"D:\projA"),
                new(400, "node cli.js", @"D:\projB"),
            };

            // Act — title mentions the OLDER session; it must win anyway
            var resolved = FocusedSessionResolver.Resolve(sessions, procs, "✳ write-report — Terminal");

            // Assert
            Assert.Same(tabB, resolved);
        }

        [Fact]
        public void MultipleMatches_CustomNameInTitleAlsoCounts()
        {
            // Arrange
            var tabA = MakeSession("aaaaaaaa-1111-4111-8111-111111111111",
                cwd: @"D:\projA", customName: "Moja sesja", minutesAgo: 30);
            var tabB = MakeSession("bbbbbbbb-2222-4222-8222-222222222222",
                cwd: @"D:\projB", minutesAgo: 1);
            var sessions = new List<SessionTokenData> { tabA, tabB };
            var procs = new List<FocusedClaudeProcess>
            {
                new(300, "node cli.js", @"D:\projA"),
                new(400, "node cli.js", @"D:\projB"),
            };

            // Act
            var resolved = FocusedSessionResolver.Resolve(sessions, procs, "moja sesja");

            // Assert
            Assert.Same(tabA, resolved);
        }

        [Fact]
        public void MultipleMatches_UnhelpfulTitle_FallsBackToNewestOfMatched()
        {
            // Arrange
            var tabA = MakeSession("aaaaaaaa-1111-4111-8111-111111111111",
                cwd: @"D:\projA", slug: "alpha", minutesAgo: 30);
            var tabB = MakeSession("bbbbbbbb-2222-4222-8222-222222222222",
                cwd: @"D:\projB", slug: "beta", minutesAgo: 5);
            var sessions = new List<SessionTokenData> { tabA, tabB };
            var procs = new List<FocusedClaudeProcess>
            {
                new(300, "node cli.js", @"D:\projA"),
                new(400, "node cli.js", @"D:\projB"),
            };

            // Act
            var resolved = FocusedSessionResolver.Resolve(sessions, procs, "Windows Terminal");

            // Assert
            Assert.Same(tabB, resolved);
        }

        [Fact]
        public void NoProcesses_ReturnsNull()
        {
            // Arrange
            var sessions = new List<SessionTokenData>
            {
                MakeSession("aaaaaaaa-1111-4111-8111-111111111111", cwd: @"D:\projA"),
            };

            // Act
            var resolved = FocusedSessionResolver.Resolve(sessions, [], "title");

            // Assert
            Assert.Null(resolved);
        }

        [Fact]
        public void NoSessions_ReturnsNull()
        {
            // Arrange
            var procs = new List<FocusedClaudeProcess> { new(300, "node cli.js", @"D:\projA") };

            // Act
            var resolved = FocusedSessionResolver.Resolve([], procs, "title");

            // Assert
            Assert.Null(resolved);
        }

        [Fact]
        public void ProcessesThatMatchNothing_ReturnNull()
        {
            // Arrange — focused terminal runs Claude in an untracked folder
            var sessions = new List<SessionTokenData>
            {
                MakeSession("aaaaaaaa-1111-4111-8111-111111111111", cwd: @"D:\projA"),
            };
            var procs = new List<FocusedClaudeProcess> { new(300, "node cli.js", @"D:\somewhere-else") };

            // Act
            var resolved = FocusedSessionResolver.Resolve(sessions, procs, null);

            // Assert
            Assert.Null(resolved);
        }

        [Fact]
        public void TaskDescriptionTitle_MatchesByWordOverlapWithFirstMessage()
        {
            // Arrange — MonitorMike's scenario: two Claude windows share one
            // WindowsTerminal.exe process, and the focused window's title is a
            // task description written by Claude, not the session's slug. The
            // FOCUSED session is older; substring matching finds nothing, so
            // before the word-overlap pass the newer session wrongly won.
            var focused = MakeSession("aaaaaaaa-1111-4111-8111-111111111111",
                cwd: @"D:\repoA", slug: "keen-mixing-origami", minutesAgo: 30,
                firstMessage: "please review and investigate the flaky integration tests in CI");
            var newerOther = MakeSession("bbbbbbbb-2222-4222-8222-222222222222",
                cwd: @"D:\repoB", slug: "brave-jumping-lizard", minutesAgo: 1,
                firstMessage: "add sequence numbers to the export list");
            var sessions = new List<SessionTokenData> { focused, newerOther };
            var procs = new List<FocusedClaudeProcess>
            {
                new(100, "node cli.js", @"D:\repoA"),
                new(200, "node cli.js", @"D:\repoB"),
            };

            // Act
            var resolved = FocusedSessionResolver.Resolve(
                sessions, procs, "Review and investigate flaky integration tests");

            // Assert
            Assert.Same(focused, resolved);
        }

        [Fact]
        public void SubstringMatch_BeatsWordOverlap()
        {
            // Arrange — one session's slug appears verbatim in the title; the
            // other only shares words. The exact match must win.
            var bySlug = MakeSession("aaaaaaaa-1111-4111-8111-111111111111",
                cwd: @"D:\repoA", slug: "review-tests", minutesAgo: 30);
            var byWords = MakeSession("bbbbbbbb-2222-4222-8222-222222222222",
                cwd: @"D:\repoB", slug: "other-slug", minutesAgo: 1,
                firstMessage: "review the tests carefully");
            var sessions = new List<SessionTokenData> { bySlug, byWords };
            var procs = new List<FocusedClaudeProcess>
            {
                new(100, "node cli.js", @"D:\repoA"),
                new(200, "node cli.js", @"D:\repoB"),
            };

            // Act
            var resolved = FocusedSessionResolver.Resolve(sessions, procs, "✳ review-tests");

            // Assert
            Assert.Same(bySlug, resolved);
        }

        [Fact]
        public void NoWordOverlapAtAll_FallsBackToNewest()
        {
            // Arrange
            var older = MakeSession("aaaaaaaa-1111-4111-8111-111111111111",
                cwd: @"D:\repoA", slug: "slug-one", minutesAgo: 30, firstMessage: "first thing");
            var newer = MakeSession("bbbbbbbb-2222-4222-8222-222222222222",
                cwd: @"D:\repoB", slug: "slug-two", minutesAgo: 1, firstMessage: "second thing");
            var sessions = new List<SessionTokenData> { older, newer };
            var procs = new List<FocusedClaudeProcess>
            {
                new(100, "node cli.js", @"D:\repoA"),
                new(200, "node cli.js", @"D:\repoB"),
            };

            // Act
            var resolved = FocusedSessionResolver.Resolve(sessions, procs, "Windows PowerShell");

            // Assert
            Assert.Same(newer, resolved);
        }

        [Fact]
        public void EqualWordOverlap_FallsBackToNewest()
        {
            // Arrange — both first messages share the same words with the title.
            var older = MakeSession("aaaaaaaa-1111-4111-8111-111111111111",
                cwd: @"D:\repoA", minutesAgo: 30, firstMessage: "deploy the payment service");
            var newer = MakeSession("bbbbbbbb-2222-4222-8222-222222222222",
                cwd: @"D:\repoB", minutesAgo: 1, firstMessage: "deploy the payment gateway");
            var sessions = new List<SessionTokenData> { older, newer };
            var procs = new List<FocusedClaudeProcess>
            {
                new(100, "node cli.js", @"D:\repoA"),
                new(200, "node cli.js", @"D:\repoB"),
            };

            // Act
            var resolved = FocusedSessionResolver.Resolve(sessions, procs, "deploy payment");

            // Assert
            Assert.Same(newer, resolved);
        }

        [Fact]
        public void TwoProcessesSameSession_CollapseToOneCandidate()
        {
            // Arrange — duplicate matches must not trip the "ambiguous" path
            var session = MakeSession("aaaaaaaa-1111-4111-8111-111111111111", cwd: @"D:\projA");
            var sessions = new List<SessionTokenData> { session };
            var procs = new List<FocusedClaudeProcess>
            {
                new(300, "node cli.js", @"D:\projA"),
                new(400, @"node cli.js --resume aaaaaaaa-1111-4111-8111-111111111111", null),
            };

            // Act
            var resolved = FocusedSessionResolver.Resolve(sessions, procs, null);

            // Assert
            Assert.Same(session, resolved);
        }
    }

    // ── MatchOpenSessions (restore snapshot) ───────────────────────

    public class MatchOpenSessionsTests
    {
        [Fact]
        public void ResumeId_ClaimsExactSession()
        {
            // Arrange
            var sessionA = MakeSession("aaaaaaaa-1111-4111-8111-111111111111", cwd: @"D:\projA");
            var sessionB = MakeSession("bbbbbbbb-2222-4222-8222-222222222222", cwd: @"D:\projB");
            var sessions = new List<SessionTokenData> { sessionA, sessionB };
            var procs = new List<FocusedClaudeProcess>
            {
                new(100, @"node cli.js --resume bbbbbbbb-2222-4222-8222-222222222222", null),
            };

            // Act
            var result = FocusedSessionResolver.MatchOpenSessions(sessions, procs);

            // Assert
            var matched = Assert.Single(result.Matched);
            Assert.Same(sessionB, matched);
            Assert.Empty(result.Unmatched);
        }

        [Fact]
        public void TwoProcessesInSameFolder_ClaimTwoDifferentSessions()
        {
            // Arrange — the "5 open, restored 3" bug: two terminals in one project
            // must count as two sessions, not collapse into one.
            var older = MakeSession("aaaaaaaa-1111-4111-8111-111111111111", cwd: @"D:\proj", minutesAgo: 300);
            var newer = MakeSession("bbbbbbbb-2222-4222-8222-222222222222", cwd: @"D:\proj", minutesAgo: 1);
            var unrelated = MakeSession("cccccccc-3333-4333-8333-333333333333", cwd: @"D:\other", minutesAgo: 5);
            var sessions = new List<SessionTokenData> { older, newer, unrelated };
            var procs = new List<FocusedClaudeProcess>
            {
                new(100, "node cli.js", @"D:\proj"),
                new(200, "node cli.js", @"D:\proj\"),
            };

            // Act
            var result = FocusedSessionResolver.MatchOpenSessions(sessions, procs);

            // Assert — both sessions of that folder, nothing from the other one.
            Assert.Equal(2, result.Matched.Count);
            Assert.Contains(newer, result.Matched);
            Assert.Contains(older, result.Matched);
            Assert.Empty(result.Unmatched);
        }

        [Fact]
        public void IdleOpenSession_StaysInSnapshot_EvenWhenNewerClosedSessionExists()
        {
            // Arrange — an open-but-idle session must beat a recently CLOSED one.
            var openButIdle = MakeSession("aaaaaaaa-1111-4111-8111-111111111111", cwd: @"D:\proj", minutesAgo: 480);
            var closedRecently = MakeSession("bbbbbbbb-2222-4222-8222-222222222222", cwd: @"D:\other", minutesAgo: 2);
            var sessions = new List<SessionTokenData> { closedRecently, openButIdle };
            var procs = new List<FocusedClaudeProcess>
            {
                new(100, "node cli.js", @"D:\proj"),
            };

            // Act
            var result = FocusedSessionResolver.MatchOpenSessions(sessions, procs);

            // Assert
            var matched = Assert.Single(result.Matched);
            Assert.Same(openButIdle, matched);
        }

        [Fact]
        public void ResumeProcess_ClaimsFirst_SoCwdProcessTakesNextNewest()
        {
            // Arrange — the resume claim wins even when the plain-cwd process
            // comes first in the list.
            var newest = MakeSession("aaaaaaaa-1111-4111-8111-111111111111", cwd: @"D:\proj", minutesAgo: 1);
            var second = MakeSession("bbbbbbbb-2222-4222-8222-222222222222", cwd: @"D:\proj", minutesAgo: 10);
            var sessions = new List<SessionTokenData> { newest, second };
            var procs = new List<FocusedClaudeProcess>
            {
                new(100, "node cli.js", @"D:\proj"),
                new(200, @"node cli.js --resume aaaaaaaa-1111-4111-8111-111111111111", @"D:\proj"),
            };

            // Act
            var result = FocusedSessionResolver.MatchOpenSessions(sessions, procs);

            // Assert — no session claimed twice, both processes got one each.
            Assert.Equal(2, result.Matched.Count);
            Assert.Contains(newest, result.Matched);
            Assert.Contains(second, result.Matched);
        }

        [Fact]
        public void UnknownResumeId_DoesNotStealFolderNewestSession()
        {
            // Arrange — the process resumes a session the tracker doesn't know;
            // it must NOT grab the folder's newest session, which belongs to
            // the other terminal.
            var tracked = MakeSession("aaaaaaaa-1111-4111-8111-111111111111", cwd: @"D:\proj", minutesAgo: 1);
            var sessions = new List<SessionTokenData> { tracked };
            var resumeProc = new FocusedClaudeProcess(
                100, @"node cli.js --resume 99999999-9999-4999-8999-999999999999", @"D:\proj");
            var cwdProc = new FocusedClaudeProcess(200, "node cli.js", @"D:\proj");
            var procs = new List<FocusedClaudeProcess> { resumeProc, cwdProc };

            // Act
            var result = FocusedSessionResolver.MatchOpenSessions(sessions, procs);

            // Assert — cwd process keeps the tracked session, resume process is unmatched.
            var matched = Assert.Single(result.Matched);
            Assert.Same(tracked, matched);
            var unmatched = Assert.Single(result.Unmatched);
            Assert.Same(resumeProc, unmatched);
        }

        [Fact]
        public void ForwardSlashCwd_MatchesBackslashSession()
        {
            // Arrange — git-bash style cwd vs. Windows-style session path.
            var session = MakeSession("aaaaaaaa-1111-4111-8111-111111111111", cwd: @"D:\proj\sub");
            var sessions = new List<SessionTokenData> { session };
            var procs = new List<FocusedClaudeProcess>
            {
                new(100, "node cli.js", "D:/proj/sub"),
            };

            // Act
            var result = FocusedSessionResolver.MatchOpenSessions(sessions, procs);

            // Assert
            var matched = Assert.Single(result.Matched);
            Assert.Same(session, matched);
        }

        [Fact]
        public void UnknownFolder_ReturnsProcessAsUnmatched()
        {
            // Arrange
            var session = MakeSession("aaaaaaaa-1111-4111-8111-111111111111", cwd: @"D:\projA");
            var sessions = new List<SessionTokenData> { session };
            var unknownProc = new FocusedClaudeProcess(100, "node cli.js", @"D:\somewhere-else");
            var procs = new List<FocusedClaudeProcess> { unknownProc };

            // Act
            var result = FocusedSessionResolver.MatchOpenSessions(sessions, procs);

            // Assert
            Assert.Empty(result.Matched);
            var unmatched = Assert.Single(result.Unmatched);
            Assert.Same(unknownProc, unmatched);
        }

        [Fact]
        public void MoreProcessesThanSessionsInFolder_LeftoverGoesToUnmatched()
        {
            // Arrange — three terminals in a folder the watcher knows one session of.
            var session = MakeSession("aaaaaaaa-1111-4111-8111-111111111111", cwd: @"D:\proj");
            var sessions = new List<SessionTokenData> { session };
            var procs = new List<FocusedClaudeProcess>
            {
                new(100, "node cli.js", @"D:\proj"),
                new(200, "node cli.js", @"D:\proj"),
                new(300, "node cli.js", @"D:\proj"),
            };

            // Act
            var result = FocusedSessionResolver.MatchOpenSessions(sessions, procs);

            // Assert
            var matched = Assert.Single(result.Matched);
            Assert.Same(session, matched);
            Assert.Equal(2, result.Unmatched.Count);
        }

        [Fact]
        public void ProcessWithoutCwdOrResume_GoesToUnmatched()
        {
            // Arrange
            var session = MakeSession("aaaaaaaa-1111-4111-8111-111111111111", cwd: @"D:\projA");
            var sessions = new List<SessionTokenData> { session };
            var procs = new List<FocusedClaudeProcess>
            {
                new(100, "node cli.js", null),
            };

            // Act
            var result = FocusedSessionResolver.MatchOpenSessions(sessions, procs);

            // Assert
            Assert.Empty(result.Matched);
            Assert.Single(result.Unmatched);
        }

        [Fact]
        public void NoProcesses_ReturnsEmptyResult()
        {
            // Arrange
            var sessions = new List<SessionTokenData>
            {
                MakeSession("aaaaaaaa-1111-4111-8111-111111111111", cwd: @"D:\projA"),
            };

            // Act
            var result = FocusedSessionResolver.MatchOpenSessions(sessions, []);

            // Assert
            Assert.Empty(result.Matched);
            Assert.Empty(result.Unmatched);
        }
    }
}
