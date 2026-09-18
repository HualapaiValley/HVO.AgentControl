import { fetchJson, rows } from '/js/page-common.js';
const root = document.querySelector('[data-profile-detail]');
if (root) {
    const id = root.dataset.profileId || '';
    const status = root.querySelector('[data-page-status]');
    const receipt = root.querySelector('[data-profile-receipt]');
    const content = root.querySelector('[data-profile-content]');
    const retire = root.querySelector('[data-profile-retire]');
    const form = root.querySelector('[data-revision-form]');
    const definitionInput = form.elements.definition;
    const fragmentInput = form.elements.dockerfileFragment;
    let profile = null;

    const pretty = (json) => { try { return JSON.stringify(JSON.parse(json), null, 2); } catch { return json; } };
    const renderRevision = (revision) => {
        const card = document.createElement('article'); card.className = 'request-card'; card.dataset.revisionId = revision.id; card.dataset.revisionNumber = String(revision.revisionNumber);
        const heading = document.createElement('h3'); heading.textContent = `Revision ${revision.revisionNumber}${profile && revision.revisionNumber === profile.currentRevisionNumber ? ' (current)' : ''}`;
        const list = document.createElement('dl');
        rows(list, [
            ['Revision ID', revision.id],
            ['Base image', revision.baseImageReference],
            ['Content hash', revision.contentHash],
            ['Build status', revision.buildStatus],
            ['Built image digest', revision.builtImageDigest],
            ['Verified', revision.verified ? 'yes' : 'no'],
            ['Created by', revision.createdBy],
            ['Created', new Date(revision.createdAt).toLocaleString()],
        ]);
        const definition = document.createElement('pre'); definition.className = 'profile-definition'; definition.textContent = pretty(revision.definition);
        card.append(heading, list, definition);
        if (revision.dockerfileFragment) { const fragment = document.createElement('pre'); fragment.className = 'profile-definition'; fragment.textContent = revision.dockerfileFragment; card.append(fragment); }
        return card;
    };

    const render = (data) => {
        profile = data.profile;
        root.querySelector('[data-profile-name]').textContent = profile.displayName;
        root.querySelector('[data-profile-subtitle]').textContent = `${profile.slug} · ${profile.status} · profile revision ${profile.revision}`;
        rows(root.querySelector('[data-profile-identity]'), [
            ['Display name', profile.displayName],
            ['Stable ID', profile.id],
            ['Slug', profile.slug],
            ['Status', profile.status],
            ['Description', profile.description || 'No description.'],
            ['Profile revision', profile.revision],
        ]);
        rows(root.querySelector('[data-profile-current]'), [
            ['Revision number', profile.currentRevisionNumber],
            ['Revision ID', profile.currentRevisionId],
            ['Content hash', profile.currentContentHash],
            ['Build status', profile.currentBuildStatus],
        ]);
        const revisions = root.querySelector('[data-profile-revisions]');
        revisions.replaceChildren();
        for (const revision of data.revisions) revisions.append(renderRevision(revision));
        const current = data.revisions.find((item) => item.revisionNumber === profile.currentRevisionNumber);
        if (current && !definitionInput.value) { definitionInput.value = pretty(current.definition); fragmentInput.value = current.dockerfileFragment || ''; }
        const active = profile.status === 'active';
        retire.hidden = !active;
        form.querySelector('[data-revision-submit]').disabled = !active;
        status.textContent = `${profile.displayName} · ${data.revisions.length} revision${data.revisions.length === 1 ? '' : 's'}`;
        delete status.dataset.status;
        content.hidden = false;
    };

    async function load() {
        try { render(await fetchJson(`/api/profiles/${encodeURIComponent(id)}`)); }
        catch (error) {
            status.textContent = error.status === 404
                ? 'Profile not found. No container profile carries that stable id.'
                : error.status === 400
                    ? 'Invalid profile id. The address is not a bounded stable container profile id.'
                    : `Profile unavailable: ${error.message}`;
            status.dataset.status = 'error';
            content.hidden = true;
            retire.hidden = true;
        }
    }

    form.addEventListener('submit', async (event) => {
        event.preventDefault();
        if (!profile) return;
        try {
            receipt.dataset.status = 'pending'; receipt.textContent = 'Creating revision…';
            const fragment = fragmentInput.value;
            const created = await fetchJson(`/api/profiles/${encodeURIComponent(id)}/revisions`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ expectedProfileRevision: profile.revision, definition: definitionInput.value, dockerfileFragment: fragment.trim() ? fragment : null }) });
            await load();
            receipt.dataset.status = 'ok'; receipt.textContent = `Created revision ${created.revisionNumber} (${created.id}). Existing employees were not rebuilt.`;
        } catch (error) { receipt.dataset.status = 'error'; receipt.textContent = `Revision failed: ${error.message}`; }
    });

    retire.addEventListener('click', async () => {
        if (!profile) return;
        try {
            receipt.dataset.status = 'pending'; receipt.textContent = 'Retiring profile…';
            await fetchJson(`/api/profiles/${encodeURIComponent(id)}/retire`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ expectedRevision: profile.revision }) });
            await load();
            receipt.dataset.status = 'ok'; receipt.textContent = 'Profile retired. Existing revisions remain readable.';
        } catch (error) { receipt.dataset.status = 'error'; receipt.textContent = `Retire failed: ${error.message}`; }
    });

    load();
}
