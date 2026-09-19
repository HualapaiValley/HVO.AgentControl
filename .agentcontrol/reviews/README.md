# AgentControl review bodies

A review that should be posted as `hvo-agentcontrol[bot]` is committed here on the
PR branch as `PR-<number>-R<round>-<head8>.md`, beginning with the line
`REVIEW PR-<number>-R<round>-<head8>`, and then posted by dispatching the
`AgentControl` workflow with `operation=post-review`. The file is read from the PR
head by the workflow, so the review text is part of the reviewed range.

Files here are records, not code; they are excluded from every gate that derives a
file set. Delete a file after its review has been posted if the PR would otherwise
carry it into the base branch.
