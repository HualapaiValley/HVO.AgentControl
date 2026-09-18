import { fetchJson, rows, label, availabilitySummary } from '/js/page-common.js';
const root = document.querySelector('[data-organization-page]');
if (root) {
    const status = root.querySelector('[data-page-status]');
    fetchJson('/api/organization/portal').then((data) => {
        status.textContent = `${data.displayName} · authoritative revision ${data.revision}`;
        delete status.dataset.status;
        rows(root.querySelector('[data-org-availability]'), data.availability.map((x) => [label(x.category), x.count]));
        root.querySelector('[data-pending-approvals]').textContent = `${data.pendingApprovals.count} requested · ${data.pendingApprovals.reason}`;
        const cards = root.querySelector('[data-org-department-cards]');
        cards.replaceChildren();
        for (const department of data.departments) {
            const roles = data.roles.filter((role) => role.departmentId === department.id);
            const card = document.createElement('a');
            card.className = 'department-card';
            card.dataset.departmentId = department.id;
            card.dataset.departmentSlug = department.slug;
            card.href = `/organization/departments/${encodeURIComponent(department.id)}`;
            const heading = document.createElement('h3'); heading.textContent = department.displayName;
            const identity = document.createElement('p'); identity.className = 'department-card-identity';
            identity.textContent = `Stable ID ${department.id} · slug ${department.slug}`;
            const counts = document.createElement('p');
            counts.textContent = `${department.employeeCount} employee${department.employeeCount === 1 ? '' : 's'} · ${roles.length} role${roles.length === 1 ? '' : 's'} · ${availabilitySummary(department.availability)}`;
            card.append(heading, identity, counts);
            cards.append(card);
        }
        const failures = root.querySelector('[data-org-failures]');
        failures.replaceChildren();
        const items = data.failuresNeedingAttention.length ? data.failuresNeedingAttention : [{ summary: 'No current orientation or runtime failures.' }];
        for (const item of items) {
            const li = document.createElement('li');
            if (item.url) { const link = document.createElement('a'); link.href = item.url; link.textContent = `${item.employeeDisplayName}: ${item.summary}`; li.append(link); }
            else li.textContent = item.summary;
            failures.append(li);
        }
        root.querySelector('[data-organization-summary]').hidden = false;
        root.querySelector('[data-org-department-cards]').hidden = false;
        root.querySelector('[data-organization-failures]').hidden = false;

        // Resolve a legacy department hash after the authoritative read. The new
        // overview links by stable id; the old page used mutable slugs.
        const legacySlug = location.hash.slice(1);
        const legacy = legacySlug ? data.departments.find((department) => department.slug === legacySlug) : null;
        if (legacy) location.replace(`/organization/departments/${encodeURIComponent(legacy.id)}`);
    }).catch((error) => { status.textContent = `Organization unavailable: ${error.message}`; status.dataset.status = 'error'; });
}
