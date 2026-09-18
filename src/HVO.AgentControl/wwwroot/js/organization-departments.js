import { fetchJson, availabilitySummary } from '/js/page-common.js';
const root = document.querySelector('[data-organization-departments-page]');
if (root) {
    const status = root.querySelector('[data-page-status]');
    const list = root.querySelector('[data-department-directory]');
    fetchJson('/api/organization/portal').then((data) => {
        list.replaceChildren();
        if (!data.departments.length) {
            const empty = document.createElement('li');
            empty.className = 'empty-state';
            empty.textContent = 'No authoritative departments are persisted.';
            list.append(empty);
        }
        list.classList.add('directory-list');
        for (const department of data.departments) {
            const roles = data.roles.filter((role) => role.departmentId === department.id);
            const item = document.createElement('li');
            item.className = 'directory-card department-directory-card';
            item.dataset.departmentId = department.id;
            item.dataset.departmentSlug = department.slug;
            const link = document.createElement('a');
            link.href = `/organization/departments/${encodeURIComponent(department.id)}`;
            const heading = document.createElement('strong'); heading.textContent = department.displayName;
            const meta = document.createElement('span');
            meta.textContent = `${department.employeeCount} employee${department.employeeCount === 1 ? '' : 's'} · ${roles.length} role${roles.length === 1 ? '' : 's'} · ${availabilitySummary(department.availability)}`;
            const rolesText = document.createElement('span');
            rolesText.textContent = roles.length
                ? `Roles: ${roles.map((role) => role.displayName).join(', ')}`
                : 'No roles assigned to this department.';
            const identity = document.createElement('span');
            identity.className = 'mono';
            identity.textContent = `stable id ${department.id} · slug ${department.slug}`;
            link.append(heading, meta, rolesText, identity);
            item.append(link);
            list.append(item);
        }
        status.textContent = `${data.departments.length} authoritative department${data.departments.length === 1 ? '' : 's'}`;
        delete status.dataset.status;
        list.hidden = false;
    }).catch((error) => { status.textContent = `Department directory unavailable: ${error.message}`; status.dataset.status = 'error'; });
}
