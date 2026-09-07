# Request replies that are no longer needed

Related: #65. A native permission or question may disappear before a queued reply is delivered, for example because another client answered it or overlapping permissions were resolved. Disappearance alone does not tell AgentControl who acted or whether permission was granted.

The runtime supervisor reads a fresh scoped native snapshot before sending a reply. If the identified request is absent, it records the command as `Cancelled`, with an explicit “reply was not sent” explanation, and marks the request `NoLongerPending`. A `ReplyNotSent` event retains the command, worker, native request identity, request kind and observation timestamp. The original command retains responder provenance, submitted decision and scope-bearing request data. Repeating the same command ID returns that receipt; a new reply to the vanished request is rejected with a clear explanation.

This outcome applies only before native reply submission. A request disappearing after submission does not resolve an unknown delivery, prove approval, or justify replay. Existing accepted, finished and unknown commands are not rewritten by the preflight cancellation recorder. A request still present is sent through the existing adapter; a transport or native rejection after that check keeps existing failure/uncertainty handling. In particular, a racing 404 after submission is not reclassified as proof that nothing was sent.

No schema migration or broad permission grants are introduced. The existing command status/detail can display the distinction; the chat redesign in #75 can present that receipt more compactly. Root-cause reduction through actual task workspaces, child-agent context and scratch paths remains separate work under #42/#65.

Regression coverage includes permission/question absence, idempotent receipts, rejection of fresh replies, audit-event uniqueness, store restart, and preserving potentially delivered replies. SSH fixture CI continues to exercise the normal question/permission path; these new tests exercise the durable preflight outcome using isolated data.
