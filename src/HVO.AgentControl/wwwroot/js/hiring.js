import { fetchJson } from '/js/page-common.js';
const root = document.querySelector('[data-hiring-page]');
if (root) {
    const form = root.querySelector('[data-hire-form]');
    const status = root.querySelector('[data-page-status]');
    const receipt = root.querySelector('[data-hire-receipt]');
    const list = root.querySelector('[data-hire-requests]');
    const department = form.elements.departmentId;
    const role = form.elements.roleId;
    const submit = root.querySelector('[data-hire-submit]');
    const noRoles = root.querySelector('[data-no-hire-roles]');
    let organization = null;
    // Profile revisions with a verified build on at least one ready host, keyed
    // by revision id. The approval selects one revision and one host where the
    // exact verified build exists; the server re-resolves the build itself.
    const approvedBuilds = new Map();
    let activeRevisions = [];
    let readyHosts = [];
    const rolesForDepartment = () => {
        role.replaceChildren();
        const available = organization.roles.filter((item) => item.departmentId === department.value);
        for (const item of available) { const option = document.createElement('option'); option.value = item.id; option.textContent = item.displayName; role.append(option); }
        const hasRoles = available.length > 0;
        role.disabled = !hasRoles;
        submit.disabled = !hasRoles;
        noRoles.hidden = hasRoles;
    };
    const newIdempotencyKey = () => {
        if (!globalThis.crypto || typeof globalThis.crypto.getRandomValues !== 'function') throw new Error('Secure random generation is unavailable.');
        if (typeof globalThis.crypto.randomUUID === 'function') return globalThis.crypto.randomUUID();
        const bytes = globalThis.crypto.getRandomValues(new Uint8Array(16));
        bytes[6] = (bytes[6] & 0x0f) | 0x40;
        bytes[8] = (bytes[8] & 0x3f) | 0x80;
        const hex = [...bytes].map((value) => value.toString(16).padStart(2, '0')).join('');
        return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
    };
    const pretty = (value) => (value === null || value === undefined || value === '') ? '—' : String(value);
    const frozenRow = (label, value) => {
        const dt = document.createElement('dt'); dt.textContent = label;
        const dd = document.createElement('dd'); dd.textContent = pretty(value);
        return [dt, dd];
    };
    const hostsForRevision = (revisionId) => readyHosts.filter((host) => (approvedBuilds.get(revisionId) || []).includes(host.id));

    const renderRequestCard = (request) => {
        const card = document.createElement('article'); card.className = 'request-card'; card.dataset.requestId = request.id; card.dataset.requestState = request.state;
        const title = document.createElement('h3'); title.textContent = request.requestedDisplayName;
        const meta = document.createElement('p'); meta.textContent = `${request.state} · ${new Date(request.createdAt).toLocaleString()} · ${request.id}`;
        const detail = document.createElement('p'); detail.textContent = `${request.departmentDisplayName} · ${request.roleDisplayName} · ${request.placement} · ${request.cpuLimit} CPU / ${request.memoryLimitMiB} MiB / ${request.pidsLimit} PIDs`;
        const purpose = document.createElement('p'); purpose.textContent = request.purpose;
        const actions = document.createElement('div'); actions.className = 'request-actions';
        const reject = document.createElement('button'); reject.type = 'button'; reject.className = 'btn'; reject.textContent = 'Reject'; reject.disabled = request.state !== 'Requested'; reject.addEventListener('click', async () => { receipt.dataset.status = 'pending'; receipt.textContent = `Rejecting ${request.id}…`; try { await fetchJson(`/api/hire-requests/${encodeURIComponent(request.id)}/reject`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ expectedRevision: request.revision }) }); await load(); receipt.dataset.status = 'ok'; receipt.textContent = `Rejected ${request.id}.`; } catch (error) { receipt.dataset.status = 'error'; receipt.textContent = `Reject failed: ${error.message}`; } });
        actions.append(reject);

        // "Approved and beyond": the owner freeze exists and its exact selection
        // is rendered. Rejected is a terminal non-approval and shows no freeze.
        const approved = ['Approved', 'Provisioning', 'Orienting', 'Ready', 'Failed', 'Interrupted', 'Uncertain'].includes(request.state);
        if (approved) {
            // Frozen selection survives the approval; only the fields the owner
            // approved are shown, never the internal approval identity.
            const frozen = document.createElement('dl'); frozen.className = 'frozen-approval'; frozen.dataset.frozenApproval = request.id;
            for (const pair of [
                frozenRow('Approved profile revision', request.containerProfileRevisionId),
                frozenRow('Verified profile build', request.profileBuildId),
                frozenRow('Image digest', request.approvedImageDigest),
                frozenRow('Execution host', request.approvedHostId),
                frozenRow('Managed employee', request.employeeId),
                frozenRow('Runtime binding', request.runtimeBindingId),
                frozenRow('Worker', request.workerId),
                frozenRow('Status detail', request.statusDetail),
            ]) frozen.append(...pair);
            const provisioningNote = document.createElement('span'); provisioningNote.className = 'control-note'; provisioningNote.dataset.provisioningNote = request.id;
            provisioningNote.textContent = request.employeeId
                ? 'Approval created the employee identity and runtime binding, then durably queued provisioning and orientation. The work runs in the background and resumes from the recorded state after a restart.'
                : 'Approval records the frozen selection. No employee identity is bound yet.';
            card.append(title, meta, detail, purpose, actions, frozen, provisioningNote);
            return card;
        }

        if (request.state !== 'Requested') {
            // Rejected (or any other non-approved terminal state): no selection is
            // offered and no freeze is implied.
            card.append(title, meta, detail, purpose, actions);
            return card;
        }

        if (request.placement === 'DeveloperContainer') {
            const profileSelect = document.createElement('select'); profileSelect.className = 'approve-profile'; profileSelect.dataset.approveProfile = request.id; profileSelect.setAttribute('aria-label', 'Approved container profile revision');
            const hostSelect = document.createElement('select'); hostSelect.className = 'approve-host'; hostSelect.dataset.approveHost = request.id; hostSelect.setAttribute('aria-label', 'Execution host');
            const approve = document.createElement('button'); approve.type = 'button'; approve.className = 'btn'; approve.textContent = 'Approve';
            const explanation = document.createElement('span'); explanation.className = 'control-note'; explanation.dataset.approveExplanation = request.id;
            const refreshSelectors = () => {
                const revisionId = profileSelect.value;
                hostSelect.replaceChildren();
                for (const host of hostsForRevision(revisionId)) { const option = document.createElement('option'); option.value = host.id; option.textContent = `${host.displayName} (${host.id})`; hostSelect.append(option); }
                const selectable = activeRevisions.length > 0 && hostsForRevision(revisionId).length > 0;
                approve.disabled = !selectable;
                hostSelect.disabled = !selectable;
                explanation.textContent = selectable
                    ? 'Approval freezes this exact profile revision and host, creates the managed employee identity and runtime binding, then durably queues provisioning and orientation to Ready.'
                    : activeRevisions.length === 0
                        ? 'No active profile revision has a verified image build on a ready host yet. Build and verify a profile revision first; approval then creates the employee identity and binding without provisioning.'
                        : 'No ready host carries the verified build for that profile revision. Choose another revision or verify a build on a ready host.';
            };
            for (const revision of activeRevisions) { const option = document.createElement('option'); option.value = revision.id; option.textContent = `${revision.profileName} r${revision.revisionNumber}`; profileSelect.append(option); }
            profileSelect.disabled = activeRevisions.length === 0;
            profileSelect.addEventListener('change', refreshSelectors);
            approve.addEventListener('click', async () => {
                receipt.dataset.status = 'pending'; receipt.textContent = `Approving ${request.id}…`;
                try {
                    await fetchJson(`/api/hire-requests/${encodeURIComponent(request.id)}/approve`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ expectedRevision: request.revision, profileRevisionId: profileSelect.value, hostId: hostSelect.value }) });
                    await load();
                    receipt.dataset.status = 'ok'; receipt.textContent = `Approved ${request.id}. The employee identity and binding were created and provisioning is queued; it runs in the background.`;
                } catch (error) { receipt.dataset.status = 'error'; receipt.textContent = `Approval failed: ${error.message}`; }
            });
            refreshSelectors();
            actions.append(approve);
            card.append(title, meta, detail, purpose, actions, profileSelect, hostSelect, explanation);
            return card;
        }

        const explanation = document.createElement('span'); explanation.className = 'control-note'; explanation.dataset.approveExplanation = request.id;
        explanation.textContent = 'Only a DeveloperContainer hire can be approved in this phase. This placement has no verified profile build to select.';
        card.append(title, meta, detail, purpose, actions, explanation);
        return card;
    };

    const render = (requests) => {
        list.replaceChildren();
        if (!requests.length) { const empty = document.createElement('p'); empty.className = 'empty-state'; empty.textContent = 'No hire requests yet.'; list.append(empty); }
        for (const request of requests) list.append(renderRequestCard(request));
        status.textContent = `${requests.length} durable request${requests.length === 1 ? '' : 's'}`; delete status.dataset.status; list.hidden = false;
    };

    // Load the active current profile revisions and the exact revisions that have
    // a verified build on a ready host. The build list is the only authority for
    // selectability; the server re-validates the selection at approval time.
    async function loadApprovalOptions() {
        approvedBuilds.clear();
        activeRevisions = [];
        readyHosts = [];
        let profiles = [];
        try { profiles = await fetchJson('/api/profiles'); } catch { profiles = []; }
        try { const hosts = await fetchJson('/api/execution-hosts'); readyHosts = hosts.filter((host) => host.enabled && host.status === 'ready'); } catch { readyHosts = []; }
        await Promise.all(profiles.filter((profile) => profile.status === 'active' && profile.currentRevisionId).map(async (profile) => {
            let builds = [];
            try { builds = await fetchJson(`/api/profiles/${encodeURIComponent(profile.id)}/revisions/${encodeURIComponent(profile.currentRevisionId)}/builds`); } catch { builds = []; }
            const verifiedHosts = builds.filter((build) => build.state === 'built' && build.verified).map((build) => build.hostId);
            if (verifiedHosts.length === 0) return;
            approvedBuilds.set(profile.currentRevisionId, verifiedHosts);
            activeRevisions.push({ id: profile.currentRevisionId, profileName: profile.displayName, revisionNumber: profile.currentRevisionNumber, revisionId: profile.currentRevisionId });
        }));
        activeRevisions.sort((left, right) => left.profileName.localeCompare(right.profileName));
    }

    async function load() { try { render(await fetchJson('/api/hire-requests')); } catch (error) { status.textContent = `Hire requests unavailable: ${error.message}`; status.dataset.status = 'error'; } }
    department.addEventListener('change', rolesForDepartment);
    form.addEventListener('submit', async (event) => {
        event.preventDefault();
        try {
            if (submit.disabled || !role.value) throw new Error('Choose a department with a staffable role.');
            receipt.dataset.status = 'pending';
            receipt.textContent = 'Creating durable request…';
            const values = new FormData(form); const key = newIdempotencyKey();
            const body = { idempotencyKey: key, requestedDisplayName: values.get('requestedDisplayName'), purpose: values.get('purpose'), departmentId: values.get('departmentId'), roleId: values.get('roleId'), placement: values.get('placement'), cpuLimit: Number(values.get('cpuLimit')), memoryLimitMiB: Number(values.get('memoryLimitMiB')), pidsLimit: Number(values.get('pidsLimit')) };
            const created = await fetchJson('/api/hire-requests', { method: 'POST', headers: { 'Content-Type': 'application/json', 'Idempotency-Key': key }, body: JSON.stringify(body) });
            // Reset the form but keep the department the owner was working in
            // (including a query-preselected one) so the next request does not
            // jump to an unrelated staffable department.
            const workingDepartment = department.value;
            form.reset(); department.value = workingDepartment; rolesForDepartment(); await load();
            receipt.dataset.status = 'ok'; receipt.textContent = `Created request ${created.id}. No employee has been created.`;
        }
        catch (error) { receipt.dataset.status = 'error'; receipt.textContent = `Request failed: ${error.message}`; }
    });
    const params = new URLSearchParams(location.search);
    const requestedDepartmentId = params.get('departmentId');
    const requestedDepartmentSlug = params.get('department');
    Promise.all([
        fetchJson('/api/organization/portal'),
        loadApprovalOptions(),
    ]).then(([data]) => {
        organization = data;
        for (const item of data.departments) { const option = document.createElement('option'); option.value = item.id; option.textContent = item.displayName; department.append(option); }
        const staffable = data.departments.filter((item) => data.roles.some((role) => role.departmentId === item.id));
        // A query parameter only preselects an authoritative department. A
        // forged, unknown, or unstaffable value is ignored and the server still
        // validates the department/role relationship on create.
        const requested = data.departments.find((item) => item.id === requestedDepartmentId)
            || data.departments.find((item) => item.slug === requestedDepartmentSlug);
        const selected = requested || staffable[0];
        if (selected) department.value = selected.id;
        rolesForDepartment();
        return load();
    }).catch((error) => { status.textContent = `Hiring unavailable: ${error.message}`; status.dataset.status = 'error'; });
}
