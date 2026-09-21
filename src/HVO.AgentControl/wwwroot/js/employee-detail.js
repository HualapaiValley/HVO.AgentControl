import { fetchJson, rows, label } from '/js/page-common.js';
const root = document.querySelector('[data-employee-detail]');
if (root) {
    const id = root.dataset.employeeId || '';
    const status = root.querySelector('[data-page-status]');
    const portal = root.querySelector('[data-portal]');
    const receipt = root.querySelector('[data-orientation-receipt]');
    const holdNote = root.querySelector('[data-hold-note]');
    const rebuildSection = root.querySelector('[data-employee-rebuild]');
    const rebuildForm = root.querySelector('[data-rebuild-form]');
    const rebuildTarget = root.querySelector('[data-rebuild-target]');
    const rebuildSubmit = root.querySelector('[data-rebuild-submit]');
    const resetWorkspace = root.querySelector('[data-reset-workspace]');
    const resetHome = root.querySelector('[data-reset-home]');
    const resetConfirmation = root.querySelector('[data-reset-confirmation]');
    const resetConfirmationWrap = root.querySelector('[data-reset-confirmation-wrap]');
    const resetConfirmationPhrase = root.querySelector('[data-reset-confirmation-phrase]');
    const rebuildReceipt = root.querySelector('[data-rebuild-receipt]');
    const rebuildHistory = root.querySelector('[data-rebuild-history]');
    const tasksSection = root.querySelector('[data-employee-tasks]');
    const taskForm = root.querySelector('[data-task-form]');
    const taskSubmit = root.querySelector('[data-task-submit]');
    const taskDescription = root.querySelector('[data-task-description]');
    const taskWorkspace = root.querySelector('[data-task-workspace]');
    const taskPaths = root.querySelector('[data-task-paths]');
    const taskForbidden = root.querySelector('[data-task-forbidden]');
    const taskSeconds = root.querySelector('[data-task-seconds]');
    const taskList = root.querySelector('[data-task-list]');
    const taskReceipt = root.querySelector('[data-task-receipt]');
    const toolChecks = [...root.querySelectorAll('[data-task-tool]')];
    let employee = null;
    let targetOptions = [];

    const requiredResetPhrase = () => {
        if (resetWorkspace.checked && resetHome.checked) return 'reset-workspace-and-home';
        if (resetWorkspace.checked) return 'reset-workspace';
        if (resetHome.checked) return 'reset-home';
        return '';
    };
    const refreshRebuildSubmit = () => {
        const phrase = requiredResetPhrase();
        resetConfirmationWrap.hidden = !phrase;
        resetConfirmationPhrase.textContent = phrase;
        if (!phrase) resetConfirmation.value = '';
        rebuildSubmit.disabled = !employee || !rebuildTarget.value || (!!phrase && resetConfirmation.value !== phrase);
    };
    const renderTargets = () => {
        const selected = rebuildTarget.value;
        rebuildTarget.replaceChildren();
        for (const target of targetOptions) {
            const option = document.createElement('option');
            option.value = target.id;
            option.textContent = `${target.profileName} r${target.revisionNumber} · ${target.id}`;
            rebuildTarget.append(option);
        }
        if (targetOptions.some((target) => target.id === selected)) rebuildTarget.value = selected;
        rebuildTarget.disabled = targetOptions.length === 0;
        refreshRebuildSubmit();
    };
    const loadFallbackTargets = async (profileStatus) => {
        if (!profileStatus.currentProfileId || !profileStatus.hostId) return [];
        let profiles = [];
        try { profiles = await fetchJson('/api/profiles'); } catch { return []; }
        const profile = profiles.find((item) => item.id === profileStatus.currentProfileId && item.status === 'active');
        if (!profile || !profile.currentRevisionId || profile.currentRevisionNumber <= profileStatus.currentRevisionNumber) return [];
        let builds = [];
        try { builds = await fetchJson(`/api/profiles/${encodeURIComponent(profile.id)}/revisions/${encodeURIComponent(profile.currentRevisionId)}/builds`); } catch { return []; }
        if (!builds.some((build) => build.hostId === profileStatus.hostId && build.state === 'built' && build.verified)) return [];
        return [{ id: profile.currentRevisionId, revisionNumber: profile.currentRevisionNumber, profileName: profile.displayName }];
    };
    const renderRebuildHistory = (history) => {
        rebuildHistory.replaceChildren();
        if (!history.length) {
            const empty = document.createElement('p'); empty.className = 'empty-state'; empty.textContent = 'No rebuild history.'; rebuildHistory.append(empty); return;
        }
        for (const rebuild of history) {
            const card = document.createElement('article'); card.className = 'request-card'; card.dataset.rebuildState = rebuild.state;
            const heading = document.createElement('h3'); heading.textContent = `${rebuild.state} · target ${rebuild.toProfileRevisionId}`;
            const summary = document.createElement('p');
            const reset = rebuild.resetWorkspace || rebuild.resetHome ? `Reset: ${rebuild.resetWorkspace ? 'workspace' : ''}${rebuild.resetWorkspace && rebuild.resetHome ? ' + ' : ''}${rebuild.resetHome ? 'home' : ''}.` : 'Workspace and home preserved.';
            summary.textContent = `${new Date(rebuild.updatedAt).toLocaleString()} · ${reset}`;
            card.append(heading, summary);
            if ((rebuild.state === 'Uncertain' || rebuild.state === 'Failed') && rebuild.failureSummary) {
                const failure = document.createElement('p'); failure.className = 'rebuild-failure'; failure.textContent = `Failure summary: ${rebuild.failureSummary}`; card.append(failure);
            }
            rebuildHistory.append(card);
        }
    };
    async function loadHistory() {
        if (!employee?.profileStatus?.workerId) { renderRebuildHistory([]); return; }
        try { renderRebuildHistory(await fetchJson(`/api/employees/${encodeURIComponent(id)}/rebuilds`)); }
        catch (error) { rebuildHistory.replaceChildren(); const message = document.createElement('p'); message.className = 'rebuild-failure'; message.textContent = `Rebuild history unavailable: ${error.message}`; rebuildHistory.append(message); }
    }

    const taskDisplayStates = ['requested', 'accepted', 'running', 'completed', 'verified', 'failed', 'cancelled', 'uncertain'];
    const detailRow = (dl, term, value) => {
        const dt = document.createElement('dt'); dt.textContent = term;
        const dd = document.createElement('dd'); dd.textContent = value === null || value === undefined || value === '' ? '—' : String(value);
        dl.append(dt, dd);
    };
    const boundedList = (values) => Array.isArray(values) && values.length ? values.join(', ') : '—';
    const renderTaskCard = (detail) => {
        const task = detail.task;
        const card = document.createElement('article');
        card.className = 'request-card task-card';
        card.dataset.taskId = task.id;
        card.dataset.taskState = detail.displayState;
        const heading = document.createElement('h3');
        heading.textContent = `${detail.displayState} · ${task.id}`;
        card.append(heading);
        const meta = document.createElement('p');
        meta.className = 'task-meta';
        meta.textContent = `Created ${new Date(task.createdAt).toLocaleString()} · updated ${new Date(task.updatedAt).toLocaleString()} · revision ${task.revision}`;
        card.append(meta);

        const spec = document.createElement('dl');
        detailRow(spec, 'Description', detail.spec?.description);
        detailRow(spec, 'Workspace root', detail.spec?.workspaceRoot);
        detailRow(spec, 'Allowed paths', boundedList(detail.spec?.allowedPaths));
        detailRow(spec, 'Allowed tools', boundedList(detail.spec?.allowedTools));
        detailRow(spec, 'Forbidden actions', boundedList(detail.spec?.forbiddenActions));
        detailRow(spec, 'Maximum seconds', detail.spec?.maximumSeconds);
        detailRow(spec, 'Test recipe', detail.spec?.testRecipeId);
        card.append(spec);

        const diagnostics = document.createElement('dl');
        diagnostics.className = 'task-diagnostics';
        detailRow(diagnostics, 'Worker', task.workerId);
        detailRow(diagnostics, 'Session', detail.request?.nativeSessionId);
        detailRow(diagnostics, 'Ownership epoch', detail.request?.ownershipEpoch);
        detailRow(diagnostics, 'Process generation', detail.request?.processGeneration);
        detailRow(diagnostics, 'Request state', detail.request?.state);
        detailRow(diagnostics, 'Forwarded', detail.request?.forwardedAt ? new Date(detail.request.forwardedAt).toLocaleString() : null);
        detailRow(diagnostics, 'Completed', detail.request?.completedAt ? new Date(detail.request.completedAt).toLocaleString() : null);
        card.append(diagnostics);

        if (task.failureDetail) {
            const failure = document.createElement('p');
            failure.className = 'task-failure';
            failure.textContent = `Failure: ${task.failureDetail}`;
            card.append(failure);
        }

        if (task.modelReportJson) {
            const reportLabel = document.createElement('p');
            reportLabel.className = 'task-model-label';
            reportLabel.textContent = 'Model-reported (unverified)';
            const report = document.createElement('pre');
            report.className = 'task-report';
            report.textContent = task.modelReportJson;
            card.append(reportLabel, report);
        }

        const verification = detail.verification;
        const verificationBlock = document.createElement('div');
        verificationBlock.className = 'task-verification';
        const verificationHeading = document.createElement('h4');
        verificationHeading.textContent = 'Host verification (independent)';
        verificationBlock.append(verificationHeading);
        if (!verification) {
            const none = document.createElement('p'); none.textContent = 'No host verification recorded.'; verificationBlock.append(none);
        } else {
            const vdl = document.createElement('dl');
            detailRow(vdl, 'State', verification.state);
            detailRow(vdl, 'Verifier', verification.verifierVersion);
            detailRow(vdl, 'Manifest hash', verification.manifestHash);
            detailRow(vdl, 'Test summary hash', verification.testSummaryHash);
            detailRow(vdl, 'Denied action hash', verification.deniedActionHash);
            detailRow(vdl, 'Verified at', verification.verifiedAt ? new Date(verification.verifiedAt).toLocaleString() : null);
            verificationBlock.append(vdl);
            if (verification.failureDetail) {
                const vf = document.createElement('p'); vf.className = 'task-failure'; vf.textContent = `Verification failure: ${verification.failureDetail}`; verificationBlock.append(vf);
            }
        }
        card.append(verificationBlock);

        const actions = document.createElement('div');
        actions.className = 'request-actions';
        const sync = document.createElement('button'); sync.type = 'button'; sync.className = 'btn'; sync.dataset.taskAction = 'sync'; sync.textContent = 'Sync';
        actions.append(sync);
        if (detail.displayState === 'running' || detail.displayState === 'accepted' || detail.displayState === 'requested' || detail.displayState === 'uncertain') {
            const cancel = document.createElement('button'); cancel.type = 'button'; cancel.className = 'btn btn-interrupt'; cancel.dataset.taskAction = 'cancel'; cancel.textContent = 'Cancel';
            actions.append(cancel);
        }
        if (detail.displayState === 'completed' && task.modelReportHash) {
            const verify = document.createElement('button'); verify.type = 'button'; verify.className = 'btn btn-primary'; verify.dataset.taskAction = 'verify'; verify.textContent = 'Verify';
            actions.append(verify);
        }
        const cancelNote = document.createElement('p');
        cancelNote.className = 'control-note';
        cancelNote.textContent = 'Cancellation stops the turn when observed; it is never a rollback of effects already applied.';
        actions.append(cancelNote);
        card.append(actions);

        actions.querySelector('[data-task-action="sync"]').addEventListener('click', () => taskAction('sync', task.id, { expectedTaskRevision: task.revision }, card));
        actions.querySelector('[data-task-action="cancel"]')?.addEventListener('click', () => taskAction('cancel', task.id, { expectedTaskRevision: task.revision }, card));
        actions.querySelector('[data-task-action="verify"]')?.addEventListener('click', () => taskAction('verify', task.id, { expectedTaskRevision: task.revision }, card));
        return card;
    };
    const renderTasks = (tasks) => {
        taskList.replaceChildren();
        if (!tasks.length) {
            const empty = document.createElement('p'); empty.className = 'empty-state'; empty.textContent = 'No tasks recorded for this employee.'; taskList.append(empty); return;
        }
        for (const detail of tasks) taskList.append(renderTaskCard(detail));
    };
    async function taskAction(action, taskId, body, card) {
        taskReceipt.dataset.status = 'pending'; taskReceipt.textContent = `${action === 'sync' ? 'Syncing' : action === 'cancel' ? 'Cancelling' : 'Verifying'} ${taskId}…`;
        try {
            const result = await fetchJson(`/api/tasks/${encodeURIComponent(taskId)}/${action}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
            await load();
            taskReceipt.dataset.status = 'ok';
            taskReceipt.textContent = action === 'sync'
                ? `Synced ${taskId}: ${result.detail}.`
                : action === 'cancel'
                    ? `Cancellation ${result.cancellation?.state || 'forwarded'} for ${taskId}.`
                    : `Verification ${result.verification?.state || 'recorded'} for ${taskId}.`;
        } catch (error) {
            taskReceipt.dataset.status = 'error'; taskReceipt.textContent = `Action failed: ${error.message}`;
        }
    }
    const refreshTaskSubmit = () => {
        const allowedTools = toolChecks.filter((node) => node.checked).map((node) => node.dataset.taskTool);
        const paths = taskPaths.value.split('\n').map((value) => value.trim()).filter(Boolean);
        const forbidden = taskForbidden.value.split('\n').map((value) => value.trim()).filter(Boolean);
        const seconds = Number(taskSeconds.value);
        taskSubmit.disabled = tasksSection.dataset.ready !== 'true' || !taskDescription.value.trim() || !taskWorkspace.value.trim()
            || paths.length === 0 || allowedTools.length === 0 || forbidden.length === 0
            || !Number.isInteger(seconds) || seconds < 1 || seconds > 1800;
    };
    taskForm.addEventListener('submit', async (event) => {
        event.preventDefault();
        refreshTaskSubmit();
        if (taskSubmit.disabled) return;
        const allowedTools = toolChecks.filter((node) => node.checked).map((node) => node.dataset.taskTool);
        const allowedPaths = taskPaths.value.split('\n').map((value) => value.trim()).filter(Boolean);
        const forbiddenActions = taskForbidden.value.split('\n').map((value) => value.trim()).filter(Boolean);
        const idempotencyKey = (crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(16).slice(2)}`);
        taskSubmit.disabled = true;
        taskReceipt.dataset.status = 'pending'; taskReceipt.textContent = 'Dispatching bounded task…';
        try {
            const created = await fetchJson(`/api/employees/${encodeURIComponent(id)}/tasks`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    expectedEmployeeRevision: employee.revision,
                    idempotencyKey,
                    taskSpec: {
                        description: taskDescription.value.trim(),
                        workspaceRoot: taskWorkspace.value.trim(),
                        allowedPaths,
                        allowedTools,
                        forbiddenActions,
                        maximumSeconds: Number(taskSeconds.value),
                        testRecipeId: root.querySelector('[data-task-recipe]').value || null,
                    },
                }),
            });
            await load();
            taskReceipt.dataset.status = 'ok';
            taskReceipt.textContent = `Dispatched ${created.task.id} (${created.displayState}).`;
        } catch (error) {
            taskReceipt.dataset.status = 'error'; taskReceipt.textContent = `Dispatch failed: ${error.message}`;
        } finally {
            refreshTaskSubmit();
        }
    });
    for (const node of [taskDescription, taskPaths, taskForbidden, taskSeconds, ...toolChecks]) {
        node.addEventListener('input', refreshTaskSubmit);
        node.addEventListener('change', refreshTaskSubmit);
    }

    async function render(data) {
        employee = data;
        root.querySelector('[data-employee-name]').textContent = data.displayName;
        const availability = root.querySelector('[data-employee-availability]');
        availability.textContent = label(data.availability);
        availability.dataset.availability = data.availability || 'unknown';
        rows(root.querySelector('[data-employee-identity]'), [['Stable ID', data.id], ['Revision', data.revision], ['Organization ID', data.organizationId], ['Department', `${data.departmentDisplayName} (${data.departmentId})`], ['Role', `${data.roleDisplayName} (${data.roleId})`], ['Purpose', data.purpose], ['Instructions', data.instructions], ['Rules', data.rules], ['Restrictions', data.restrictions]]);
        rows(root.querySelector('[data-employee-runtime]'), [['Binding ID', data.runtime.bindingId], ['Placement', data.runtime.placement], ['Host owned', data.runtime.hostOwned], ['Native session ID', data.runtime.nativeSessionId], ['Session title', data.runtime.sessionTitle], ['Control status', data.runtime.controlStatus], ['Session state', data.runtime.sessionState], ['Observed model', data.runtime.controlModel], ['Terminal', data.terminal.available ? data.terminal.url : data.terminal.reason]]);
        rows(root.querySelector('[data-employee-orientation]'), data.orientation ? [['Assignment', data.orientation.assignmentId], ['Version', data.orientation.orientationVersion], ['State', data.orientation.state], ['Revision', data.orientation.revision], ['Restart required', data.orientation.restartRequired], ['Dispatch held', data.orientation.dispatchHeld], ['Hold reasons', (data.orientation.holdReasons || []).join(', ') || 'None'], ['Evidence source', data.orientation.evidenceSource], ['Last error', data.orientation.lastError]] : [['State', 'No orientation assignment']]);
        rows(root.querySelector('[data-employee-diagnostics]'), [['Sanitized runtime error', data.runtime.sanitizedError], ['Recent logs', `${data.recentLogs.supported ? 'Supported' : 'Unsupported'}: ${data.recentLogs.reason}`]]);
        const profile = data.profileStatus || {};
        rows(root.querySelector('[data-employee-profile]'), [['Profile', profile.currentProfileDisplayName], ['Revision', profile.currentRevisionNumber], ['Revision ID', profile.currentProfileRevisionId], ['Image digest', profile.currentImageDigest], ['Platform', profile.currentPlatform], ['Active rebuild', profile.activeRebuildState]]);
        const updateNote = root.querySelector('[data-profile-update-note]');
        updateNote.hidden = !profile.newerRevisionAvailable;
        updateNote.textContent = profile.newerRevisionAvailable ? `Newer verified revision ${profile.newerRevisionNumber} is available. It will not be adopted automatically.` : '';
        targetOptions = profile.newerRevisionId ? [{ id: profile.newerRevisionId, revisionNumber: profile.newerRevisionNumber, profileName: profile.currentProfileDisplayName }] : await loadFallbackTargets(profile);
        renderTargets();
        rebuildSection.hidden = !profile.workerId;
        root.querySelectorAll('[data-selected-employee-name]').forEach((node) => node.textContent = data.displayName);
        const managed = data.runtime.hostOwned !== true && data.runtime.placement === 'DeveloperContainer';
        root.querySelectorAll('[data-orientation-deliver],[data-orientation-comprehension],[data-orientation-hold]').forEach((button) => button.disabled = !managed);
        const holdReasons = data.orientation?.holdReasons || [];
        const manualHeld = holdReasons.includes('manual');
        const hold = root.querySelector('[data-orientation-hold]');
        hold.dataset.held = String(manualHeld);
        hold.textContent = manualHeld ? 'Clear manual hold' : 'Set manual hold';
        holdNote.hidden = !managed;
        const nonManualHolds = holdReasons.filter((reason) => reason !== 'manual');
        holdNote.textContent = managed
            ? `Manual hold ${manualHeld ? 'active' : 'inactive'}. Clearing affects only the owner manual hold${nonManualHolds.length ? `; other holds remain: ${nonManualHolds.join(', ')}` : ''}. Orientation or recovery holds are never cleared here.`
            : '';
        const orientation = data.orientation;
        const comprehensionButton = root.querySelector('[data-orientation-comprehension]');
        comprehensionButton.disabled = !managed || !orientation || orientation.state !== 'delivered' || orientation.restartRequired === true;
        root.querySelector('[data-orientation-deliver]').disabled = !(managed && profile.workerId && data.runtime.controlStatus !== 'held');
        tasksSection.hidden = !profile.workerId;
        const taskReady = Boolean(profile.workerId) && data.runtime.controlStatus !== 'held' && orientation?.dispatchHeld !== true && Boolean(data.runtime.nativeSessionId);
        tasksSection.dataset.ready = String(taskReady);
        renderTasks(data.recentTasks || []);
        refreshTaskSubmit();
        portal.dataset.selectedEmployeeId = data.id;
        portal.dataset.hostOwned = String(data.runtime.hostOwned === true);
        portal.agentControlSelectedEmployee = data;
        portal.dispatchEvent(new CustomEvent('agentcontrol:employee-selected', { detail: data }));
        root.querySelector('[data-employee-content]').hidden = false;
        delete status.dataset.status;
        status.textContent = `Exact employee ${data.id}`;
    }
    async function load() { try { await render(await fetchJson(`/api/employees/${encodeURIComponent(id)}`)); await loadHistory(); } catch (error) { status.textContent = error.status === 404 || error.status === 400 ? 'Employee not found.' : `Employee unavailable: ${error.message}`; status.dataset.status = 'error'; } }
    async function mutate(path, method, body) { receipt.dataset.status = 'pending'; receipt.textContent = 'Saving…'; try { const result = await fetchJson(path, { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }); await load(); receipt.dataset.status = 'ok'; receipt.textContent = `Saved. State: ${result.status?.state || result.state || 'updated'}.`; } catch (error) { receipt.dataset.status = 'error'; receipt.textContent = `Action failed: ${error.message}`; } }
    root.querySelector('[data-orientation-deliver]').addEventListener('click', () => mutate(`/api/employees/${encodeURIComponent(id)}/orientation/deliver`, 'POST', { expectedEmployeeRevision: employee?.revision }));
    root.querySelector('[data-orientation-comprehension]').addEventListener('click', () => mutate(`/api/employees/${encodeURIComponent(id)}/orientation/comprehension/run`, 'POST', { expectedOrientationRevision: employee?.orientation?.revision }));
    root.querySelector('[data-orientation-hold]').addEventListener('click', (event) => mutate(`/api/employees/${encodeURIComponent(id)}/dispatch-hold`, 'PUT', { expectedEmployeeRevision: employee?.revision, held: event.currentTarget.dataset.held !== 'true', detail: 'Owner portal action' }));
    rebuildTarget.addEventListener('change', refreshRebuildSubmit);
    resetWorkspace.addEventListener('change', refreshRebuildSubmit);
    resetHome.addEventListener('change', refreshRebuildSubmit);
    resetConfirmation.addEventListener('input', refreshRebuildSubmit);
    rebuildForm.addEventListener('submit', async (event) => {
        event.preventDefault();
        refreshRebuildSubmit();
        if (rebuildSubmit.disabled) return;
        const phrase = requiredResetPhrase();
        rebuildSubmit.disabled = true;
        rebuildReceipt.dataset.status = 'pending'; rebuildReceipt.textContent = 'Rebuild in progress…';
        try {
            const result = await fetchJson(`/api/employees/${encodeURIComponent(id)}/rebuild`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ expectedRevision: employee.revision, targetProfileRevisionId: rebuildTarget.value, resetWorkspace: resetWorkspace.checked, resetHome: resetHome.checked, resetConfirmation: phrase || null }) });
            await load();
            rebuildReceipt.dataset.status = 'ok'; rebuildReceipt.textContent = `Rebuild ${result.rebuild.state}: ${result.rebuild.id}.`;
        } catch (error) { rebuildReceipt.dataset.status = 'error'; rebuildReceipt.textContent = `Rebuild failed: ${error.message}`; refreshRebuildSubmit(); }
    });
    load();
}
