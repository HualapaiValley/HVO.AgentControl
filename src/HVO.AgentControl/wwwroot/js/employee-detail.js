import { fetchJson, rows, label } from '/js/page-common.js';
const root = document.querySelector('[data-employee-detail]');
if (root) {
    const id = root.dataset.employeeId || '';
    const status = root.querySelector('[data-page-status]');
    const portal = root.querySelector('[data-portal]');
    const receipt = root.querySelector('[data-orientation-receipt]');
    let employee = null;
    function render(data) {
        employee = data;
        root.querySelector('[data-employee-name]').textContent = data.displayName;
        const availability = root.querySelector('[data-employee-availability]');
        availability.textContent = label(data.availability);
        availability.dataset.availability = data.availability || 'unknown';
        rows(root.querySelector('[data-employee-identity]'), [['Stable ID', data.id], ['Organization ID', data.organizationId], ['Department', `${data.departmentDisplayName} (${data.departmentId})`], ['Role', `${data.roleDisplayName} (${data.roleId})`], ['Purpose', data.purpose], ['Instructions', data.instructions], ['Rules', data.rules], ['Restrictions', data.restrictions]]);
        rows(root.querySelector('[data-employee-runtime]'), [['Binding ID', data.runtime.bindingId], ['Placement', data.runtime.placement], ['Host owned', data.runtime.hostOwned], ['Native session ID', data.runtime.nativeSessionId], ['Session title', data.runtime.sessionTitle], ['Control status', data.runtime.controlStatus], ['Session state', data.runtime.sessionState], ['Observed model', data.runtime.controlModel], ['Terminal', data.terminal.available ? data.terminal.url : data.terminal.reason]]);
        rows(root.querySelector('[data-employee-orientation]'), data.orientation ? [['Assignment', data.orientation.assignmentId], ['Version', data.orientation.orientationVersion], ['State', data.orientation.state], ['Revision', data.orientation.revision], ['Restart required', data.orientation.restartRequired], ['Dispatch held', data.orientation.dispatchHeld], ['Hold reasons', (data.orientation.holdReasons || []).join(', ') || 'None'], ['Evidence source', data.orientation.evidenceSource], ['Last error', data.orientation.lastError]] : [['State', 'No orientation assignment']]);
        rows(root.querySelector('[data-employee-diagnostics]'), [['Sanitized runtime error', data.runtime.sanitizedError], ['Recent logs', `${data.recentLogs.supported ? 'Supported' : 'Unsupported'}: ${data.recentLogs.reason}`]]);
        root.querySelectorAll('[data-selected-employee-name]').forEach((node) => node.textContent = data.displayName);
        const enabled = data.runtime.hostOwned === true;
        root.querySelectorAll('[data-orientation-deliver],[data-orientation-comprehension],[data-orientation-hold]').forEach((button) => button.disabled = !enabled);
        const hold = root.querySelector('[data-orientation-hold]'); hold.dataset.held = String(data.orientation?.holdReasons?.includes('manual') === true); hold.textContent = hold.dataset.held === 'true' ? 'Clear manual hold' : 'Set manual hold';
        portal.dataset.selectedEmployeeId = data.id;
        portal.dataset.hostOwned = String(data.runtime.hostOwned === true);
        // Retain the safe read-model object as well as dispatching the event. The
        // page and terminal modules are independent ES modules, so a very fast
        // employee read can finish before terminal.js registers its listener.
        // The terminal controller consumes this retained selection after it
        // subscribes, making module load ordering irrelevant.
        portal.agentControlSelectedEmployee = data;
        portal.dispatchEvent(new CustomEvent('agentcontrol:employee-selected', { detail: data }));
        root.querySelector('[data-employee-content]').hidden = false;
        delete status.dataset.status;
        status.textContent = `Exact employee ${data.id}`;
    }
    async function load() { try { render(await fetchJson(`/api/employees/${encodeURIComponent(id)}`)); } catch (error) { status.textContent = error.status === 404 || error.status === 400 ? 'Employee not found.' : `Employee unavailable: ${error.message}`; status.dataset.status = 'error'; } }
    async function mutate(path, method, body) { receipt.dataset.status = 'pending'; receipt.textContent = 'Saving…'; try { const result = await fetchJson(path, { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }); await load(); receipt.dataset.status = 'ok'; receipt.textContent = `Saved. State: ${result.state || 'updated'}.`; } catch (error) { receipt.dataset.status = 'error'; receipt.textContent = `Action failed: ${error.message}`; } }
    root.querySelector('[data-orientation-deliver]').addEventListener('click', () => mutate('/api/orientation/deliver', 'POST', {}));
    root.querySelector('[data-orientation-comprehension]').addEventListener('click', () => mutate('/api/orientation/comprehension/run', 'POST', {}));
    root.querySelector('[data-orientation-hold]').addEventListener('click', (event) => mutate('/api/orientation/manual-hold', 'PUT', { held: event.currentTarget.dataset.held !== 'true', detail: 'Owner portal action' }));
    load();
}
