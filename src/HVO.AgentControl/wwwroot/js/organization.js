const portal = document.querySelector("[data-portal]");
const root = document.querySelector("[data-org-overview]");

if (portal && root) {
    const state = {
        organization: null,
        employee: null,
        view: "overview",
        selectedRoleId: "",
        loadGeneration: 0,
        loadAbort: null,
        explicitEmployeeGeneration: 0,
        explicitEmployeeAbort: null,
        explicitEmployeeTarget: "",
        passiveEmployeeGeneration: 0,
        passiveEmployeeAbort: null,
        navigationGeneration: 0,
        nextDraftLineageId: 0,
        drafts: new Map(),
        loaded: false,
        runtimeState: "",
    };
    const text = (value) => value === null || value === undefined || value === "" ? "\u2014" : String(value);
    const label = (value) => String(value || "unknown").replaceAll("-", " ");
    const element = (selector) => root.querySelector(selector);
    const organizationControls = () => root.querySelectorAll("[data-organization-control]");
    const draftDefinitions = {
        organizationName: {
            input: "[data-org-name-input]",
            reset: "[data-reset-org-name]",
            authoritative: (data) => data.displayName,
            revision: (data) => data.revision,
        },
        organizationInstructions: {
            input: "[data-org-instructions]",
            reset: "[data-reset-org-instructions]",
            authoritative: (data) => data.basicInstructions,
            revision: (data) => data.revision,
        },
    };

    function roleDraftKey(roleId) {
        return `role:${roleId}`;
    }

    function roleFor(data, roleId) {
        return data?.roles?.find((role) => role.id === roleId) || null;
    }

    function definitionFor(key) {
        if (draftDefinitions[key]) return draftDefinitions[key];
        if (!key.startsWith("role:")) return null;
        const roleId = key.slice(5);
        return {
            input: "[data-role-instructions]",
            reset: "[data-reset-role-instructions]",
            authoritative: (data) => roleFor(data, roleId)?.standingInstructions ?? "",
            revision: (data) => roleFor(data, roleId)?.revision ?? 0,
        };
    }

    function updateDraftState(key, data = state.organization) {
        const definition = definitionFor(key);
        if (!definition) return;
        const draft = state.drafts.get(key);
        const reset = element(definition.reset);
        const input = element(definition.input);
        const authoritativeRevision = definition.revision(data);
        const visible = !key.startsWith("role:") || key === roleDraftKey(state.selectedRoleId);
        if (draft && authoritativeRevision !== draft.baseRevision) draft.conflict = true;
        if (visible && input) input.dataset.draftState = draft?.conflict ? "conflict" : draft ? "dirty" : "clean";
        if (visible && reset) {
            reset.hidden = !draft;
            reset.textContent = draft?.conflict ? "Reset to changed authoritative value" : "Discard draft";
        }
    }

    function beginOrUpdateDraft(key, value) {
        const definition = definitionFor(key);
        if (!definition || !state.organization) return;
        const existing = state.drafts.get(key);
        state.drafts.set(key, {
            value,
            lineageId: existing?.lineageId ?? ++state.nextDraftLineageId,
            editSequence: (existing?.editSequence ?? 0) + 1,
            baseRevision: existing?.baseRevision ?? definition.revision(state.organization),
            conflict: existing?.conflict === true,
        });
        updateDraftState(key);
    }

    function resetDraft(key) {
        const definition = definitionFor(key);
        if (!definition || !state.organization) return;
        state.drafts.delete(key);
        const input = element(definition.input);
        if (input) input.value = definition.authoritative(state.organization);
        updateDraftState(key);
        element("[data-config-receipt]").textContent = "Draft reset to the current authoritative value.";
        element("[data-config-receipt]").dataset.status = "ok";
    }

    function renderDraft(key, data = state.organization) {
        const definition = definitionFor(key);
        if (!definition) return;
        const draft = state.drafts.get(key);
        const input = element(definition.input);
        if (input) input.value = draft?.value ?? definition.authoritative(data);
        updateDraftState(key, data);
    }

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

    function routeForCurrentView() {
        return state.view === "employee" && state.employee
            ? `employee/${encodeURIComponent(state.employee.id)}`
            : state.view;
    }

    function setHash(route) {
        const hash = `#${route}`;
        if (location.hash !== hash) history.replaceState(null, "", hash);
    }

    function invalidateEmployeeIntent() {
        state.navigationGeneration += 1;
        state.explicitEmployeeGeneration += 1;
        state.explicitEmployeeTarget = "";
        if (state.explicitEmployeeAbort) {
            state.explicitEmployeeAbort.abort();
            state.explicitEmployeeAbort = null;
        }
        state.passiveEmployeeGeneration += 1;
        if (state.passiveEmployeeAbort) {
            state.passiveEmployeeAbort.abort();
            state.passiveEmployeeAbort = null;
        }
    }

    function supportedViews() {
        const departments = state.organization?.departments || [];
        return new Set(["overview", "system", ...departments.map((department) => department.slug)]);
    }

    function navigate(view, options = {}) {
        const { updateHash = true, invalidateSelection = view !== "employee" } = options;
        const target = supportedViews().has(view) || view === "employee" ? view : "overview";
        if (invalidateSelection) invalidateEmployeeIntent();
        state.view = target;
        root.querySelectorAll("[data-view]").forEach((panel) => panel.hidden = panel.dataset.view !== target);
        document.querySelectorAll("[data-nav]").forEach((button) => {
            const selected = button.dataset.nav === target;
            button.setAttribute("aria-current", selected ? "page" : "false");
        });
        if (updateHash) setHash(target === "employee" ? routeForCurrentView() : target);
    }

    function renderEmployee(employee) {
        state.employee = employee;
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
    }

    async function selectEmployee(id, options = {}) {
        const { navigate: shouldNavigate = true } = options;
        const explicit = shouldNavigate;
        if (!explicit && state.explicitEmployeeAbort) return null;
        if (explicit && state.passiveEmployeeAbort) {
            state.passiveEmployeeGeneration += 1;
            state.passiveEmployeeAbort.abort();
            state.passiveEmployeeAbort = null;
        }
        const generationProperty = explicit ? "explicitEmployeeGeneration" : "passiveEmployeeGeneration";
        const abortProperty = explicit ? "explicitEmployeeAbort" : "passiveEmployeeAbort";
        const requestGeneration = ++state[generationProperty];
        const explicitGeneration = state.explicitEmployeeGeneration;
        const navigationGeneration = state.navigationGeneration;
        if (state[abortProperty]) state[abortProperty].abort();
        const controller = new AbortController();
        state[abortProperty] = controller;
        if (explicit) state.explicitEmployeeTarget = id;
        try {
            const response = await fetch(`/api/employees/${encodeURIComponent(id)}`, {
                credentials: "same-origin",
                cache: "no-store",
                signal: controller.signal,
            });
            if (requestGeneration !== state[generationProperty]) return null;
            if (!response.ok) {
                element("[data-org-state]").textContent = `Employee detail unavailable (HTTP ${response.status}).`;
                return null;
            }
            const employee = await response.json();
            const intentStillCurrent = navigationGeneration === state.navigationGeneration;
            const refreshStillVisible = !explicit
                && explicitGeneration === state.explicitEmployeeGeneration
                && !state.explicitEmployeeAbort
                && state.view === "employee"
                && state.employee?.id === id;
            if (requestGeneration !== state[generationProperty]
                || (explicit && (!intentStillCurrent || state.explicitEmployeeTarget !== id))
                || (!explicit && !refreshStillVisible)) return null;
            renderEmployee(employee);
            if (explicit) navigate("employee", { invalidateSelection: false });
            return employee;
        } catch (error) {
            if (!error || error.name !== "AbortError") {
                element("[data-org-state]").textContent = `Employee detail unavailable: ${error?.message || "network error"}.`;
            }
            return null;
        } finally {
            if (state[abortProperty] === controller) state[abortProperty] = null;
            if (explicit && state.explicitEmployeeTarget === id && requestGeneration === state.explicitEmployeeGeneration) {
                state.explicitEmployeeTarget = "";
            }
        }
    }

    function ensureDepartmentNavigation(department) {
        let button = document.querySelector(`[data-nav="${CSS.escape(department.slug)}"]`);
        if (!button) {
            button = document.createElement("button");
            button.type = "button";
            button.className = "nav-item";
            button.dataset.nav = department.slug;
            button.textContent = department.displayName;
            button.setAttribute("aria-current", "false");
            const system = document.querySelector('[data-nav="system"]');
            system?.parentNode?.insertBefore(button, system);
            bindNav(button);
        }
        button.hidden = false;
    }

    function renderDepartments(data) {
        const container = element("[data-department-views]");
        const authoritativeSlugs = new Set(data.departments.map((department) => department.slug));
        document.querySelectorAll('[data-nav]:not([data-nav="overview"]):not([data-nav="system"])')
            .forEach((button) => button.hidden = !authoritativeSlugs.has(button.dataset.nav));
        container.replaceChildren();
        for (const department of data.departments) {
            ensureDepartmentNavigation(department);
            const section = document.createElement("section");
            section.className = "org-view";
            section.dataset.view = department.slug;
            section.hidden = state.view !== department.slug;
            const title = document.createElement("h2");
            title.textContent = department.displayName;
            const description = document.createElement("p");
            description.textContent = `Employees persisted in the ${department.displayName} department.`;
            const list = document.createElement("ul");
            list.className = "employee-list";
            list.dataset.departmentEmployees = department.slug;
            const employees = data.employees.filter((employee) => employee.departmentSlug === department.slug);
            if (!employees.length) {
                const item = document.createElement("li");
                item.className = "empty-state";
                item.textContent = `No employees are assigned to ${department.displayName}.`;
                list.append(item);
            }
            for (const employee of employees) {
                const item = document.createElement("li");
                const button = document.createElement("button");
                button.type = "button";
                button.className = "employee-card";
                button.textContent = `${employee.displayName} \u00b7 ${label(employee.availability)}`;
                button.addEventListener("click", () => selectEmployee(employee.id));
                item.append(button);
                list.append(item);
            }
            section.append(title, description, list);
            container.append(section);
        }
    }

    function renderRoles(data) {
        const select = element("[data-role-select]");
        const previous = state.selectedRoleId || select.value;
        select.replaceChildren();
        for (const role of data.roles) {
            const option = document.createElement("option");
            option.value = role.id;
            option.textContent = role.displayName;
            select.append(option);
        }
        const selected = data.roles.find((role) => role.id === previous) || data.roles[0] || null;
        state.selectedRoleId = selected?.id || "";
        select.value = state.selectedRoleId;
        const form = element("[data-role-instructions-form]");
        form.dataset.roleId = selected?.id || "";
        const dirtyKey = selected ? roleDraftKey(selected.id) : "";
        if (dirtyKey) renderDraft(dirtyKey, data);
        else {
            element("[data-role-instructions]").value = "";
            element("[data-reset-role-instructions]").hidden = true;
        }
        element("[data-role-instructions]").disabled = !selected;
        form.querySelector('button[type="submit"]').disabled = !selected;
    }

    function renderOrganization(data) {
        state.organization = data;
        state.loaded = true;
        portal.dataset.organizationLoaded = String(data.revision);
        element("[data-org-state]").textContent = `Authoritative store revision ${data.revision}.`;
        element("[data-org-description]").textContent = text(data.description);
        element("[data-org-basic-instructions]").textContent = text(data.basicInstructions);
        rows(element("[data-org-departments]"), data.departments.map((department) => [department.displayName, `${department.employeeCount} employee${department.employeeCount === 1 ? "" : "s"}`]));
        rows(element("[data-org-availability]"), data.availability.map((count) => [label(count.category), count.count]));
        element("[data-pending-approvals]").textContent = `${data.pendingApprovals.supported ? "Supported" : "Unsupported"} (${data.pendingApprovals.count}): ${data.pendingApprovals.reason}`;
        const failures = element("[data-org-failures]");
        failures.replaceChildren();
        if (!data.failuresNeedingAttention.length) {
            const item = document.createElement("li");
            item.textContent = "No current orientation or runtime failures.";
            failures.append(item);
        }
        for (const failure of data.failuresNeedingAttention) {
            const item = document.createElement("li");
            const link = document.createElement("a");
            link.href = failure.url;
            link.textContent = `${failure.employeeDisplayName}: ${failure.summary}`;
            link.addEventListener("click", (event) => {
                event.preventDefault();
                selectEmployee(failure.employeeId);
            });
            item.append(link);
            failures.append(item);
        }
        renderDepartments(data);
        renderDraft("organizationName", data);
        renderDraft("organizationInstructions", data);
        organizationControls().forEach((control) => control.disabled = false);
        renderRoles(data);
    }

    async function load(options = {}) {
        const { refreshEmployee = false } = options;
        const requestGeneration = ++state.loadGeneration;
        if (state.loadAbort) state.loadAbort.abort();
        const controller = new AbortController();
        state.loadAbort = controller;
        try {
            const response = await fetch("/api/organization/portal", {
                credentials: "same-origin",
                cache: "no-store",
                signal: controller.signal,
            });
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const data = await response.json();
            if (requestGeneration !== state.loadGeneration) return false;
            renderOrganization(data);
            if (refreshEmployee && state.view === "employee" && state.employee && !state.explicitEmployeeAbort) {
                await selectEmployee(state.employee.id, { navigate: false });
            }
            return true;
        } catch (error) {
            if (!error || error.name !== "AbortError") {
                element("[data-org-state]").textContent = `Organization unavailable: ${error?.message || "network error"}`;
                if (!state.loaded) organizationControls().forEach((control) => control.disabled = true);
            }
            return false;
        } finally {
            if (state.loadAbort === controller) state.loadAbort = null;
        }
    }

    async function mutate(path, method, body, receipt, options) {
        if (!state.organization) {
            receipt.textContent = "Organization is not loaded; no change was sent.";
            receipt.dataset.status = "error";
            return null;
        }
        const submittedDraft = options.dirtyKey ? state.drafts.get(options.dirtyKey) : null;
        if (options.dirtyKey && !submittedDraft) return null;
        const submission = submittedDraft ? {
            lineageId: submittedDraft.lineageId,
            editSequence: submittedDraft.editSequence,
            value: submittedDraft.value,
            baseRevision: submittedDraft.baseRevision,
        } : null;
        const requestBody = submission ? options.body(submission) : body;
        receipt.textContent = "Saving authoritative changes...";
        receipt.dataset.status = "pending";
        const response = await fetch(path, {
            method,
            credentials: "same-origin",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(requestBody),
        });
        let result = null;
        try { result = await response.json(); } catch { result = null; }
        if (!response.ok) {
            if (response.status === 409 && submission) {
                const current = state.drafts.get(options.dirtyKey);
                if (current?.lineageId === submission.lineageId) {
                    current.conflict = true;
                    updateDraftState(options.dirtyKey);
                }
            }
            const conflict = response.status === 409
                ? " Authoritative data changed; your draft and original revision are preserved. Reset it to reconcile, or retry intentionally."
                : "";
            receipt.textContent = `Validation/update failed (HTTP ${response.status}).${conflict}`;
            receipt.dataset.status = "error";
            return null;
        }
        if (submission) {
            const current = state.drafts.get(options.dirtyKey);
            if (current?.lineageId === submission.lineageId
                && current.editSequence === submission.editSequence
                && current.value === submission.value) {
                state.drafts.delete(options.dirtyKey);
                updateDraftState(options.dirtyKey, result || state.organization);
            }
        }
        receipt.textContent = options.format(result);
        receipt.dataset.status = options.status?.(result) || "ok";
        await load({ refreshEmployee: options.refreshEmployee === true });
        return result;
    }

    function bindNav(button) {
        button.addEventListener("click", () => navigate(button.dataset.nav));
    }

    function handleRoute() {
        if (location.hash === "#portal-content") {
            const content = document.getElementById("portal-content");
            content?.focus({ preventScroll: true });
            setHash(routeForCurrentView());
            return;
        }
        const match = location.hash.match(/^#employee\/([^/]+)$/);
        if (match) {
            selectEmployee(decodeURIComponent(match[1]));
            return;
        }
        const requested = location.hash.slice(1) || "overview";
        if (!supportedViews().has(requested)) {
            navigate("overview");
            return;
        }
        navigate(requested, { updateHash: location.hash !== `#${requested}` });
    }

    document.querySelectorAll("[data-nav]").forEach(bindNav);
    document.querySelector('.skip-link[href="#portal-content"]')?.addEventListener("click", (event) => {
        event.preventDefault();
        const content = document.getElementById("portal-content");
        content?.focus({ preventScroll: true });
        content?.scrollIntoView({ block: "start" });
        setHash(routeForCurrentView());
    });
    element("[data-back-to-department]").addEventListener("click", () => navigate(state.employee?.departmentSlug || "overview"));
    element("[data-org-name-input]").addEventListener("input", (event) => {
        beginOrUpdateDraft("organizationName", event.currentTarget.value);
    });
    element("[data-org-instructions]").addEventListener("input", (event) => {
        beginOrUpdateDraft("organizationInstructions", event.currentTarget.value);
    });
    element("[data-role-instructions]").addEventListener("input", (event) => {
        if (state.selectedRoleId) beginOrUpdateDraft(roleDraftKey(state.selectedRoleId), event.currentTarget.value);
    });
    element("[data-reset-org-name]").addEventListener("click", () => resetDraft("organizationName"));
    element("[data-reset-org-instructions]").addEventListener("click", () => resetDraft("organizationInstructions"));
    element("[data-reset-role-instructions]").addEventListener("click", () => {
        if (state.selectedRoleId) resetDraft(roleDraftKey(state.selectedRoleId));
    });
    element("[data-role-select]").addEventListener("change", (event) => {
        state.selectedRoleId = event.currentTarget.value;
        renderRoles(state.organization);
    });
    element("[data-org-name-form]").addEventListener("submit", (event) => {
        event.preventDefault();
        const org = state.organization;
        if (!org) return;
        mutate("/api/organization", "PATCH", null, element("[data-config-receipt]"), {
            dirtyKey: "organizationName",
            body: (draft) => ({ organizationId: org.id, revision: draft.baseRevision, displayName: draft.value }),
            format: () => "Saved organization name. Orientation is stale.",
        });
    });
    element("[data-org-instructions-form]").addEventListener("submit", (event) => {
        event.preventDefault();
        const org = state.organization;
        if (!org) return;
        mutate("/api/organization/basic-instructions", "PUT", null, element("[data-config-receipt]"), {
            dirtyKey: "organizationInstructions",
            body: (draft) => ({ organizationId: org.id, revision: draft.baseRevision, basicInstructions: draft.value }),
            format: () => "Saved organization instructions. Orientation is stale.",
        });
    });
    element("[data-role-instructions-form]").addEventListener("submit", (event) => {
        event.preventDefault();
        const form = event.currentTarget;
        if (!form.dataset.roleId) return;
        mutate(`/api/roles/${encodeURIComponent(form.dataset.roleId)}/instructions`, "PUT", null, element("[data-config-receipt]"), {
            dirtyKey: roleDraftKey(form.dataset.roleId),
            body: (draft) => ({ revision: draft.baseRevision, standingInstructions: draft.value }),
            format: () => "Saved role instructions. Orientation is stale.",
        });
    });
    element("[data-orientation-deliver]").addEventListener("click", () => mutate(
        "/api/orientation/deliver", "POST", {}, element("[data-orientation-receipt]"), {
            refreshEmployee: true,
            format: (result) => result?.restartRequired
                ? "Delivered. Runtime restart required."
                : "Delivered. Runtime restart not required.",
        }));
    element("[data-orientation-comprehension]").addEventListener("click", () => mutate(
        "/api/orientation/comprehension/run", "POST", {}, element("[data-orientation-receipt]"), {
            refreshEmployee: true,
            format: (result) => result?.state === "Comprehended"
                ? "Comprehended."
                : `Comprehension failed: ${result?.state || "Unknown"}.`,
            status: (result) => result?.state === "Comprehended" ? "ok" : "error",
        }));
    element("[data-orientation-hold]").addEventListener("click", (event) => {
        const held = event.currentTarget.dataset.held !== "true";
        mutate("/api/orientation/manual-hold", "PUT", { held, detail: "Owner portal action" }, element("[data-orientation-receipt]"), {
            refreshEmployee: true,
            format: (result) => `Manual hold ${held ? "set" : "cleared"}. State: ${result?.state || "Unknown"}.`,
        });
    });
    window.addEventListener("hashchange", handleRoute);

    async function loadWhenStoreCanExist() {
        const runtimeState = portal.dataset.runtimeState;
        if (runtimeState === state.runtimeState) return;
        state.runtimeState = runtimeState;
        if (runtimeState === "ready" || runtimeState === "degraded") {
            const loaded = await load({ refreshEmployee: state.view === "employee" });
            if (loaded && !state.loadedRoute) {
                state.loadedRoute = true;
                handleRoute();
            }
        } else if (runtimeState === "idle" || runtimeState === "faulted") {
            if (state.loadAbort) state.loadAbort.abort();
            element("[data-org-state]").textContent = "Organization unavailable: the authoritative store is not open.";
            organizationControls().forEach((control) => control.disabled = true);
        }
    }

    organizationControls().forEach((control) => control.disabled = true);
    loadWhenStoreCanExist();
    new MutationObserver(loadWhenStoreCanExist).observe(portal, { attributes: true, attributeFilter: ["data-runtime-state"] });
}
