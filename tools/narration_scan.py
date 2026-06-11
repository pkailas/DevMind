#!/usr/bin/env python3
# File: tools/narration_scan.py  v1.0
# Copyright (c) iOnline Consulting LLC. All rights reserved.
#
# Standalone analysis tool: detects "narration stalls" in DevMind historical
# session logs and emits a candidate list.
#
# A narration stall = a user turn where the agentic loop ended with prose
# (no tool call) instead of taking action. The model described work without
# calling any tool, causing the loop to stall until the user nudged it.
#
# Streams line-by-line (never loads full files into memory). Stdlib only.
#
# Usage:  python narration_scan.py
# Output: CSV at C:\Users\pkailas\dm_output.txt  +  summary to stdout.
#
# ── STEP 0: DERIVED ACTION GRAMMAR ──────────────────────────────────────────
#
# From empirical analysis of 2026-06-05T120431Z-pid39460 (~1,034 tool calls)
# and cross-referenced with AgenticLoop.ts (DevMindShell):
#
# The model uses the OpenAI function-calling API. Each assistant message is
# either:
#
#   ACTION  = assistant message with a non-empty `tool_calls` array.
#             The `content` field may contain preamble prose
#             ("Now let me check the build...") but the actual directive
#             is the tool_calls entry. The loop dispatches these via MCP.
#
#   NARRATION = assistant message with NO tool_calls (empty or absent).
#               Pure prose response. The loop exits with finish_reason="stop".
#               May be legitimate (answering a question) or a stall
#               (describing work without doing it).
#
# The agentic loop (AgenticLoop.ts) handles these as:
#   - finish_reason="tool_calls" -> parse, dispatch, re-prompt (continue loop)
#   - finish_reason="stop"       -> turn complete (loop exits)
#
# A narration stall occurs when finish_reason="stop" but the prose carries
# a confabulation signal -- language that asserts a tool-worthy action that
# no tool actually produced. The confabulation discriminator patterns:
#
#   announced_action:  "let me read/check/look/inspect/verify/find/search/
#                      grep/fix/update/edit/modify/change/add/create/
#                      remove/delete/rename/run/build/test"
#   build_test_claim:  "build: N errors", "build succeeded/failed",
#                      "tests passed", "compiles cleanly"
#   report_header:     "## Fix", "## Root Cause", "## Changes", "## Summary"
#
# ─────────────────────────────────────────────────────────────────────────────

import csv
import glob
import json
import os
import re
import sys
from pathlib import Path

# ── Paths ────────────────────────────────────────────────────────────────────

TRAINING_DIR = r"C:\Users\pkailas\AppData\Roaming\devmind\training_logs"
TRACE_DIRS = [
    r"C:\Users\pkailas\source\repos\DevMindShell\.dm-trace",
    r"C:\Users\pkailas\AppData\Local\devmind\trace",
]
NARRATION_DIAG_FILE = os.path.join(TRAINING_DIR, "narration-diag.jsonl")
OUTPUT_CSV = r"C:\Users\pkailas\dm_output.txt"

CONTEXT_WINDOW_TOKENS = 262144
CHARS_PER_TOKEN = 4

# ── Confabulation claim patterns (from AgenticLoop.ts NARRATION_CLAIM_PATTERNS) ──

CLAIM_PATTERNS = [
    (re.compile(
        r"\b(?:now\s+)?let me\s+"
        r"(?:read|check|look|inspect|verify|find|search|grep|fix|update|"
        r"edit|modify|change|add|create|remove|delete|rename|run|build|test)"
        r"\b", re.IGNORECASE
    ), "announced_action"),
    (re.compile(r"\bbuild:\s*\d+\s*(?:errors?|warnings?)\b", re.IGNORECASE),
     "build_test_claim"),
    (re.compile(r"\b0\s+(?:errors?|warnings?)\b", re.IGNORECASE),
     "build_test_claim"),
    (re.compile(r"\bbuild\s+(?:succeeded|failed)\b", re.IGNORECASE),
     "build_test_claim"),
    (re.compile(r"\btests?\s+pass(?:ed)?\b", re.IGNORECASE),
     "build_test_claim"),
    (re.compile(r"\bcompiles?\s+cleanly\b", re.IGNORECASE),
     "build_test_claim"),
    (re.compile(
        r"^\s*#{1,6}\s+(?:fix|root cause|changes|summary)\b",
        re.IGNORECASE | re.MULTILINE
    ), "report_header"),
]


# ── Helpers ──────────────────────────────────────────────────────────────────


def has_claim_signal(text):
    """Return the matched claim-signal category, or None."""
    for pattern, signal in CLAIM_PATTERNS:
        if pattern.search(text):
            return signal
    return None


def estimate_tokens(messages):
    """Estimate token count from a message list (~4 chars/token)."""
    total_chars = 0
    for m in messages:
        content = m.get("content") or ""
        total_chars += len(content)
        for tc in m.get("tool_calls", []):
            func = tc.get("function", {})
            total_chars += len(func.get("name", ""))
            total_chars += len(func.get("arguments", ""))
    return max(1, total_chars // CHARS_PER_TOKEN)


def extract_prefix(filename):
    """Extract session prefix from filename (e.g. 2026-06-05T120431Z-pid39460)."""
    return Path(filename).stem


def find_trace_file(trace_dirs, prefix, suffix):
    """Find a trace file matching prefix+suffix in any trace directory."""
    for d in trace_dirs:
        if not os.path.isdir(d):
            continue
        path = os.path.join(d, f"{prefix}{suffix}")
        if os.path.isfile(path):
            return path
    return None


def count_lines(filepath):
    """Count lines in a file without loading it into memory."""
    count = 0
    with open(filepath, "r", encoding="utf-8") as f:
        for _ in f:
            count += 1
    return count


def load_narration_diag():
    """Load narration-diag.jsonl indexed by (session_id, turn_index)."""
    if not os.path.isfile(NARRATION_DIAG_FILE):
        return {}
    entries = {}
    with open(NARRATION_DIAG_FILE, "r", encoding="utf-8") as f:
        for line in f:
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                continue
            key = (obj.get("session_id", ""), obj.get("turn_index", -1))
            entries[key] = obj
    return entries


def session_trace_info(prefix):
    """Pre-scan the shell trace for a session.
    Returns (has_clean_exit, had_retry_force, tool_call_count).
    """
    shell_trace = find_trace_file(TRACE_DIRS, prefix, ".shell.jsonl")
    if not shell_trace:
        return False, False, 0

    has_exit = False
    had_retry = False
    tc_count = 0

    with open(shell_trace, "r", encoding="utf-8") as f:
        for line in f:
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                continue
            evt = obj.get("event", "")
            if evt == "shell.exit":
                has_exit = True
            elif evt == "mcp.tool_call.request":
                tc_count += 1
            else:
                el = evt.lower()
                if "retry" in el or "force" in el or "tool_choice" in el:
                    had_retry = True

    return has_exit, had_retry, tc_count


# ── Core scan ────────────────────────────────────────────────────────────────


def scan_session(train_file, diag_index):
    """Scan a single training log file for narration stalls.

    Each LINE = one user turn (cumulative snapshot of the conversation).
    We find the LAST assistant message among the new messages in each turn.
    If it has no tool_calls, the agentic loop ended with prose -- a candidate
    narration stall.

    Streams line-by-line; never loads the full file.
    Yields candidate dicts.
    """
    prefix = extract_prefix(train_file)
    total_lines = count_lines(train_file)

    has_exit, had_retry, trace_tc_count = session_trace_info(prefix)

    prev_msg_count = 0
    turn_index = 0

    with open(train_file, "r", encoding="utf-8") as f:
        for line in f:
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                continue

            msgs = obj.get("messages", [])
            current_count = len(msgs)
            new_msgs = msgs[prev_msg_count:]

            outcome = obj.get("outcome", "")
            timestamp = obj.get("timestamp", "")

            # Find the LAST assistant message among the new messages.
            # This determines how the agentic loop ended for this turn.
            last_assistant = None
            for m in reversed(new_msgs):
                if m.get("role") == "assistant":
                    last_assistant = m
                    break

            # No new assistant message this turn (e.g. user-only turn)
            if last_assistant is None:
                prev_msg_count = current_count
                turn_index += 1
                continue

            tc = last_assistant.get("tool_calls", [])
            content = last_assistant.get("content") or ""

            # ACTION: last assistant has tool calls -- not a stall
            if tc:
                prev_msg_count = current_count
                turn_index += 1
                continue

            # NARRATION: last assistant has NO tool calls.
            # The agentic loop ended with prose (finish_reason="stop").

            # Skip clean terminal turns (last turn, clean exit, no claim signal)
            is_last_turn = (turn_index == total_lines - 1)
            claim = has_claim_signal(content)
            is_clean_terminal = is_last_turn and has_exit and claim is None

            if is_clean_terminal:
                prev_msg_count = current_count
                turn_index += 1
                continue

            # CANDIDATE narration stall -- capture context
            messages_before = msgs[:prev_msg_count]
            approx_tokens = estimate_tokens(messages_before)
            usage_pct = approx_tokens / CONTEXT_WINDOW_TOKENS

            # Validate against narration-diag if available
            diag_entry = diag_index.get((prefix, turn_index))
            diag_validated = diag_entry is not None
            diag_tokens = diag_entry.get("turn_start_tokens") if diag_entry else None

            candidate = {
                "session_id": prefix,
                "turn_index": turn_index,
                "total_turns": total_lines,
                "session_position": round(turn_index / max(1, total_lines), 4),
                "approx_tokens_at_turn_start": approx_tokens,
                "usage_pct": round(usage_pct, 4),
                "diag_validated": diag_validated,
                "diag_turn_start_tokens": diag_tokens if diag_validated else "",
                "had_retry_force": had_retry,
                "claim_signal": claim or "",
                "narration_excerpt": content[:200].replace("\n", " ").replace("\r", ""),
                "outcome": outcome,
                "timestamp": timestamp,
                "response_chars": len(content),
            }
            yield candidate

            prev_msg_count = current_count
            turn_index += 1


# ── Main ─────────────────────────────────────────────────────────────────────


def main():
    sys.stdout.reconfigure(encoding="utf-8")

    diag_index = load_narration_diag()

    training_files = sorted(glob.glob(os.path.join(TRAINING_DIR, "*.jsonl")))
    training_files = [
        f for f in training_files
        if Path(f).name != "narration-diag.jsonl"
    ]

    print(f"Found {len(training_files)} training log files")

    all_candidates = []
    total_sessions = 0
    total_turns = 0

    for train_file in training_files:
        total_sessions += 1
        session_turns = count_lines(train_file)
        total_turns += session_turns

        for candidate in scan_session(train_file, diag_index):
            all_candidates.append(candidate)

    # ── Write CSV ────────────────────────────────────────────────────────────

    fieldnames = [
        "session_id", "turn_index", "total_turns", "session_position",
        "approx_tokens_at_turn_start", "usage_pct", "diag_validated",
        "diag_turn_start_tokens", "had_retry_force", "claim_signal",
        "narration_excerpt", "outcome", "timestamp", "response_chars",
    ]

    with open(OUTPUT_CSV, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        for c in all_candidates:
            writer.writerow(c)

    # ── Summary to stdout ────────────────────────────────────────────────────

    print()
    print("=" * 60)
    print("NARRATION STALL SCAN -- SUMMARY")
    print("=" * 60)
    print(f"Sessions scanned:       {total_sessions}")
    print(f"Total turns:            {total_turns}")
    print(f"Candidates found:       {len(all_candidates)}")
    print(f"Output CSV:             {OUTPUT_CSV}")

    # Bucket by usage_pct (as percentage)
    buckets_usage = {"<25%": 0, "25-50%": 0, "50-75%": 0, ">75%": 0}
    for c in all_candidates:
        up = c["usage_pct"] * 100
        if up < 25:
            buckets_usage["<25%"] += 1
        elif up < 50:
            buckets_usage["25-50%"] += 1
        elif up < 75:
            buckets_usage["50-75%"] += 1
        else:
            buckets_usage[">75%"] += 1

    print()
    print("By context usage (usage_pct):")
    for bucket, count in buckets_usage.items():
        print(f"  {bucket:>6}: {count}")

    # Bucket by session_position
    buckets_position = {"first_third": 0, "middle_third": 0, "last_third": 0}
    for c in all_candidates:
        pos = c["session_position"]
        if pos < 0.333:
            buckets_position["first_third"] += 1
        elif pos < 0.666:
            buckets_position["middle_third"] += 1
        else:
            buckets_position["last_third"] += 1

    print()
    print("By session position:")
    for bucket, count in buckets_position.items():
        print(f"  {bucket:>13}: {count}")

    # Claim signal breakdown
    claim_counts = {}
    for c in all_candidates:
        cs = c["claim_signal"] or "(none)"
        claim_counts[cs] = claim_counts.get(cs, 0) + 1
    print()
    print("By claim signal:")
    for cs, count in sorted(claim_counts.items(), key=lambda x: -x[1]):
        print(f"  {cs:>20}: {count}")

    # Outcome breakdown
    outcome_counts = {}
    for c in all_candidates:
        o = c["outcome"]
        outcome_counts[o] = outcome_counts.get(o, 0) + 1
    print()
    print("By outcome:")
    for o, count in sorted(outcome_counts.items(), key=lambda x: -x[1]):
        print(f"  {o:>30}: {count}")

    # Diag validation stats
    validated = sum(1 for c in all_candidates if c["diag_validated"])
    print()
    print(f"Token estimate validated by narration-diag: {validated}/{len(all_candidates)}")

    retry = sum(1 for c in all_candidates if c["had_retry_force"])
    print(f"Candidates in sessions with retry/force events: {retry}/{len(all_candidates)}")


if __name__ == "__main__":
    main()
