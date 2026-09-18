import { fetchJson } from '/js/page-common.js';
const root = document.querySelector('[data-system-page]');
if (root) {
    const status = root.querySelector('[data-page-status]'); const receipt = root.querySelector('[data-config-receipt]');
    const name = root.querySelector('[data-org-name-input]'); const instructions = root.querySelector('[data-org-instructions]'); const roleSelect = root.querySelector('[data-role-select]'); const roleInstructions = root.querySelector('[data-role-instructions]');
    let data = null; const drafts = new Map();
    // The selected role is tracked independently of the <select> value so an
    // unrelated organization reload (which rebuilds the options) cannot silently
    // fall back to the first role.
    let selectedRoleId = '';
    const roleKey = (id = selectedRoleId) => `role:${id}`;
    const cueFor = (key) => key === 'name'
        ? root.querySelector('[data-org-name-draft-state]')
        : key === 'instructions'
            ? root.querySelector('[data-org-instructions-draft-state]')
            : root.querySelector('[data-role-instructions-draft-state]');
    function mark(key, input, reset) {
        const draft = drafts.get(key); const conflict = draft?.conflict === true;
        input.dataset.draftState = conflict ? 'conflict' : draft ? 'dirty' : 'clean';
        if (conflict) input.setAttribute('aria-invalid', 'true'); else input.removeAttribute('aria-invalid');
        const cue = cueFor(key); cue.hidden = !draft; cue.dataset.draftState = input.dataset.draftState; cue.textContent = conflict ? 'Conflict: authority changed after this draft began. Discard or review before retrying.' : 'Unsaved draft retained locally.';
        reset.hidden = !draft;
    }
    function begin(key, input, revision) { const current = drafts.get(key); drafts.set(key, { value: input.value, baseRevision: current?.baseRevision ?? revision, conflict: current?.conflict === true }); }
    function render() {
        name.value = drafts.get('name')?.value ?? data.displayName; instructions.value = drafts.get('instructions')?.value ?? data.basicInstructions;
        roleSelect.replaceChildren(); for (const role of data.roles) { const option = document.createElement('option'); option.value = role.id; option.textContent = role.displayName; roleSelect.append(option); }
        root.querySelector('[data-system-forms]').hidden = false; root.querySelectorAll('[data-system-control]').forEach((control) => { control.disabled = false; });
        status.dataset.status = 'ok'; status.textContent = `Authoritative revision ${data.revision}`;
        renderRole();
        mark('name', name, root.querySelector('[data-reset-org-name]')); mark('instructions', instructions, root.querySelector('[data-reset-org-instructions]'));
    }
    function renderRole() {
        const role = data.roles.find((item) => item.id === selectedRoleId) || data.roles[0];
        if (!role) {
            selectedRoleId = ''; roleSelect.value = ''; roleSelect.disabled = true;
            roleInstructions.value = ''; roleInstructions.disabled = true;
            const reset = root.querySelector('[data-reset-role-instructions]'); reset.hidden = true;
            const cue = cueFor(roleKey()); cue.hidden = true;
            root.querySelector('[data-role-instructions-form] button[type="submit"]').disabled = true;
            status.dataset.status = 'error';
            status.textContent = 'No authoritative roles are available to configure.';
            return;
        }
        selectedRoleId = role.id; roleSelect.value = role.id; roleSelect.disabled = false;
        const key = roleKey();
        roleInstructions.disabled = false;
        roleInstructions.value = drafts.get(key)?.value ?? role.standingInstructions;
        mark(key, roleInstructions, root.querySelector('[data-reset-role-instructions]'));
        const submit = root.querySelector('[data-role-instructions-form] button[type="submit"]');
        submit.disabled = false;
    }
    async function reload() { const next = await fetchJson('/api/organization/portal'); for (const [key, draft] of drafts) { const revision = key === 'name' || key === 'instructions' ? next.revision : next.roles.find((x) => `role:${x.id}` === key)?.revision; if (revision !== draft.baseRevision) draft.conflict = true; } data = next; render(); }
    async function save(path, method, key, body) { const draft = drafts.get(key); if (!draft) return; receipt.dataset.status = 'pending'; receipt.textContent = 'Saving authoritative changes…'; try { await fetchJson(path, { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body(draft)) }); const current = drafts.get(key); if (current === draft) drafts.delete(key); } catch (error) { if (error.status === 409) { draft.conflict = true; try { await reload(); } catch { /* the error receipt below reports the conflict */ } } receipt.dataset.status = 'error'; receipt.textContent = `Update failed: ${error.message}`; render(); return; } receipt.dataset.status = 'ok'; receipt.textContent = 'Saved. Current orientation is stale.'; try { await reload(); } catch (error) { status.dataset.status = 'error'; status.textContent = `System configuration unavailable after save: ${error.message}`; receipt.dataset.status = 'error'; receipt.textContent = `Saved, but reloading authority failed: ${error.message}`; } }
    name.addEventListener('input', () => { begin('name', name, data.revision); mark('name', name, root.querySelector('[data-reset-org-name]')); }); instructions.addEventListener('input', () => { begin('instructions', instructions, data.revision); mark('instructions', instructions, root.querySelector('[data-reset-org-instructions]')); }); roleInstructions.addEventListener('input', () => { const role = data.roles.find((x) => x.id === selectedRoleId); if (role) begin(roleKey(), roleInstructions, role.revision); mark(roleKey(), roleInstructions, root.querySelector('[data-reset-role-instructions]')); }); roleSelect.addEventListener('change', () => { selectedRoleId = roleSelect.value; renderRole(); });
    root.querySelector('[data-reset-org-name]').addEventListener('click', () => { drafts.delete('name'); render(); }); root.querySelector('[data-reset-org-instructions]').addEventListener('click', () => { drafts.delete('instructions'); render(); }); root.querySelector('[data-reset-role-instructions]').addEventListener('click', () => { drafts.delete(roleKey()); renderRole(); });
    root.querySelector('[data-org-name-form]').addEventListener('submit', (event) => { event.preventDefault(); save('/api/organization', 'PATCH', 'name', (draft) => ({ organizationId: data.id, revision: draft.baseRevision, displayName: draft.value })); });
    root.querySelector('[data-org-instructions-form]').addEventListener('submit', (event) => { event.preventDefault(); save('/api/organization/basic-instructions', 'PUT', 'instructions', (draft) => ({ organizationId: data.id, revision: draft.baseRevision, basicInstructions: draft.value })); });
    root.querySelector('[data-role-instructions-form]').addEventListener('submit', (event) => { event.preventDefault(); const id = selectedRoleId; if (!id) return; save(`/api/roles/${encodeURIComponent(id)}/instructions`, 'PUT', `role:${id}`, (draft) => ({ revision: draft.baseRevision, standingInstructions: draft.value })); });
    reload().catch((error) => { status.textContent = `System configuration unavailable: ${error.message}`; status.dataset.status = 'error'; });
}
