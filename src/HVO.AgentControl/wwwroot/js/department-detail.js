import { fetchJson, rows, label, availabilityBadge } from '/js/page-common.js';
const root = document.querySelector('[data-department-detail]');
if (root) {
    const id = root.dataset.departmentId || '';
    const status = root.querySelector('[data-page-status]');
    const content = root.querySelector('[data-department-content]');
    const hire = root.querySelector('[data-department-hire]');

    const renderRole = (role) => {
        const article = document.createElement('article');
        article.className = 'role-summary';
        article.dataset.roleId = role.id;
        const heading = document.createElement('h3'); heading.textContent = role.displayName;
        const list = document.createElement('dl');
        rows(list, [
            ['Stable ID', role.id],
            ['Slug', role.slug],
            ['Instruction profile', role.instructionProfile],
            ['Permission profile', role.permissionProfile],
            ['Standing instructions', role.standingInstructions],
            ['Revision', role.revision],
        ]);
        article.append(heading, list);
        return article;
    };

    fetchJson(`/api/departments/${encodeURIComponent(id)}`).then((data) => {
        root.querySelector('[data-department-name]').textContent = data.displayName;
        root.querySelector('[data-department-subtitle]').textContent = `${data.organizationDisplayName} · authoritative revision ${data.revision}`;
        rows(root.querySelector('[data-department-identity]'), [
            ['Display name', data.displayName],
            ['Stable ID', data.id],
            ['Slug', data.slug],
            ['Organization', data.organizationDisplayName],
            ['Revision', data.revision],
            ['Standing instructions', data.standingInstructions || 'No department standing instructions are persisted.'],
        ]);
        rows(root.querySelector('[data-department-availability]'), data.availability.map((item) => [label(item.category), item.count]));

        const roles = root.querySelector('[data-department-roles]');
        roles.replaceChildren();
        if (!data.roles.length) {
            const empty = document.createElement('p');
            empty.className = 'empty-state';
            empty.textContent = 'No roles are assigned to this department.';
            roles.append(empty);
        }
        for (const role of data.roles) roles.append(renderRole(role));

        const roster = root.querySelector('[data-department-roster]');
        roster.replaceChildren();
        root.querySelector('[data-department-empty]').hidden = data.employees.length > 0;
        for (const employee of data.employees) {
            const item = document.createElement('li');
            item.className = 'directory-card department-roster-card';
            item.dataset.employeeId = employee.id;
            item.dataset.availability = employee.availability;
            const link = document.createElement('a');
            link.href = `/employees/${encodeURIComponent(employee.id)}`;
            const heading = document.createElement('strong'); heading.textContent = employee.displayName;
            const availability = document.createElement('span');
            availabilityBadge(availability, employee.availability);
            const meta = document.createElement('span');
            meta.textContent = `${employee.roleDisplayName} · ${employee.purpose}`;
            link.append(heading, availability, meta);
            item.append(link);
            roster.append(item);
        }

        const failures = root.querySelector('[data-department-failures]');
        const failureList = root.querySelector('[data-department-failure-list]');
        failureList.replaceChildren();
        if (data.failuresNeedingAttention.length) {
            for (const failure of data.failuresNeedingAttention) {
                const item = document.createElement('li');
                const link = document.createElement('a');
                link.href = failure.url;
                link.textContent = `${failure.employeeDisplayName}: ${failure.summary}`;
                item.append(link);
                failureList.append(item);
            }
            failures.hidden = false;
        } else {
            failures.hidden = true;
        }

        hire.href = data.hireUrl;
        hire.hidden = false;
        status.textContent = `${data.displayName} · ${data.employees.length} employee${data.employees.length === 1 ? '' : 's'}`;
        delete status.dataset.status;
        content.hidden = false;
    }).catch((error) => {
        const message = error.status === 404
            ? 'Department not found. No department carries that stable id.'
            : error.status === 400
                ? 'Invalid department id. The address is not a bounded stable department id.'
                : `Department unavailable: ${error.message}`;
        status.textContent = message;
        status.dataset.status = 'error';
        content.hidden = true;
        hire.hidden = true;
    });
}
