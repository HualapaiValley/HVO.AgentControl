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
    const buildForm = root.querySelector('[data-build-form]');
    const buildHost = buildForm.elements.hostId;
    const buildSubmit = root.querySelector('[data-build-submit]');
    const buildReceipt = root.querySelector('[data-build-receipt]');
    const noBuildHosts = root.querySelector('[data-no-build-hosts]');
    let profile = null;
    let hosts = [];
    const buildsByRevision = new Map();

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
        const builds = document.createElement('div'); builds.className = 'build-list'; builds.dataset.revisionBuilds = revision.id;
        const known = buildsByRevision.get(revision.id) || [];
        if (!known.length) { const none = document.createElement('p'); none.className = 'control-note'; none.textContent = 'No builds recorded on any host.'; builds.append(none); }
        for (const build of known) {
            const row = document.createElement('p'); row.dataset.buildId = build.id; row.dataset.buildState = build.state;
            row.textContent = `${build.hostId}: ${build.state}${build.verified ? ' (verified)' : ''}${build.imageDigest ? ' · ' + build.imageDigest : ''}${build.failureSummary ? ' · ' + build.failureSummary : ''} · ${new Date(build.updatedAt).toLocaleString()}`;
            builds.append(row);
        }
        card.append(heading, list, definition, builds);
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

    const renderHosts = () => {
        buildHost.replaceChildren();
        const ready = hosts.filter((h) => h.enabled && h.status === 'ready');
        for (const h of ready) { const option = document.createElement('option'); option.value = h.id; option.textContent = `${h.displayName} (${h.id})`; buildHost.append(option); }
        const usable = ready.length > 0 && profile && profile.status === 'active';
        buildHost.disabled = !usable; buildSubmit.disabled = !usable; noBuildHosts.hidden = ready.length > 0;
    };
    async function loadBuilds(data) {
        buildsByRevision.clear();
        await Promise.all(data.revisions.map(async (revision) => {
            try { buildsByRevision.set(revision.id, await fetchJson(`/api/profiles/${encodeURIComponent(id)}/revisions/${encodeURIComponent(revision.id)}/builds`)); }
            catch { buildsByRevision.set(revision.id, []); }
        }));
    }
    async function load() {
        try {
            const data = await fetchJson(`/api/profiles/${encodeURIComponent(id)}`);
            await loadBuilds(data);
            try { hosts = await fetchJson('/api/execution-hosts'); } catch { hosts = []; }
            render(data); renderHosts();
        }
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

    buildForm.addEventListener('submit', async (event) => {
        event.preventDefault();
        if (!profile || buildSubmit.disabled) return;
        try {
            buildReceipt.dataset.status = 'pending'; buildReceipt.textContent = `Building revision ${profile.currentRevisionNumber} on ${buildHost.value}… this runs docker build on the host and can take several minutes.`;
            const build = await fetchJson(`/api/profiles/${encodeURIComponent(id)}/revisions/${encodeURIComponent(profile.currentRevisionId)}/builds`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ hostId: buildHost.value }) });
            await load();
            buildReceipt.dataset.status = build.state === 'built' && build.verified ? 'ok' : 'error';
            buildReceipt.textContent = build.state === 'built' && build.verified
                ? `Built and verified on ${build.hostId}: ${build.imageDigest}. No employee was provisioned.`
                : `Build ${build.state} on ${build.hostId}${build.failureSummary ? ': ' + build.failureSummary : ''}.`;
        } catch (error) { buildReceipt.dataset.status = 'error'; buildReceipt.textContent = `Build failed: ${error.message}`; }
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
