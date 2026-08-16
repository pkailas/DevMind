// File: MemoryManager.cs  v8.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DevMind
{
    /// <summary>
    /// Manages DevMind's cross-session memory system.
    /// Layer 1: MEMORY.md index file in the solution root (always loaded into system prompt).
    /// Layer 2: Repo topic files in .devmind/memory/ (loaded on demand via recall_memory).
    /// Layer 3 (machine-level): topic files in %APPDATA%\devmind\memory (see <see cref="DevMindPaths"/>).
    ///
    /// Layer 3 is READ-ONLY at the tool surface (save_memory still writes repo-only) and
    /// composes with Layer 2. Standing context is emitted global-first then repo, so the
    /// more specific repo rules sit last in the prompt and win on a genuine conflict —
    /// while nothing from either layer is silently dropped. A missing or unreadable global
    /// directory is a normal state (feature degrades to repo-only) and never throws.
    /// </summary>
    public sealed class MemoryManager
    {
        private const int IndexLineCap = 200;
        private const int MaxTopicSizeBytes = 50 * 1024; // 50 KB
        private const int MaxSlugLength = 50;

        /// <summary>
        /// Independent byte cap for the GLOBAL (machine-level) standing-context layer.
        /// Kept deliberately separate from the repo cap (default 24 KB) so a long global
        /// conventions file can NEVER starve a repo standing topic: the two layers budget
        /// against each other only through ordering, and the global layer trims its own
        /// topics (with its own omission marker) before a single byte of it can reduce
        /// what a repo topic gets. Tuning this to a config knob is intentionally deferred
        /// until someone actually needs to.
        /// </summary>
        public const int GlobalStandingContextMaxBytes = 8 * 1024;

        /// <summary>Prefix for explicitly recalling a machine-level (global) topic: "global:&lt;slug&gt;".</summary>
        public const string GlobalTopicPrefix = "global:";

        private static readonly string IndexHeader =
            "# DevMind Memory Index\n" +
            "<!-- Auto-managed by DevMind. Do not edit the entries below manually. -->\n\n";

        private readonly string _solutionRoot;
        private readonly string _indexPath;
        private readonly string _topicDir;
        private readonly string _globalTopicDir;

        /// <summary>
        /// Creates a MemoryManager for the given solution root directory.
        /// The machine-level (global) layer resolves to %APPDATA%\devmind\memory via
        /// <see cref="DevMindPaths"/>.
        /// </summary>
        public MemoryManager(string solutionRoot)
            : this(solutionRoot, DevMindPaths.GlobalMemoryDir) { }

        /// <summary>
        /// Creates a MemoryManager with an explicit machine-level memory directory.
        /// <paramref name="globalMemoryDir"/> is the test seam for unit tests that must
        /// not touch the real %APPDATA%\devmind\memory — production callers always use the
        /// single-argument constructor. A missing or unreadable global directory is a
        /// normal state: every global read degrades to empty and never throws.
        /// </summary>
        public MemoryManager(string solutionRoot, string globalMemoryDir)
        {
            _solutionRoot = solutionRoot ?? throw new ArgumentNullException(nameof(solutionRoot));
            _indexPath = Path.Combine(_solutionRoot, "MEMORY.md");
            _topicDir = Path.Combine(_solutionRoot, ".devmind", "memory");
            _globalTopicDir = globalMemoryDir ?? string.Empty;
        }

        // ── Layer 1: MEMORY.md ──────────────────────────────────────────────

        /// <summary>
        /// Returns the full content of MEMORY.md, or null if it doesn't exist.
        /// </summary>
        public string LoadIndex()
        {
            try
            {
                if (!File.Exists(_indexPath))
                    return null;
                return File.ReadAllText(_indexPath, Encoding.UTF8);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Adds or updates an entry in the MEMORY.md index.
        /// Creates MEMORY.md if it doesn't exist.
        /// Enforces the 200-line entry cap (oldest entries removed first).
        /// </summary>
        public void AddIndexEntry(string topicSlug, string description)
        {
            try
            {
                topicSlug = SanitizeSlug(topicSlug);
                if (string.IsNullOrEmpty(description))
                    description = topicSlug;

                var entries = LoadIndexEntries();

                // Remove existing entry for this slug (will be re-added at bottom)
                entries.RemoveAll(e =>
                    e.StartsWith($"- [{topicSlug}]", StringComparison.OrdinalIgnoreCase));

                // Add new entry at the end (newest at bottom)
                entries.Add($"- [{topicSlug}] {description}");

                // Enforce cap — remove oldest (top of list)
                while (entries.Count > IndexLineCap)
                    entries.RemoveAt(0);

                WriteIndex(entries);
            }
            catch
            {
                // Never crash the extension on memory file errors
            }
        }

        // ── Layer 2: Repo Topic Files ───────────────────────────────────────

        /// <summary>
        /// Returns the content of a repo topic file, or null if it doesn't exist.
        /// (Machine-level topics are read via <see cref="LoadGlobalTopic"/> /
        /// <see cref="RecallTopic"/>.)
        /// </summary>
        public string LoadTopic(string topicSlug) => LoadTopicIn(_topicDir, topicSlug);

        /// <summary>
        /// Writes or overwrites a repo topic file and updates the index.
        /// Creates the .devmind/memory/ directory if needed.
        /// NOTE: intentionally repo-only. The machine-level layer is read-only at the
        /// tool surface — see <see cref="RecallTopic"/>.
        /// </summary>
        public void SaveTopic(string topicSlug, string content, string description)
        {
            try
            {
                topicSlug = SanitizeSlug(topicSlug);

                if (!Directory.Exists(_topicDir))
                    Directory.CreateDirectory(_topicDir);

                // Cap content size
                if (content != null && Encoding.UTF8.GetByteCount(content) > MaxTopicSizeBytes)
                {
                    // Truncate to approximate limit
                    int charLimit = MaxTopicSizeBytes / 2; // rough UTF-8 estimate
                    if (content.Length > charLimit)
                        content = content.Substring(0, charLimit) + "\n\n[TRUNCATED — exceeded 50 KB limit]";
                }

                string path = Path.Combine(_topicDir, topicSlug + ".md");
                File.WriteAllText(path, content ?? "", Encoding.UTF8);

                // Update the index entry
                AddIndexEntry(topicSlug, description ?? topicSlug);
            }
            catch
            {
                // Never crash the extension on memory file errors
            }
        }

        // ── Layer 3: Machine-level (global) Topic Files — READ-ONLY ─────────

        /// <summary>
        /// Returns the content of a machine-level (global) topic file, or null if it
        /// doesn't exist. A missing global directory returns null, never throws.
        /// </summary>
        public string LoadGlobalTopic(string topicSlug) => LoadTopicIn(_globalTopicDir, topicSlug);

        /// <summary>
        /// Lists machine-level (global) topic slugs. Returns an empty list when the
        /// global directory does not exist (the common case) — never throws.
        /// </summary>
        public List<string> ListGlobalTopics() => ListTopicsIn(_globalTopicDir);

        /// <summary>Result of <see cref="RecallTopic"/>: content plus a cross-layer note.</summary>
        public sealed class RecallResult
        {
            /// <summary>The recalled topic's full content (the requested layer).</summary>
            public string Content { get; init; } = "";
            /// <summary>
            /// Null when no other layer holds a same-slug topic; otherwise a note that
            /// surfaces the collision (never silently resolved by first-match).
            /// </summary>
            public string CollisionNote { get; init; }
        }

        /// <summary>
        /// Layered recall with EXPLICIT scope, never first-match.
        /// Accepts either "slug" (repo-default: the repo layer is checked first, global
        /// is the fallback) or "global:slug" (global only — recall the machine-level
        /// version regardless of a same-slug repo topic).
        /// Returns null when the topic exists in neither layer. When the slug exists in
        /// BOTH layers, the requested layer's content is returned and
        /// <see cref="RecallResult.CollisionNote"/> states the other layer's slug so the
        /// caller can see the ambiguity instead of being silently resolved.
        /// </summary>
        public RecallResult RecallTopic(string topic)
        {
            string t = topic ?? "";
            bool explicitGlobal = t.StartsWith(GlobalTopicPrefix, StringComparison.OrdinalIgnoreCase);
            string slug = explicitGlobal ? t.Substring(GlobalTopicPrefix.Length) : t;

            // Repo layer (default; skipped when explicitly global).
            if (!explicitGlobal)
            {
                string repoContent = LoadTopicIn(_topicDir, slug);
                if (repoContent != null)
                    return new RecallResult
                    {
                        Content = repoContent,
                        CollisionNote = ListTopicsIn(_globalTopicDir).Contains(slug, StringComparer.OrdinalIgnoreCase)
                            ? $"NOTE: a machine-level (global) version of slug \"{slug}\" also exists — recall_memory \"{GlobalTopicPrefix}{slug}\" to compare."
                            : null,
                    };
            }

            // Global layer (fallback, or the only layer when explicitly global).
            string globalContent = LoadTopicIn(_globalTopicDir, slug);
            if (globalContent != null)
                return new RecallResult
                {
                    Content = globalContent,
                    CollisionNote = ListTopicsIn(_topicDir).Contains(slug, StringComparer.OrdinalIgnoreCase)
                        ? $"NOTE: the repo also has a topic of slug \"{slug}\" — recall_memory \"{slug}\" to compare."
                        : null,
                };

            return null;
        }

        /// <summary>
        /// Topic list across BOTH layers, in recall_memory order: repo topics first
        /// (the specific context the agent is operating in), then global topics prefixed
        /// with <see cref="GlobalTopicPrefix"/> so a slug collision is visible and
        /// recallable, never silently resolved. Byte-identical to the legacy repo-only
        /// list when no global topics exist.
        /// </summary>
        public List<string> ListTopicsForRecall()
        {
            var result = new List<string>(ListTopicsIn(_topicDir));
            foreach (string slug in ListTopicsIn(_globalTopicDir))
                result.Add(GlobalTopicPrefix + slug);
            return result;
        }

        /// <summary>
        /// Searches BOTH layers: repo topics first, then global (global hits tagged
        /// "global:slug" like <see cref="ListTopicsForRecall"/>). Byte-identical to the
        /// repo-only result when no global topics exist.
        /// </summary>
        public string SearchTopicsAllLayers(string pattern, int maxMatches = 50)
        {
            string repoResult = SearchTopicsIn(_topicDir, pattern, maxMatches);
            string globalResult = SearchTopicsIn(_globalTopicDir, pattern, maxMatches);

            // No global layer at all: byte-identical to the legacy repo-only result.
            if (ListTopicsIn(_globalTopicDir).Count == 0)
                return repoResult;

            // Tag global hits so they are distinguishable from repo hits.
            if (globalResult != null)
            {
                if (globalResult.StartsWith("search_memory: no matches"))
                    globalResult = null; // no global hits: fall through to repo result
                else
                    globalResult = globalResult.Replace("  ", "  global:");
            }

            if (repoResult == null)
                return globalResult;
            if (globalResult == null)
                return repoResult;
            return repoResult + "\n\n" + globalResult;
        }

        /// <summary>
        /// Standing context (BOTH layers): global (machine-level) standing topics first,
        /// under a GLOBAL header, then repo standing topics, under a REPO header (the
        /// REPO header only appears when a global section is present — labels matter,
        /// without them the model cannot tell which source a conflicting rule came from).
        ///
        /// Two PART budgets, by design: the global layer trims against its own
        /// <see cref="GlobalStandingContextMaxBytes"/> cap before a byte of it can reduce
        /// the repo layer's <paramref name="maxTotalBytes"/> cap, so a long global file
        /// can never make a repo convention disappear. When NO global standing context
        /// exists, the output is byte-for-byte the legacy repo-only result — the state on
        /// every machine without a %APPDATA%\devmind\memory directory, which
        /// StandingContextTests rely on.
        ///
        /// Returns null when there are no standing topics in either layer.
        /// </summary>
        public string LoadStandingContext(int maxTotalBytes = 24 * 1024)
        {
            try
            {
                string repo = BuildRepoStandingContext(maxTotalBytes);
                string global = LoadGlobalStandingContext();

                if (global == null)
                    return repo; // no global layer: byte-identical to legacy output

                var sb = new StringBuilder();
                sb.Append("\n## GLOBAL (machine-level) conventions\n");
                sb.Append(global);
                if (repo != null)
                {
                    sb.Append("\n## REPO conventions\n");
                    sb.Append(repo);
                }
                return sb.ToString();
            }
            catch
            {
                // Global layer is best-effort: if reading it blew up, degrade to
                // repo-only rather than dropping the repo context entirely.
                try { return BuildRepoStandingContext(maxTotalBytes); }
                catch { return null; }
            }
        }

        /// <summary>
        /// The repo-layer standing context, verbatim from the original implementation
        /// (MCP complaint #6): every topic whose slug starts with "standing-" or contains
        /// "convention", concatenated with per-topic headers, capped at
        /// <paramref name="maxTotalBytes"/> with the exact
        /// "[omitted — standing-context budget exhausted; recall_memory ...]" marker.
        /// Returns null when there are no repo standing topics.
        /// </summary>
        public string BuildRepoStandingContext(int maxTotalBytes = 24 * 1024)
        {
            try
            {
                var sb = new StringBuilder();
                int budget = maxTotalBytes;
                foreach (string slug in ListTopicsIn(_topicDir))
                {
                    if (!IsStandingSlug(slug)) continue;

                    string content = LoadTopicIn(_topicDir, slug);
                    if (string.IsNullOrWhiteSpace(content)) continue;

                    sb.Append(StandingBlock(slug, content, budget, out int bytes));
                    if (bytes >= 0)
                        budget -= bytes;
                }
                return sb.Length == 0 ? null : sb.ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The global (machine-level) standing context, built from the layer's own 8 KB
        /// cap (<see cref="GlobalStandingContextMaxBytes"/>) with its OWN omission
        /// marker — trimming happens INSIDE this layer, so the repo layer's budget is
        /// never touched. A slug that also exists in the repo is tagged "[global — also
        /// present in repo]" so the collision is visible in the combined output.
        /// Returns null when the global layer has no standing topics (including when the
        /// global directory does not exist). Never throws.
        /// </summary>
        public string LoadGlobalStandingContext(int maxTotalBytes = GlobalStandingContextMaxBytes)
        {
            try
            {
                var sb = new StringBuilder();
                int budget = maxTotalBytes;
                foreach (string slug in ListTopicsIn(_globalTopicDir))
                {
                    if (!IsStandingSlug(slug)) continue;

                    string content = LoadTopicIn(_globalTopicDir, slug);
                    if (string.IsNullOrWhiteSpace(content)) continue;

                    bool alsoInRepo = ListTopicsIn(_topicDir).Contains(slug, StringComparer.OrdinalIgnoreCase);
                    string headerSlug = alsoInRepo ? $"{slug} [global — also present in repo]" : slug;

                    string block = $"\n## [{headerSlug}]\n{content.Trim()}\n";
                    int bytes = Encoding.UTF8.GetByteCount(block);
                    if (bytes > budget)
                    {
                        sb.Append($"\n## [{headerSlug}]\n[omitted — global standing-context budget exhausted; recall_memory \"{GlobalTopicPrefix}{slug}\"]\n");
                        continue;
                    }
                    sb.Append(block);
                    budget -= bytes;
                }
                return sb.Length == 0 ? null : sb.ToString();
            }
            catch
            {
                // Missing/unreadable global directory is a normal state — degrade to
                // empty, never throw.
                return null;
            }
        }

        private static bool IsStandingSlug(string slug) =>
            slug.StartsWith("standing-", StringComparison.OrdinalIgnoreCase)
            || slug.IndexOf("convention", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Builds one standing block; if it exceeds the remaining budget, emits the
        /// omission pointer instead (never a half-file). Returns the block byte count,
        /// or -1 when the topic was omitted.
        /// </summary>
        private static string StandingBlock(string slug, string content, int budget, out int bytes)
        {
            string block = $"\n## [{slug}]\n{content.Trim()}\n";
            int b = Encoding.UTF8.GetByteCount(block);
            if (b > budget)
            {
                bytes = -1;
                return $"\n## [{slug}]\n[omitted — standing-context budget exhausted; recall_memory \"{slug}\"]\n";
            }
            bytes = b;
            return block;
        }

        // ── Layer 2 (cont.): Repo list / search (dir-parameterized) ─────────

        /// <summary>
        /// Lists all available REPO topic slugs (filenames without .md extension).
        /// </summary>
        public List<string> ListTopics() => ListTopicsIn(_topicDir);

        /// <summary>
        /// Searches all REPO topic files for lines matching a pattern (case-insensitive
        /// substring; '|' separates OR alternatives — same semantics as grep_file).
        /// Returns "topic:line: content" hits, or null when there are no topics.
        /// </summary>
        public string SearchTopics(string pattern, int maxMatches = 50) => SearchTopicsIn(_topicDir, pattern, maxMatches);

        // ── Helpers ─────────────────────────────────────────────────────────

        /// <summary>
        /// Deletes a topic file and removes its index entry. (Repo layer only — the
        /// machine-level layer is read-only at the tool surface.)
        /// </summary>
        public void DeleteTopic(string topicSlug)
        {
            try
            {
                topicSlug = SanitizeSlug(topicSlug);

                string path = Path.Combine(_topicDir, topicSlug + ".md");
                if (File.Exists(path))
                    File.Delete(path);

                // Remove from index
                var entries = LoadIndexEntries();
                entries.RemoveAll(e =>
                    e.StartsWith($"- [{topicSlug}]", StringComparison.OrdinalIgnoreCase));
                WriteIndex(entries);
            }
            catch
            {
                // Never crash the extension on memory file errors
            }
        }

        /// <summary>
        /// Sanitizes a topic slug: lowercase, alphanumeric + hyphens only, max 50 chars.
        /// </summary>
        public static string SanitizeSlug(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug))
                return "untitled";

            slug = slug.ToLowerInvariant().Trim();
            slug = Regex.Replace(slug, @"[^a-z0-9]+", "-");
            slug = slug.Trim('-');

            if (slug.Length > MaxSlugLength)
                slug = slug.Substring(0, MaxSlugLength).TrimEnd('-');

            return string.IsNullOrEmpty(slug) ? "untitled" : slug;
        }

        // ── Dir-parameterized topic reads (shared by repo + global layers) ───

        /// <summary>
        /// Returns the content of a topic file in <paramref name="topicDir"/>, or null
        /// if the directory or file does not exist. A missing directory returns null —
        /// never throws.
        /// </summary>
        private static string LoadTopicIn(string topicDir, string topicSlug)
        {
            try
            {
                topicSlug = SanitizeSlug(topicSlug);
                if (string.IsNullOrEmpty(topicDir))
                    return null;
                string path = Path.Combine(topicDir, topicSlug + ".md");
                if (!File.Exists(path))
                    return null;
                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Lists topic slugs (filenames without .md extension) in <paramref name="topicDir"/>.
        /// Returns an empty list when the directory does not exist — never throws.
        /// </summary>
        private static List<string> ListTopicsIn(string topicDir)
        {
            try
            {
                if (string.IsNullOrEmpty(topicDir) || !Directory.Exists(topicDir))
                    return new List<string>();

                return Directory.GetFiles(topicDir, "*.md")
                    .Select(f => Path.GetFileNameWithoutExtension(f))
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// Searches topic files in <paramref name="topicDir"/> for lines matching
        /// <paramref name="pattern"/> (case-insensitive substring; '|' separates OR
        /// alternatives — same semantics as grep_file). Returns "topic:line: content"
        /// hits, or null when there are no topics.
        /// </summary>
        private static string SearchTopicsIn(string topicDir, string pattern, int maxMatches)
        {
            var topics = ListTopicsIn(topicDir);
            if (topics.Count == 0)
                return null;

            var matcher = SearchPattern.BuildMatcher(pattern);
            var sb = new StringBuilder();
            int matches = 0;
            var matchedTopics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string topic in topics)
            {
                string content = LoadTopicIn(topicDir, topic);
                if (content == null) continue;

                string[] lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                for (int i = 0; i < lines.Length && matches < maxMatches; i++)
                {
                    if (!matcher(lines[i])) continue;
                    sb.AppendLine($"  {topic}:{i + 1}: {lines[i].TrimEnd()}");
                    matches++;
                    matchedTopics.Add(topic);
                }
                if (matches >= maxMatches) break;
            }

            if (matches == 0)
                return $"search_memory: no matches for \"{pattern}\" across {topics.Count} topic(s).";

            string header = matches >= maxMatches
                ? $"search_memory results for \"{pattern}\" (first {maxMatches} matches — recall_memory a topic for full content):"
                : $"search_memory results for \"{pattern}\" ({matches} match(es) across {matchedTopics.Count} topic(s)):";
            return header + "\n" + sb.ToString().TrimEnd('\r', '\n');
        }

        private List<string> LoadIndexEntries()
        {
            try
            {
                if (!File.Exists(_indexPath))
                    return new List<string>();

                return File.ReadAllLines(_indexPath, Encoding.UTF8)
                    .Where(line => line.StartsWith("- [", StringComparison.Ordinal))
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private void WriteIndex(List<string> entries)
        {
            var sb = new StringBuilder();
            sb.Append(IndexHeader);
            foreach (string entry in entries)
            {
                sb.AppendLine(entry);
            }

            string dir = Path.GetDirectoryName(_indexPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_indexPath, sb.ToString(), Encoding.UTF8);
        }
    }
}
