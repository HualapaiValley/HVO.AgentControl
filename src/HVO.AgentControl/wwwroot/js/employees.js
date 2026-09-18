import { fetchJson, label } from '/js/page-common.js';
const root = document.querySelector('[data-employees-page]');
if (root) {
    const status = root.querySelector('[data-page-status]');
    const list = root.querySelector('[data-employee-directory]');
    const search = root.querySelector('[data-employee-search]');
    const department = root.querySelector('[data-department-filter]');
    const availability = root.querySelector('[data-availability-filter]');
    let employees = [];
    let loaded = false;
    root.querySelector('[data-directory-filters]').addEventListener('submit', (event) => event.preventDefault());
    const render = () => {
        if (!loaded) return;
        const query = (search.value || '').trim().toLowerCase();
        const filtered = employees.filter((employee) => (!query || [employee.displayName, employee.id, employee.purpose, employee.roleDisplayName].some((value) => String(value || '').toLowerCase().includes(query)))
            && (!department.value || employee.departmentSlug === department.value)
            && (!availability.value || employee.availability === availability.value));
        list.replaceChildren();
        if (!filtered.length) { const item = document.createElement('li'); item.className = 'empty-state'; item.textContent = 'No employees match these filters.'; list.append(item); }
        for (const employee of filtered) {
            const item = document.createElement('li'); item.className = 'directory-card';
            const link = document.createElement('a'); link.href = `/employees/${encodeURIComponent(employee.id)}`;
            const heading = document.createElement('strong'); heading.textContent = employee.displayName;
            const meta = document.createElement('span'); meta.textContent = `${employee.departmentDisplayName} · ${employee.roleDisplayName} · ${label(employee.availability)}`;
            const purpose = document.createElement('span'); purpose.textContent = employee.purpose;
            link.append(heading, meta, purpose); item.append(link); list.append(item);
        }
        status.textContent = `${filtered.length} of ${employees.length} employees`;
        delete status.dataset.status;
        list.hidden = false;
    };
    Promise.all([fetchJson('/api/organization/portal')]).then(([data]) => {
        employees = data.employees;
        loaded = true;
        for (const item of data.departments) { const option = document.createElement('option'); option.value = item.slug; option.textContent = item.displayName; department.append(option); }
        for (const item of data.availability.filter((item) => item.count > 0)) { const option = document.createElement('option'); option.value = item.category; option.textContent = label(item.category); availability.append(option); }
        const requestedDepartment = new URLSearchParams(location.search).get('department');
        if ([...department.options].some((option) => option.value === requestedDepartment)) department.value = requestedDepartment;
        render();
    }).catch((error) => { status.textContent = `Employee directory unavailable: ${error.message}`; status.dataset.status = 'error'; });
    [search, department, availability].forEach((control) => control.addEventListener('input', render));
}
