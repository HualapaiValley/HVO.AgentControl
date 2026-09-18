import { fetchJson } from '/js/page-common.js';
const root = document.querySelector('[data-profiles-page]');
if (root) {
    const form = root.querySelector('[data-profile-form]');
    const status = root.querySelector('[data-page-status]');
    const receipt = root.querySelector('[data-profile-receipt]');
    const list = root.querySelector('[data-profile-list]');
    const newIdempotencyKey = () => {
        if (!globalThis.crypto || typeof globalThis.crypto.getRandomValues !== 'function') throw new Error('Secure random generation is unavailable.');
        if (typeof globalThis.crypto.randomUUID === 'function') return globalThis.crypto.randomUUID();
        const bytes = globalThis.crypto.getRandomValues(new Uint8Array(16));
        bytes[6] = (bytes[6] & 0x0f) | 0x40;
        bytes[8] = (bytes[8] & 0x3f) | 0x80;
        const hex = [...bytes].map((value) => value.toString(16).padStart(2, '0')).join('');
        return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
    };
    const render = (profiles) => {
        list.replaceChildren();
        if (!profiles.length) { const empty = document.createElement('p'); empty.className = 'empty-state'; empty.textContent = 'No container profiles yet.'; list.append(empty); }
        for (const profile of profiles) {
            const card = document.createElement('article'); card.className = 'request-card'; card.dataset.profileId = profile.id; card.dataset.profileStatus = profile.status;
            const title = document.createElement('h3'); const link = document.createElement('a'); link.href = `/profiles/${encodeURIComponent(profile.id)}`; link.textContent = profile.displayName; title.append(link);
            const meta = document.createElement('p'); meta.textContent = `${profile.slug} · ${profile.status} · revision ${profile.currentRevisionNumber} (${profile.currentBuildStatus}) · ${profile.id}`;
            const description = document.createElement('p'); description.textContent = profile.description || 'No description.';
            const hash = document.createElement('p'); hash.className = 'control-note'; hash.textContent = `Current content hash ${profile.currentContentHash}`;
            card.append(title, meta, description, hash); list.append(card);
        }
        status.textContent = `${profiles.length} profile${profiles.length === 1 ? '' : 's'}`; delete status.dataset.status; list.hidden = false;
    };
    async function load() { try { render(await fetchJson('/api/profiles')); } catch (error) { status.textContent = `Container profiles unavailable: ${error.message}`; status.dataset.status = 'error'; } }
    form.addEventListener('submit', async (event) => {
        event.preventDefault();
        try {
            receipt.dataset.status = 'pending';
            receipt.textContent = 'Creating profile…';
            const values = new FormData(form); const key = newIdempotencyKey();
            const fragment = String(values.get('dockerfileFragment') || '');
            const body = { idempotencyKey: key, slug: values.get('slug'), displayName: values.get('displayName'), description: values.get('description'), definition: values.get('definition'), dockerfileFragment: fragment.trim() ? fragment : null };
            const created = await fetchJson('/api/profiles', { method: 'POST', headers: { 'Content-Type': 'application/json', 'Idempotency-Key': key }, body: JSON.stringify(body) });
            form.reset(); await load();
            receipt.dataset.status = 'ok'; receipt.textContent = `Created profile ${created.id} (revision 1). No image was built and no employee was created.`;
        }
        catch (error) { receipt.dataset.status = 'error'; receipt.textContent = `Create failed: ${error.message}`; }
    });
    load();
}
