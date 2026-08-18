// File: CommandEnumPairingTests.cs
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Automated replacement for the lost manual spike harness
// (%TEMP%\tg-keyspike\BindTest, removed by temp cleanup) that enforced the
// Terminal.Gui / Terminal.Gui.Editor version-pairing rule.
//
// WHY THIS EXISTS (read DEVMIND_STATUS.md §5 for the full analysis):
//
//   Between Terminal.Gui 2.4.3 (88 members) and 2.4.17 (92 members) the
//   `Command` enum GREW — four members added, none removed, at TWO separate
//   insertion points (2.4.3 ordinal terms):
//
//     • `Home` inserted at ordinal 15 — every member from 15 up shifts +1
//       (NewLine 43→44, SelectAll 41→42, Paste 61→62, …). This is the mid-list
//       insertion that causes the damage; `Command.Insert` moving 37→38 is a
//       CONSEQUENCE of it, not the cause.
//     • `Center`, `ZoomIn`, `ZoomOut` inserted after `Edit` near the tail —
//       the last four members (InsertCaretAbove, InsertCaretBelow,
//       StartSelection, StartRectangleSelection) shift +4, not +1.
//
//   Per-release attribution (which release introduced which insertion) is
//   UNVERIFIED — the dump compares 2.4.3 against 2.4.17, not each release.
//
//   The pairing boundary sits INSIDE the Editor 2.5.x line (verified 2026-08-18):
//
//     • Editor ≤ 2.5.2 is compiled against the PRE-insertion enum (core ≤ 2.4.3),
//       despite its nuspec claiming `>= 2.4.0`. Pairing it with a drifted core
//       cross-wires EVERY editing key:
//
//     • Enter     → core's DeleteAll   (text deleted instead of newline)
//     • Backspace → core's SelectAll   ("backspace highlights the row")
//     • Delete    → deletes LEFTWARD
//     • Ctrl+V    → lands in a dead handler (paste silently dropped)
//     • context menu renders core's names for Editor's ordinals
//       ("Cut" where Paste belongs, no Paste item)
//     • Editor ≥ 2.5.3 is compiled against the POST-insertion enum (requires core
//       ≥ 2.4.6; 2.5.7 requires ≥ 2.4.17 — the package nuspecs enforce the lower
//       bound). Pairing a post-insertion Editor with a PRE-drift core (≤ 2.4.3)
//       cross-wires the same way, in the opposite direction.
//
//   ALL of these failures are SILENT: KeyBindings.TryGet still returns a
//   binding; only the dispatch target is wrong. That is why this rule used to
//   be guarded by a manual offline dispatch harness — and why it is guarded
//   here by a test that runs in CI.
//
//   CURRENT PIN: core 2.4.17 + Editor 2.5.7 (post-insertion on both sides).
//   NEVER mix across the boundary — bump both packages together.
//
// TECHNIQUE (same rule DevMind.TUI/TuiInputBox.cs applies at runtime):
//
//   The Editor registers its own stock key bindings using the ordinals it was
//   COMPILED against. Read a stock binding off a freshly constructed Editor,
//   take `binding.Commands[0]` (the ordinal the Editor uses), and compare it
//   to what THIS process's core `Command` enum says that ordinal means. Under
//   an aligned pairing they agree; under a drifted pairing they diverge.
//
//   NOTE: this deliberately compares against this process's enum NAMES
//   (e.g. `Command.NewLine`) rather than literals — the invariant is "the
//   Editor's ordinals and the core's enum are the same enum", not "they are
//   some fixed set of numbers".
//
// WHICH ASSERTIONS ACTUALLY DETECT DRIFT (verified against the 2.4.3/2.4.17
// full-range dumps):
//
//   • Editor_StockEnter_Binds_Core_NewLine — LOAD-BEARING. The Editor bakes
//     Enter's ordinal in at ITS compile time (Editor ≤ 2.5.2 keeps 43, while
//     core's NewLine moves 43→44 under a post-insertion core). The only
//     Editor-vs-core comparison that must diverge across the boundary.
//   • Core_Command_Insert_Is_PostInsertion_Ordinal_38 — LOAD-BEARING canary on
//     the core enum itself: pinning Insert at 38 (≠37) fails the moment the
//     core is reverted to the pre-insertion layout, even if the Editor still
//     matched.
//   • Editor_StockSelectAll_CtrlA_Binds_Core_SelectAll — catches a mismatched
//     core INCIDENTALLY: Ctrl+A comes up NOT BOUND across the boundary, so
//     the "no stock binding" branch fires before the ordinal compare runs.
//   • Backspace, Delete, Ctrl+C, Ctrl+X, Ctrl+V — these keys are registered
//     by CORE's base View, so their stock ordinals move WITH core (40→41,
//     39→40, 59→60, 60→61, 61→62). Editor and core shift together, so these
//     assertions can NEVER detect enum drift. They still guard against
//     Editor-side binding regressions (a binding disappearing or retargeted
//     inside an Editor release) — keep them; they are not drift coverage.
//
//   Post-insertion ordinals in core 2.4.17 (for reference): Backspace 41,
//   Delete 40, Copy 60, Cut 61, Paste 62, SelectAll 42, NewLine 44, Insert 38.
//
// HEADLESS: constructing the Editor and reading its KeyBindings works WITHOUT
// Application.Init (verified against 2.4.3 + 2.5.2 and 2.4.17 + 2.5.7) — no
// console driver, no TTY, no global Application state, nothing to shut down.
// The tests below must stay that way: they run in shared CI alongside other
// test projects.

using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Document = Terminal.Gui.Editor.Document.TextDocument;
using GuiEditor = Terminal.Gui.Editor.Editor;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class CommandEnumPairingTests
    {
        // ── Shared context for every failure message ─────────────────────────
        //
        // Built from the assemblies ACTUALLY LOADED in this test process (not
        // what a csproj claims), so the message always names the real versions.

        private static string VersionContext
        {
            get
            {
                if (_versionContext != null) return _versionContext;

                string coreVer = DescribeAssembly(typeof(Command).Assembly);
                string editorVer = DescribeAssembly(typeof(GuiEditor).Assembly);

                _versionContext =
                    "════════ TERMINAL.GUI VERSION-PAIRING RULE VIOLATED ════════\n" +
                    $"  Loaded core:   Terminal.Gui        {coreVer}\n" +
                    $"  Loaded Editor: Terminal.Gui.Editor {editorVer}\n" +
                    "  Pairing rule:  The Command enum grew between core 2.4.3 and 2.4.17 —\n" +
                    "                 Home inserted at ordinal 15 (shifting members 15+ by +1)\n" +
                    "                 and Center/ZoomIn/ZoomOut after Edit (the last four\n" +
                    "                 members shift +4). Editor ≤ 2.5.2 is compiled against the\n" +
                    "                 PRE-insertion enum (requires core ≤ 2.4.3); Editor ≥ 2.5.3\n" +
                    "                 is compiled against the POST-insertion enum (requires core\n" +
                    "                 ≥ 2.4.6; 2.5.7 requires ≥ 2.4.17). Pairing across the\n" +
                    "                 boundary silently cross-wires EVERY editing key\n" +
                    "                 (Enter→DeleteAll, Backspace→SelectAll, Delete deletes\n" +
                    "                 leftward, Ctrl+V lands in a dead handler, context menu\n" +
                    "                 shows wrong labels). All failures are SILENT — dispatch\n" +
                    "                 still \"succeeds\" into the wrong handler.\n" +
                    "  Fix:          pin Terminal.Gui 2.4.17 + Terminal.Gui.Editor 2.5.7 in\n" +
                    "                 DevMind.TUI/DevMind.TUI.csproj. Never bump either\n" +
                    "                 package without re-running THIS test.\n" +
                    "  Full analysis: DEVMIND_STATUS.md §5 (\"Command-enum ORDINAL DRIFT\").";
                return _versionContext;
            }
        }

        private static string? _versionContext;

        private static string DescribeAssembly(Assembly a)
        {
            var informational = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            string fileVer = "";
            if (!string.IsNullOrEmpty(a.Location))
                try { fileVer = FileVersionInfo.GetVersionInfo(a.Location).FileVersion ?? ""; } catch { /* not on disk (e.g. trimmed) */ }
            return $"Assembly {a.GetName().Version}{(fileVer.Length > 0 ? $", file {fileVer}" : "")}" +
                   (informational != null ? $", informational \"{informational}\"" : "");
        }

        // ── The Editor under test ─────────────────────────────────────────────
        //
        // One stock Editor per test class (xunit constructs the class per test,
        // so a [MemberData]/static is overkill — a plain lazy instance is fine
        // and constructing it takes milliseconds).

        private static GuiEditor MakeStockEditor() => new()
        {
            Document = new Document(),   // Editor's backing TextDocument is null by default
            CanFocus = true,
        };

        // ── Boundary pin: detect a core REVERT even if Editor bindings line up ─

        [Fact]
        public void Core_Command_Insert_Is_PostInsertion_Ordinal_38()
        {
            // CANARY, not the inserted member: Command.Insert sits at ordinal 37 in
            // core 2.4.3 and at 38 in 2.4.17 because the `Home` insertion at ordinal 15
            // (plus three tail additions) shifted every later member. This is a
            // cheap, stable marker of "core enum pre- or post-drift": the CURRENT
            // pin (core 2.4.17 + Editor 2.5.7) is post-insertion on BOTH sides,
            // so this core must have Insert at 38. It fails the moment the core
            // is reverted to the pre-insertion layout (≤ 2.4.3), even if the
            // Editor is still a post-insertion release — a pre-insertion core is
            // never a valid pairing partner for Editor ≥ 2.5.3.
            int insertOrdinal = (int)Command.Insert;
            Assert.True(insertOrdinal == 38,
                $"{VersionContext}\n" +
                $"\nBoundary check: this process's core has Command.Insert at ordinal " +
                $"{insertOrdinal}. Insert is 37 in core 2.4.3 (pre-drift) and 38 in 2.4.17 " +
                "(the `Home` insertion at ordinal 15 shifted it +1). The current pin " +
                "(core 2.4.17 + Editor 2.5.7) is post-insertion on both sides, so this " +
                "core is OLDER than the pairing rule allows — a pre-insertion core cannot " +
                "be paired with any Editor ≥ 2.5.3.\n" +
                "If you have deliberately reverted both packages to a mutually aligned " +
                "older pairing (core ≤ 2.4.3 + Editor ≤ 2.5.2), update the pairing rule " +
                "in DevMind.TUI.csproj AND the expected ordinals in this test — " +
                "deliberately, not accidentally.");
        }

        // ── Cross-check: Editor's stock ordinals vs this process's core enum ─

        [Fact]
        public void Editor_StockEnter_Binds_Core_NewLine()
            => CheckEditorStockBinding(MakeStockEditor(), Key.Enter, Command.NewLine, "Enter");

        [Fact]
        public void Editor_StockBackspace_Binds_Core_DeleteCharLeft()
            => CheckEditorStockBinding(MakeStockEditor(), Key.Backspace, Command.DeleteCharLeft, "Backspace");

        [Fact]
        public void Editor_StockDelete_Binds_Core_DeleteCharRight()
            => CheckEditorStockBinding(MakeStockEditor(), Key.Delete, Command.DeleteCharRight, "Delete");

        [Fact]
        public void Editor_StockPaste_CtrlV_Binds_Core_Paste()
            => CheckEditorStockBinding(MakeStockEditor(), Key.V.WithCtrl, Command.Paste, "Ctrl+V (paste)");

        // The paste cluster includes the cut/copy side too — under a drifted
        // pairing the right-click context menu renders core's names for
        // Editor's ordinals ("Cut" where Paste belongs, no Paste item).
        [Fact]
        public void Editor_StockCopy_CtrlC_Binds_Core_Copy()
            => CheckEditorStockBinding(MakeStockEditor(), Key.C.WithCtrl, Command.Copy, "Ctrl+C (copy)");

        [Fact]
        public void Editor_StockCut_CtrlX_Binds_Core_Cut()
            => CheckEditorStockBinding(MakeStockEditor(), Key.X.WithCtrl, Command.Cut, "Ctrl+X (cut)");

        // SelectAll is the Backspace cross-wire victim named in §5 ("backspace
        // highlights the row" → Editor's Backspace lands on SelectAll).
        [Fact]
        public void Editor_StockSelectAll_CtrlA_Binds_Core_SelectAll()
            => CheckEditorStockBinding(MakeStockEditor(), Key.A.WithCtrl, Command.SelectAll, "Ctrl+A (select-all)");

        // ── The check itself ──────────────────────────────────────────────────

        private static void CheckEditorStockBinding(GuiEditor editor, Key key, Command expectedCoreMember, string keyLabel)
        {
            // 1. The Editor must register a stock binding for this key. If it
            //    doesn't, the pairing test can't run — fail loudly rather than
            //    pass vacuously.
            bool bound = editor.KeyBindings.TryGet(key, out KeyBinding binding);
            int editorOrdinal = -1;
            if (bound && binding is { Commands: { Length: > 0 } })
                editorOrdinal = (int)binding.Commands[0];

            Assert.True(bound && binding is { Commands: { Length: > 0 } },
                $"{VersionContext}\n" +
                $"\nEditor registered NO stock binding for {keyLabel}. The pairing check needs " +
                "the Editor's stock ordinal to compare against core's enum — if the Editor " +
                "version changed, re-derive the expectations in this test from the new " +
                "Editor's stock bindings (the invariant is agreement, not the literals).");

            // 2. The ordinal the Editor compiled in must mean the SAME command in
            //    THIS process's core enum. This is the entire test: under the
            //    pinned pairing (Editor 2.5.7 + core 2.4.17) they agree; under a
            //    mismatched pairing (Editor ≤ 2.5.2 with core ≥ 2.4.6, or
            //    Editor ≥ 2.5.3 with core ≤ 2.4.3) they diverge by
            //    +1 for members at or above the `Home` insertion (ordinal 15),
            //    and by +4 for the tail band (InsertCaretAbove through
            //    StartRectangleSelection). NOTE: the five keys registered by
            //    core's base View (Backspace, Delete, Ctrl+C/X/V) shift WITH
            //    core, so they cannot detect drift — they guard Editor-side
            //    regressions only.
            int coreOrdinal = (int)expectedCoreMember;
            Assert.True(editorOrdinal == coreOrdinal,
                $"{VersionContext}\n" +
                $"\nKey {keyLabel}: the Editor's stock binding uses ordinal {editorOrdinal} " +
                $"(what it was compiled to mean '{expectedCoreMember}'), but this process's core " +
                $"Command.{expectedCoreMember} is ordinal {coreOrdinal}." +
                (editorOrdinal == coreOrdinal - 1
                    ? "\nThis is the classic +1 drift from core's `Home` insertion at " +
                      "ordinal 15 (2.4.3 → 2.4.17) — the core enum has shifted relative to the Editor's."
                    : $"\nOrdinal delta {editorOrdinal - coreOrdinal} — an enum mismatch beyond the " +
                      "documented +1/+4 drift (the tail band — InsertCaretAbove through " +
                      "StartRectangleSelection — shifts +4); re-derive the pairing rule from full-range enum dumps."));
        }
    }
}
