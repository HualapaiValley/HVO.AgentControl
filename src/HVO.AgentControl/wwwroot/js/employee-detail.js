import { fetchJson, rows, label } from '/js/page-common.js';
const root = document.querySelector('[data-employee-detail]');
if (root) {
    const id = root.dataset.employeeId || '';
    const status = root.querySelector('[data-page-status]');
    const portal = root.querySelector('[data-portal]');
    const receipt = root.querySelector('[data-orientation-receipt]');
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
        const enabled = data.runtime.hostOwned === true;
        root.querySelectorAll('[data-orientation-deliver],[data-orientation-comprehension],[data-orientation-hold]').forEach((button) => button.disabled = !enabled);
        const hold = root.querySelector('[data-orientation-hold]'); hold.dataset.held = String(data.orientation?.holdReasons?.includes('manual') === true); hold.textContent = hold.dataset.held === 'true' ? 'Clear manual hold' : 'Set manual hold';
        portal.dataset.selectedEmployeeId = data.id;
        portal.dataset.hostOwned = String(data.runtime.hostOwned === true);
        portal.agentControlSelectedEmployee = data;
        portal.dispatchEvent(new CustomEvent('agentcontrol:employee-selected', { detail: data }));
        root.querySelector('[data-employee-content]').hidden = false;
        delete status.dataset.status;
        status.textContent = `Exact employee ${data.id}`;
        await loadHistory();
    }
    async function load() { try { await render(await fetchJson(`/api/employees/${encodeURIComponent(id)}`)); } catch (error) { status.textContent = error.status === 404 || error.status === 400 ? 'Employee not found.' : `Employee unavailable: ${error.message}`; status.dataset.status = 'error'; } }
    async function mutate(path, method, body) { receipt.dataset.status = 'pending'; receipt.textContent = 'Saving…'; try { const result = await fetchJson(path, { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }); await load(); receipt.dataset.status = 'ok'; receipt.textContent = `Saved. State: ${result.state || 'updated'}.`; } catch (error) { receipt.dataset.status = 'error'; receipt.textContent = `Action failed: ${error.message}`; } }
    root.querySelector('[data-orientation-deliver]').addEventListener('click', () => mutate('/api/orientation/deliver', 'POST', {}));
    root.querySelector('[data-orientation-comprehension]').addEventListener('click', () => mutate('/api/orientation/comprehension/run', 'POST', {}));
    root.querySelector('[data-orientation-hold]').addEventListener('click', (event) => mutate('/api/orientation/manual-hold', 'PUT', { held: event.currentTarget.dataset.held !== 'true', detail: 'Owner portal action' }));
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
