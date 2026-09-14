## Summary

What changed and why?

## Validation

- [ ] Build, tests, and formatting passed.
- [ ] Relevant browser/container checks passed (or explain what was not run).
- [ ] No credentials, runtime data, or private transcripts included.
- [ ] Version history and documentation updated when needed.

Commands/results:
- Starting baseline SHA/checks and pinned toolchain:
- Failures encountered, diagnosis/fix, and final exact-head results:

## Review

- [ ] Reviewer count matches the owner-approved #243 practice: routine change =
      one independent reviewer, high-risk change = two independent reviewers, and
      focused correction = one independent reviewer. This PR is high-risk and
      therefore still requires two. Approved lanes exclude the coordinator; model
      provenance and any routing/fallback limits are recorded under the Phase 1
      review protocol.
- [ ] Independent review (not the implementing agent) ran against the same exact
      immutable merge-base/head SHA pair recorded below.
- [ ] Every finding, including all owner comments, is triaged.
- [ ] Findings were checked against actual repository context and acceptance
      criteria; rejected suggestions have specific evidence/scope reasoning.
- [ ] Each fix is answered in its own thread with the change, the validation
      that exercised it, and the exact head it applies to, before resolving it.
- [ ] Deferrals have owner approval and a linked follow-up issue; no security,
      data-loss, acceptance, failing-CI or material-correctness finding is
      deferred.
- [ ] Deferred findings are commented as Deferred with issue numbers in their
      original threads; linked issues remain open, labeled `status:deferred`,
      and scheduled for the next development/review cycle.
- [ ] Both reviewers assessed all carried-forward items and their interactions;
      unfinished items have an explicit new owner disposition.
- [ ] Review round count is within the three-round bound, or the owner approved
      a fourth.
- [ ] Required CI is green on the exact reviewed head (necessary, not
      sufficient).
- [ ] Reviewer packets include relevant unchanged context and state omissions;
      the PR body reflects the current head, checks and review dispositions.

- Reviewed head SHA:
- Immutable review base SHA (both reviewers attest this same base):
- Review round: 1 / 2 / 3 / 4 (4 requires explicit owner authorization reference):
- Additional-cycle authorization reference, if applicable:
- Coordinator provider/model:
- Eligible model pool and random selection:
- Reviewer A provider/model, session/evidence, scope, base/head attestation:
- Reviewer B provider/model, session/evidence, scope, base/head attestation:
- Upstream routing/fallback evidence or unresolved provenance limits:
- Deferred findings and linked issues:
- Carried-forward issues, source findings, fixes and reviewer dispositions:

## UI Evidence

Attach redacted screenshots for visible changes, or mark not applicable.

## Risks / Known Limitations

Include recovery, compatibility, security, or deployment considerations.

## Merge Authorization

- [ ] Owner has explicitly authorized merge for this exact head.
- Authorization reference (Phase 1 standing authorization is valid only after
  both independent reviews and exact-head required CI pass):
- [ ] No auto-merge. Merging does not trigger a release.

## Post-Merge Record

Complete after merge, not as a claim that these checks already passed:
- Actual merge SHA and resulting main CI:
- Issue closure verified; unfinished/deferred issues still open:
- Local/remote branch state and next dependency-ready issue:
