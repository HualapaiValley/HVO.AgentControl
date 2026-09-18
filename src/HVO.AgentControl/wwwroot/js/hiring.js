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
    const render = (requests) => {
        list.replaceChildren();
        if (!requests.length) { const empty = document.createElement('p'); empty.className = 'empty-state'; empty.textContent = 'No hire requests yet.'; list.append(empty); }
        for (const request of requests) {
            const card = document.createElement('article'); card.className = 'request-card'; card.dataset.requestId = request.id;
            const title = document.createElement('h3'); title.textContent = request.requestedDisplayName;
            const meta = document.createElement('p'); meta.textContent = `${request.state} · ${new Date(request.createdAt).toLocaleString()} · ${request.id}`;
            const detail = document.createElement('p'); detail.textContent = `${request.departmentDisplayName} · ${request.roleDisplayName} · ${request.placement} · ${request.cpuLimit} CPU / ${request.memoryLimitMiB} MiB / ${request.pidsLimit} PIDs`;
            const purpose = document.createElement('p'); purpose.textContent = request.purpose;
            const actions = document.createElement('div'); actions.className = 'request-actions';
            const reject = document.createElement('button'); reject.type = 'button'; reject.className = 'btn'; reject.textContent = 'Reject'; reject.disabled = request.state !== 'Requested'; reject.addEventListener('click', async () => { receipt.dataset.status = 'pending'; receipt.textContent = `Rejecting ${request.id}…`; try { await fetchJson(`/api/hire-requests/${encodeURIComponent(request.id)}/reject`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ expectedRevision: request.revision }) }); await load(); receipt.dataset.status = 'ok'; receipt.textContent = `Rejected ${request.id}.`; } catch (error) { receipt.dataset.status = 'error'; receipt.textContent = `Reject failed: ${error.message}`; } });
            const approve = document.createElement('button'); approve.type = 'button'; approve.className = 'btn'; approve.textContent = 'Approve'; approve.disabled = true; approve.title = 'Approval and provisioning remain pending #219 discussion and owner approval.'; approve.setAttribute('aria-describedby', `approval-${request.id}`);
            const explanation = document.createElement('span'); explanation.id = `approval-${request.id}`; explanation.className = 'control-note'; explanation.textContent = 'Approval and provisioning remain pending #219 discussion and owner approval.';
            actions.append(reject, approve, explanation); card.append(title, meta, detail, purpose, actions); list.append(card);
        }
        status.textContent = `${requests.length} durable request${requests.length === 1 ? '' : 's'}`; delete status.dataset.status; list.hidden = false;
    };
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
    fetchJson('/api/organization/portal').then((data) => {
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
