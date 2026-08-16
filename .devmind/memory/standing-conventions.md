Tool-selection rules for delegated work. These are dispositions, not per-task
constraints — they apply to every task unless a brief explicitly overrides them.

1. UNFAMILIAR API? CHECK THE DOCS BEFORE WRITING A REPRO.
   Before writing Win32 P/Invoke, or any framework/library API you have not
   written before, call learn_search (or learn_fetch / learn_code_search) FIRST.
   Do not write it from memory and then debug empirically.

   Field failure this comes from: a JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE P/Invoke
   was written from memory and failed with ERROR_INVALID_PARAMETER (87). Three
   rounds of empirical repro followed — a standalone console app, three flag
   combinations, byte-level struct verification — ending in the confident wrong
   conclusion "this environment restricts job objects". The actual cause was one
   sentence in the Microsoft docs: that flag REQUIRES the
   JOBOBJECT_EXTENDED_LIMIT_INFORMATION struct (class 9), not the basic one
   (class 2). learn_search returns that as the first result for an obvious query.
   The struct was byte-perfect — it was the right layout of the wrong struct.

2. APPENDING TO A FILE? USE append_file, NOT patch_file.
   patch_file needs a unique FIND anchor. The end of a file is the worst place to
   find one: trailing braces repeat, and a file-scoped namespace has no
   class-close + namespace-close pair to anchor on. Your own earlier edits also
   shift the lines out from under a FIND built on a stale read.

   Field failures this comes from: two separate thrash-guard stops in one day,
   both appending to the end of an existing file. One hit "Ambiguous FIND" on two
   blocks differing only in leading whitespace; the other hit "FIND text not
   found" anchoring on a brace pair that does not exist in a file-scoped
   namespace. For NEW tests, prefer create_file with a new test file over
   appending to an existing one — that sidesteps the anchor problem entirely.

3. WHEN A MEASUREMENT CONTRADICTS EXPECTATIONS, SUSPECT THE MEASUREMENT FIRST.
   Before concluding something surprising about the OS, the environment, or the
   world, verify the instrument you measured with. Escalating to more empirical
   work on a broken instrument produces confident wrong findings.

   Field failure this comes from: an IsProcessInJob helper declared with two
   parameters instead of three read the success-bool as the membership answer.
   Both the delegating caller and the agent then produced confident wrong
   diagnoses from its output ("the environment restricts job objects", "the test
   is querying a dead process"). Containment had been working the whole time.
   See also topic: job-object-pinvoke-gotcha.

4. A BASELINE TEST COUNT IS NOT A PASS.
   Reporting "410/410 passing" when 410 was already the baseline says nothing
   broke; it does not say the change works. Always report as "before -> after
   (+N new)". If N is 0 on a behavioural change, say so explicitly rather than
   letting the raw count imply verification.

5. NEVER WEAKEN AN ASSERTION TO MAKE A TEST PASS.
   If a test fails, either the code is wrong or the test is wrong — say which,
   with evidence. A relaxed assertion that passes against a broken implementation
   is worse than a red test, because it ships a silent no-op. If a case genuinely
   cannot be tested safely, say so and explain; do not write something vacuous.
