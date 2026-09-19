# Repository Setup For The HualapaiValley Model

This is the checklist for bringing a repository into the same shape as
`HVO.SkyMonitor`: an organization-owned repository with a daily integration
branch under a fast required check, a stable branch that moves only by gated
promotion, org-level self-hosted runners, and an independent review identity.
Every step below was performed on `HVO.SkyMonitor` and then on this repository
(`HVO.AgentControl`, 2026-09-18); where a step has a known trap, the trap is
recorded next to it, and the section at the end records what was specific to
this repository.

The model in one picture:

```text
feature/<issue>-<name>          PR -> development/v1   fast CI (~7 min), bot-posted review
development/v1  (default)       nightly promotion PR   opened by hvo-agentcontrol[bot]
main            (stable)        full qualification     operator merges with a merge commit
```

## 1. Organization prerequisites (once, already done)

| Item | State | Where |
| --- | --- | --- |
| Organization | `HualapaiValley`, Free plan | https://github.com/HualapaiValley |
| Actions policy | all repos, all actions, **SHA pinning required** | org settings |
| Default workflow token | read-only, cannot approve PRs | org settings |
| Runner groups | `hvo-linux-x64` (3), `hvo-arm64` (4), `hvo-macos` (5), all `selected` visibility, public repos allowed | org Actions settings |
| GitHub App | `HVO-AgentControl`, App ID `4858890`, owned by the org, installed on all org repositories | org developer settings |
| App private key | org secret `HVO_AGENTCONTROL_PRIVATE_KEY` (also in `hvo-central-kv` as `HVO-AGENTCONTROL-PRIVATE-KEY`) | org Actions secrets |
| App ID / installation | org variables `HVO_AGENTCONTROL_APP_ID`, `HVO_AGENTCONTROL_INSTALLATION_ID` | org Actions variables |

Two plan facts that shape every decision below:

- **A Free organization cannot protect branches on private repositories.** A
  repository must be public to keep branch protection, or the org must be on
  Team ($4/user/month). `HVO.SkyMonitor`, `HVO.WebSite`, `HVO.RoofController`
  and `HVO.AgentControl` are public for this reason.
- **`workflow_dispatch` and `schedule` only register from the default branch.**
  The nightly promotion and the AgentControl control plane therefore require
  the daily branch to be the default, not `main`.

## 2. Before transferring a repository

Run from the source account while the repo is still there.

```bash
R=RoySalisbury/<repo>
gh api repos/$R --jq '{private,default_branch,open_issues_count}'
gh api repos/$R/branches --jq '.[]|select(.protected)|.name'   # will be DROPPED by transfer; record them
gh api repos/$R/actions/runners --jq '.runners[]|{name,status,labels:[.labels[]|select(.type=="custom")|.name]}'
gh secret list -R $R; gh variable list -R $R
gh api repos/$R/environments --jq '.environments[].name'
gh api repos/$R/keys --jq 'length'; gh api repos/$R/hooks --jq 'length'
gh api repos/HualapaiValley/<repo> 2>&1 | grep -q "Not Found" && echo "no name conflict"
```

What transfers intact: git history, issues, PRs, labels, milestones, secrets,
variables, environments, deploy keys, releases, packages, stars; old URLs
redirect. What does **not**: branch protection (dropped, not suspended), repo
scoped self-hosted runners, and GitHub App installations.

If the repo is private and needs protection, decide **public** or **Team**
before transferring, not after.

## 3. Move the runners first

Re-registering the repo's runners at org level before the transfer means CI
never loses them. Runner hosts and their SSH names are in
`~/.ssh/config` on the operator host (`github-runner`, `github-runner-pi`,
`m5-macbook`). The runner user is `actions` on Linux and per-repo on the Mac.

```bash
O=HualapaiValley
REG=$(gh api -X POST orgs/$O/actions/runners/registration-token --jq .token)   # expires in 1h
# on the runner host, in the runner's install directory, as root:
./svc.sh stop; ./svc.sh uninstall
sudo -u actions rm -f .runner .credentials .credentials_rsaparams .runner_migrated .service
sudo -u actions ./config.sh --url https://github.com/$O --token $REG \
  --runnergroup hvo-linux-x64 --name <runner-name> --labels <existing labels> --unattended
./svc.sh install actions; ./svc.sh start
```

Then delete the stale server-side record and re-attach any systemd drop-ins:

```bash
gh api -X DELETE repos/$R/actions/runners/<old-id>
# the unit is renamed actions.runner.HualapaiValley.<name>.service; drop-ins under the
# OLD unit name (NoNewPrivileges, cache paths) do not follow it. Move them:
mv /etc/systemd/system/actions.runner.RoySalisbury-*.service.d/* \
   /etc/systemd/system/actions.runner.HualapaiValley.<name>.service.d/
systemctl daemon-reload && systemctl restart actions.runner.HualapaiValley.<name>.service
```

Traps recorded on SkyMonitor:

- `config.sh remove` fails after a transfer (the old repo URL 404s). Skip it;
  delete the record via the API and clear the dot-files instead.
- A leftover `.runner_migrated` file blocks reconfiguration with "already
  configured". Delete it.
- On macOS, `svc.sh install` run as the runner user writes a per-user
  LaunchAgent that only runs while that user is logged in. Use a root-owned
  LaunchDaemon in `/Library/LaunchDaemons` with `UserName` set, as the two
  SkyMonitor Mac runners now do.
- The ARM64 harness asserts `NoNewPrivileges` from `/proc/self/status`; if the
  drop-in did not move, the first ARM64 run fails at that gate.

Keep the existing labels so no `runs-on` line needs to change.

## 4. Transfer and re-establish

```bash
gh api -X POST repos/RoySalisbury/<repo>/transfer -f new_owner=HualapaiValley
rid=$(gh api repos/HualapaiValley/<repo> --jq .id)
gh api -X PUT orgs/HualapaiValley/actions/runner-groups/3/repositories/$rid   # and 4/5 as needed
gh api -X PATCH repos/HualapaiValley/<repo> -F private=false                  # if going public
git remote set-url origin https://github.com/HualapaiValley/<repo>.git         # on every clone
```

Install the App on the repo: https://github.com/organizations/HualapaiValley/settings/installations
-> `HVO-AgentControl` -> Configure -> add the repository. Then grant the org
variables and secret to it:

```bash
for v in HVO_AGENTCONTROL_APP_ID HVO_AGENTCONTROL_INSTALLATION_ID; do
  gh variable set $v --org HualapaiValley --body "$(gh variable get $v --org HualapaiValley)" --visibility selected --repos <existing>,<repo>
done
# the secret's repo list is edited in the org UI (Actions secrets -> HVO_AGENTCONTROL_PRIVATE_KEY -> repository access)
```

## 5. Branches and protection

Create the daily branch from `main`, make it the default, protect both.

```bash
R=HualapaiValley/<repo>
gh api -X POST repos/$R/git/refs -f ref=refs/heads/development/v1 -f sha=$(gh api repos/$R/branches/main --jq .commit.sha)
gh api -X PATCH repos/$R -f default_branch=development/v1
```

`development/v1` protection (the two check names must match the workflow's job
`name:` values exactly):

```bash
gh api -X PUT "repos/$R/branches/development%2Fv1/protection" --input - <<'EOF'
{"required_status_checks":{"strict":true,"contexts":["Development v1 / Preflight","Development v1 / Build and Unit"]},
 "enforce_admins":true,
 "required_pull_request_reviews":{"required_approving_review_count":0,"dismiss_stale_reviews":false},
 "restrictions":null,"required_conversation_resolution":true,
 "allow_force_pushes":false,"allow_deletions":false,"required_linear_history":false}
EOF
```

`main` protection: the same, with `contexts` set to the repo's full-pipeline
checks (`Required CI` on SkyMonitor; `Build and test`, `Docker config and image`
and `Browser smoke (disabled runtime)` here). `required_linear_history` must
stay `false` on `main` so promotion can use a merge commit.

`required_approving_review_count` is 0 on purpose: GitHub refuses self-approval
when the reviewer and implementer share an account, and the review record lives
in the bot-posted comment and the resolved threads, which `required_conversation_resolution`
enforces.

## 6. Workflows to copy

Copy these from `HVO.SkyMonitor` and adjust the marked lines.

| File | Purpose | Adjust |
| --- | --- | --- |
| `.github/workflows/development-v1.yml` | hosted Preflight (whitespace, actionlint) + self-hosted Build and Unit under a 540s deadline with a timing manifest | build/test commands; `runs-on` labels; the stage list if the repo has no ShellCheck or category audit |
| `.github/workflows/agentcontrol.yml` | `verify-identity`, `post-review`, `post-finding`, `reply-thread` as the App, each bound to a head SHA | nothing; it reads owner/repo from context |
| `.github/workflows/promote-main.yml` | nightly promotion PR, 02:00 America/Phoenix | the aggregate check name if the green-run check is extended to `main`'s pipeline |
| `.github/ISSUE_TEMPLATE/development-v1.yml`, `.github/PULL_REQUEST_TEMPLATE/development-v1.md` | issue form and PR template | project-specific fields |
| `.github/dependabot.yml` | no `target-branch`, so it follows the default | ecosystems |
| `scripts/review:v1` | emits review request / correction / converged skeletons with exact ranges | nothing |
| `docs/runbooks/development-v1-review.md` (`docs/REVIEW-PROTOCOL.md` here) | the review process | nothing |
| `docs/development-v1.md` | landing page with the native workflow badge | badge URL |

Every `uses:` must be pinned by full commit SHA; the org enforces it and
Preflight's actionlint checks it.

Labels the process uses (create with `gh label create`): `review:mechanical`,
`review:standard`, `review:deep`, `review:requested`, `review:changes-required`,
`review:converged`, `risk:security`, `risk:durability`, `risk:concurrency`,
`risk:ci-control`, `risk:deployment`, `workflow:in-progress`, `workflow:blocked`.

## 7. Prove it before calling it done

In this order, because each step depends on the last:

1. Open a trivial PR to `development/v1`. Preflight runs hosted; Build and Unit
   waits for a `hvo-linux-x64` runner. Confirm the job's `runner_group_name`.
2. `gh workflow run agentcontrol.yml -f operation=verify-identity`. The summary
   must show the App slug and that the token sees exactly this repository.
3. Dispatch `post-review` with the body as an input bound to the PR head,
   confirm the comment author is `hvo-agentcontrol[bot]`; then `post-finding`
   and `reply-thread` once each and confirm the same.
4. Merge. Wait for the push run on `development/v1` to go green.
5. `gh workflow run promote-main.yml`. Confirm a promotion PR opens, authored by
   the bot, and that `main`'s full pipeline runs on it. Merge it with a merge
   commit.

## 8. Known follow-ups

- After a merge-commit promotion, `main` is one commit "ahead" of
  `development/v1` (the merge commit). The promotion workflow tolerates exactly
  that shape (a merge commit every parent of which is on `development/v1`,
  SkyMonitor #914) and halts on anything else.
- `resolveReviewThread` is refused for App installation tokens even on threads
  the App authored. The reviewer's `VERIFIED_*` reply is posted by the bot;
  the operator resolves the thread.
- Repositories with private data that must stay private need the org on Team
  before transfer, or they lose branch protection on arrival.

## 9. What was specific to HVO.AgentControl

Done 2026-09-18, after the transfer recorded in `docs/REPOSITORY-ADMIN.md`.

- **Fast gate scope.** `Development v1 / Build and Unit` runs restore, a
  warning-clean Release build, `dotnet format --verify-no-changes` and the
  hermetic test selection (1263 of 1322 at adoption): everything except
  `AgentIsolationContainerTests`, `WorkerImageContractTests`,
  `TmuxRealIntegrationTests` and `OpenCodeOutboundIntegrationTests`, which need
  a Docker daemon and the built images, a real tmux, or the pinned OpenCode
  wire. Those, the Docker config/image job and the hermetic browser suites stay
  in `build.yml` (`V2 CI`) as `main`'s qualification, so they run on every
  promotion PR. There is no ShellCheck or category-audit stage here.
- **Runner label.** The three `hvo-linux-x64` runners gained an
  `hvo-agentcontrol` label (alongside `hvo-skymonitor`) rather than adding a
  fourth machine; `.github/actionlint.yaml` lists only that label. The repository
  was added to runner group 3.
- **Review text is a dispatch input, not a file on the branch.** SkyMonitor
  commits the review body under `.agentcontrol/reviews/` and removes it after
  posting; that moves the PR head twice after the reviewed range, which the
  same rulebook says invalidates the review. Here `post-review`,
  `post-finding` and `reply-thread` take the text as workflow inputs, are bound
  to a full head SHA, and refuse to post if the PR has moved. Nothing is
  committed for a review, so there is no staged-file check in Preflight and no
  `.agentcontrol/` directory. The finding threads and `VERIFIED_*` replies are
  therefore bot-authored too, which SkyMonitor's two-operation workflow cannot
  do.
- **Ordering.** `APPROVE` on the head permits marking ready; Build and Unit
  then runs on that head; convergence is asserted only after the checks are
  green. This removes the deadlock in the SkyMonitor text between "converged
  before ready" and "no converged verdict before checks".
- **Python is pinned in the fast gate** (`setup-python` 3.12) because the
  selected tests execute the Python fake ACP server, worker supervisor and PTY
  bridge; SkyMonitor's gate has no such dependency.
- **Branch hygiene at adoption.** 133 V1-era branches (none touched since
  2026-09-12, no open PRs) were bundled to the operator host
  (`~/repo-archives/HVO.AgentControl-pre-v1-branches-2026-09-18.bundle`,
  SHA-256 `21ce251d…818d4d9d`) and deleted before `development/v1` was created
  from `main` at `a2c463a`. The open Dependabot PR was retargeted to
  `development/v1`.
- **Replaced process.** The earlier policy-lane review protocol (random
  reviewer-lane draws, model provenance, three-cycle cap) that governed PRs up
  to #262 is replaced by this model; `docs/REVIEW-PROTOCOL.md` now holds the
  v1 rulebook, and `.github/pull_request_template.md` is the v1 template.
- **Untouched.** `main` protection (three `V2 CI` checks, admins enforced,
  conversation resolution, no linear-history requirement), the `release`
  environment, deploy keys, and the manual `release.yml` workflow.
