const portal = document.querySelector("[data-portal]");
const root = document.querySelector("[data-org-overview]");

if (portal && root) {
    const state = { organization: null, employee: null, view: "overview" };
    const text = (value) => value === null || value === undefined || value === "" ? "\u2014" : String(value);
    const label = (value) => String(value || "unknown").replaceAll("-", " ");
    const element = (selector) => root.querySelector(selector);

    function rows(target, values) {
        target.replaceChildren();
        for (const [term, value] of values) {
            const dt = document.createElement("dt");
            dt.textContent = term;
            const dd = document.createElement("dd");
            dd.textContent = text(value);
            target.append(dt, dd);
        }
    }

    function navigate(view, employeeId = null) {
        state.view = view;
        root.querySelectorAll("[data-view]").forEach((panel) => panel.hidden = panel.dataset.view !== view);
        document.querySelectorAll("[data-nav]").forEach((button) => {
            const selected = button.dataset.nav === view;
            button.setAttribute("aria-current", selected ? "page" : "false");
        });
        if (employeeId) history.replaceState(null, "", `#employee/${encodeURIComponent(employeeId)}`);
        else history.replaceState(null, "", `#${view}`);
    }

    async function selectEmployee(id) {
        const response = await fetch(`/api/employees/${encodeURIComponent(id)}`, { credentials: "same-origin" });
        if (!response.ok) {
            element("[data-org-state]").textContent = `Employee detail unavailable (HTTP ${response.status}).`;
            return;
        }
        state.employee = await response.json();
        const employee = state.employee;
        document.querySelectorAll("[data-selected-employee-name]").forEach((node) => node.textContent = employee.displayName);
        element("[data-employee-name]").textContent = employee.displayName;
        element("[data-employee-availability]").textContent = label(employee.availability);
        rows(element("[data-employee-identity]"), [
            ["Stable ID", employee.id], ["Organization ID", employee.organizationId],
            ["Department", `${employee.departmentDisplayName} (${employee.departmentId})`],
            ["Role", `${employee.roleDisplayName} (${employee.roleId})`], ["Purpose", employee.purpose],
            ["Instructions", employee.instructions], ["Rules", employee.rules], ["Restrictions", employee.restrictions],
        ]);
        rows(element("[data-employee-runtime]"), [
            ["Binding ID", employee.runtime.bindingId], ["Placement", employee.runtime.placement],
            ["Host owned", employee.runtime.hostOwned], ["Native session ID", employee.runtime.nativeSessionId],
            ["Session title", employee.runtime.sessionTitle], ["Control status", employee.runtime.controlStatus],
            ["Session state", employee.runtime.sessionState], ["Observed model", employee.runtime.controlModel],
            ["Terminal", employee.terminal.available ? employee.terminal.url : employee.terminal.reason],
        ]);
        const orientation = employee.orientation;
        rows(element("[data-employee-orientation]"), orientation ? [
            ["Assignment", orientation.assignmentId], ["Version", orientation.orientationVersion],
            ["State", orientation.state], ["Revision", orientation.revision],
            ["Restart required", orientation.restartRequired], ["Dispatch held", orientation.dispatchHeld],
            ["Hold reasons", (orientation.holdReasons || []).join(", ") || "None"],
            ["Evidence source", orientation.evidenceSource], ["Last error", orientation.lastError],
        ] : [["State", "No orientation assignment"]]);
        rows(element("[data-employee-diagnostics]"), [
            ["Sanitized runtime error", employee.runtime.sanitizedError],
            ["Recent logs", `${employee.recentLogs.supported ? "Supported" : "Unsupported"}: ${employee.recentLogs.reason}`],
        ]);
        const actionsEnabled = employee.runtime.hostOwned === true;
        root.querySelectorAll("[data-orientation-deliver],[data-orientation-comprehension],[data-orientation-hold]")
            .forEach((button) => button.disabled = !actionsEnabled);
        const hold = element("[data-orientation-hold]");
        hold.dataset.held = orientation?.holdReasons?.includes("manual") ? "true" : "false";
        hold.textContent = hold.dataset.held === "true" ? "Clear manual hold" : "Set manual hold";
        portal.dataset.selectedEmployeeId = employee.id;
        portal.dispatchEvent(new CustomEvent("agentcontrol:employee-selected", { detail: employee }));
        navigate("employee", employee.id);
    }

    function renderOrganization(data) {
        state.organization = data;
        element("[data-org-state]").textContent = `Authoritative store revision ${data.revision}.`;
        element("[data-org-description]").textContent = text(data.description);
        element("[data-org-basic-instructions]").textContent = text(data.basicInstructions);
        rows(element("[data-org-departments]"), data.departments.map((department) => [department.displayName, `${department.employeeCount} employee${department.employeeCount === 1 ? "" : "s"}`]));
        rows(element("[data-org-availability]"), data.availability.map((count) => [label(count.category), count.count]));
        element("[data-pending-approvals]").textContent = `Unsupported (${data.pendingApprovals.count}): ${data.pendingApprovals.reason}`;
        const failures = element("[data-org-failures]");
        failures.replaceChildren();
        if (!data.failuresNeedingAttention.length) {
            const item = document.createElement("li"); item.textContent = "No current orientation or runtime failures."; failures.append(item);
        }
        for (const failure of data.failuresNeedingAttention) {
            const item = document.createElement("li");
            const link = document.createElement("a"); link.href = failure.url; link.textContent = `${failure.employeeDisplayName}: ${failure.summary}`;
            link.addEventListener("click", (event) => { event.preventDefault(); selectEmployee(failure.employeeId); });
            item.append(link); failures.append(item);
        }
        const operations = element('[data-department-employees="operations"]');
        operations.replaceChildren();
        for (const employee of data.employees.filter((item) => item.departmentSlug === "operations")) {
            const item = document.createElement("li"); const button = document.createElement("button");
            button.type = "button"; button.className = "employee-card"; button.textContent = `${employee.displayName} \u00b7 ${label(employee.availability)}`;
            button.addEventListener("click", () => selectEmployee(employee.id)); item.append(button); operations.append(item);
        }
        element("[data-org-name-input]").value = data.displayName;
        element("[data-org-instructions]").value = data.basicInstructions;
        const role = data.roles.find((item) => item.slug === "operations-it");
        element("[data-role-instructions]").value = role?.standingInstructions || "";
        element("[data-role-instructions-form]").dataset.roleId = role?.id || "";
        element("[data-role-instructions-form]").dataset.revision = String(role?.revision || 0);
    }

    async function load() {
        try {
            const response = await fetch("/api/organization/portal", { credentials: "same-origin" });
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            renderOrganization(await response.json());
            const match = location.hash.match(/^#employee\/(.+)$/);
            if (match) await selectEmployee(decodeURIComponent(match[1]));
            else navigate(location.hash.slice(1) || "overview");
        } catch (error) {
            element("[data-org-state]").textContent = `Organization unavailable: ${error.message}`;
        }
    }

    async function mutate(path, method, body, receipt) {
        receipt.textContent = "Saving authoritative changes...";
        const response = await fetch(path, { method, credentials: "same-origin", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
        if (!response.ok) { receipt.textContent = `Validation/update failed (HTTP ${response.status}).`; return null; }
        receipt.textContent = "Saved. Orientation is stale or requires redelivery/restart/comprehension.";
        await load();
        if (state.employee) await selectEmployee(state.employee.id);
        return response;
    }

    document.querySelectorAll("[data-nav]").forEach((button) => button.addEventListener("click", () => navigate(button.dataset.nav)));
    element("[data-back-to-department]").addEventListener("click", () => navigate(state.employee?.departmentSlug || "overview"));
    element("[data-org-name-form]").addEventListener("submit", (event) => {
        event.preventDefault(); const org = state.organization;
        mutate("/api/organization", "PATCH", { organizationId: org.id, revision: org.revision, displayName: element("[data-org-name-input]").value }, element("[data-config-receipt]"));
    });
    element("[data-org-instructions-form]").addEventListener("submit", (event) => {
        event.preventDefault(); const org = state.organization;
        mutate("/api/organization/basic-instructions", "PUT", { organizationId: org.id, revision: org.revision, basicInstructions: element("[data-org-instructions]").value }, element("[data-config-receipt]"));
    });
    element("[data-role-instructions-form]").addEventListener("submit", (event) => {
        event.preventDefault(); const form = event.currentTarget;
        mutate(`/api/roles/${encodeURIComponent(form.dataset.roleId)}/instructions`, "PUT", { revision: Number(form.dataset.revision), standingInstructions: element("[data-role-instructions]").value }, element("[data-config-receipt]"));
    });
    element("[data-orientation-deliver]").addEventListener("click", () => mutate("/api/orientation/deliver", "POST", {}, element("[data-orientation-receipt]")));
    element("[data-orientation-comprehension]").addEventListener("click", () => mutate("/api/orientation/comprehension/run", "POST", {}, element("[data-orientation-receipt]")));
    element("[data-orientation-hold]").addEventListener("click", (event) => mutate("/api/orientation/manual-hold", "PUT", { held: event.currentTarget.dataset.held !== "true", detail: "Owner portal action" }, element("[data-orientation-receipt]")));
    window.addEventListener("hashchange", () => {
        const match = location.hash.match(/^#employee\/(.+)$/);
        if (match) selectEmployee(decodeURIComponent(match[1]));
        else navigate(location.hash.slice(1) || "overview");
    });

    function loadWhenStoreCanExist() {
        const runtimeState = portal.dataset.runtimeState;
        if (runtimeState === "ready" || runtimeState === "degraded") {
            load();
        } else if (runtimeState === "idle" || runtimeState === "faulted") {
            element("[data-org-state]").textContent = "Organization unavailable: the authoritative store is not open.";
        }
    }
    loadWhenStoreCanExist();
    new MutationObserver(loadWhenStoreCanExist).observe(portal, { attributes: true, attributeFilter: ["data-runtime-state"] });
}
