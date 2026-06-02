// Application state — all derived from server, never persisted locally
const state = {
    user: null,        // { id, displayName, email, sub }
    organizations: [], // [{ orgId, orgName, role }]
    activeContext: null,
};

// API helpers
async function api(method, path, body) {
    const opts = { method, headers: {}, credentials: 'same-origin' };
    if (body) {
        opts.headers['Content-Type'] = 'application/json';
        opts.body = JSON.stringify(body);
    }
    const res = await fetch(path, opts);
    if (!res.ok) {
        const text = await res.text();
        let detail;
        let type;
        try {
            const json = JSON.parse(text);
            type = json.type;
            detail = json.detail || json.title || json.error || friendlyError(json.type, res.status);
        } catch {
            detail = text;
        }

        const error = new Error(detail || friendlyError(type, res.status));
        error.status = res.status;
        error.type = type;
        if (res.status === 401 && path !== '/auth/login') {
            await clearSessionCookie();
            showLogin();
        }
        throw error;
    }
    if (res.status === 204) return null;
    const contentType = res.headers.get('content-type') || '';
    if (!contentType.includes('application/json')) return null;
    return res.json();
}

function friendlyError(type, status) {
    if (type === 'urn:todo-app:registration-failed' && status === 409) {
        return 'An account with that email already exists. If you already registered, switch to Sign In.';
    }

    const messages = {
        'urn:todo-app:invalid-credentials': 'Invalid email or password',
        'urn:todo-app:email-in-use': 'An account with that email already exists',
        'urn:todo-app:registration-failed': 'Registration failed. Please check your details and try again.',
        'urn:todo-app:not-authenticated': 'You must be signed in',
        'urn:todo-app:forbidden': 'You do not have permission to do this',
        'urn:todo-app:not-found': 'Not found',
        'urn:todo-app:conflict': 'A conflict occurred — the item may already exist',
        'urn:todo-app:api-error': 'Something went wrong, please try again',
    };
    return messages[type] || (status ? `Request failed (${status})` : 'An error occurred');
}

function toast(msg, type = 'info') {
    const el = document.getElementById('toast');
    el.textContent = msg;
    el.className = `toast ${type}`;
    setTimeout(() => el.classList.add('hidden'), 3000);
}

// Session management
let sessionCheckVersion = 0;

async function checkSession() {
    const checkVersion = ++sessionCheckVersion;
    try {
        const res = await fetch('/auth/me', { credentials: 'same-origin', cache: 'no-store' });
        if (!res.ok) {
            if (res.status === 401) {
                await clearSessionCookie();
            }
            if (checkVersion === sessionCheckVersion) {
                showLogin();
            }
            return;
        }

        const authUser = await res.json();
        if (authUser && authUser.sub) {
            await loadProfile();
            if (checkVersion === sessionCheckVersion) {
                showApp();
            }
        } else {
            if (checkVersion === sessionCheckVersion) {
                showLogin();
            }
        }
    } catch (e) {
        if (e.status === 401) {
            await clearSessionCookie();
        }
        if (checkVersion === sessionCheckVersion) {
            showLogin();
        }
    }
}

async function clearSessionCookie() {
    try {
        await fetch('/auth/logout', {
            method: 'POST',
            credentials: 'same-origin',
            cache: 'no-store',
        });
    } catch {
        // If the broker is unavailable, resetting local UI state is still correct.
    }
}

async function loadProfile() {
    try {
        const profile = await api('GET', '/api/me');
        state.user = {
            id: profile.id,
            displayName: profile.displayName,
            email: profile.email || '',
        };

        // Load organizations
        try {
            const orgs = await api('GET', '/api/me/organizations');
            state.organizations = orgs || [];
        } catch {
            state.organizations = [];
        }
    } catch (e) {
        console.error('Failed to load profile:', e);
        toast('Failed to load profile', 'error');
        throw e;
    }
}

function showLogin() {
    state.user = null;
    state.organizations = [];
    state.activeContext = null;
    document.getElementById('login-screen').classList.remove('hidden');
    document.getElementById('app-screen').classList.add('hidden');
    document.getElementById('user-display-name').textContent = '';
    document.getElementById('user-todo-entry').innerHTML = '';
    document.getElementById('org-list').innerHTML = '';
    document.getElementById('todo-list').innerHTML = '';
    document.getElementById('members-list').innerHTML = '';
    document.getElementById('members-section').classList.add('hidden');
    document.getElementById('todo-panel').classList.add('hidden');
    document.getElementById('welcome').classList.remove('hidden');
    // Clear forms and error messages
    document.getElementById('form-login')?.reset();
    document.getElementById('form-register')?.reset();
    document.getElementById('login-error')?.classList.add('hidden');
    document.getElementById('register-error')?.classList.add('hidden');
}

function showApp() {
    document.getElementById('login-screen').classList.add('hidden');
    document.getElementById('app-screen').classList.remove('hidden');
    document.getElementById('user-display-name').textContent = state.user?.displayName || 'User';
    renderSidebar();

    // Auto-select personal todo list
    if (state.user) {
        selectContext('user');
    }
}

// Auth actions
async function doRegister(email, password, displayName) {
    const errorEl = document.getElementById('register-error');
    errorEl.classList.add('hidden');
    try {
        await api('POST', '/auth/register', { email, password, displayName });
        const createdUser = await api('POST', '/api/users', { displayName, email });
        if (createdUser.provisioningTicket) {
            toast('Setting up your workspace...', 'info');
            await waitForProvisioning(createdUser.provisioningTicket);
            toast('Workspace ready!', 'success');
        }

        await loadProfile();
        showApp();
        toast(`Welcome, ${state.user?.displayName || 'User'}!`, 'success');
    } catch (e) {
        errorEl.textContent = e.message || 'Registration failed';
        errorEl.classList.remove('hidden');
    }
}

async function doLogin(email, password) {
    const errorEl = document.getElementById('login-error');
    errorEl.classList.add('hidden');
    try {
        await api('POST', '/auth/login', { email, password });
        await loadProfile();
        showApp();
        toast(`Welcome back, ${state.user?.displayName || 'User'}!`, 'success');
    } catch (e) {
        errorEl.textContent = e.message || 'Invalid email or password';
        errorEl.classList.remove('hidden');
    }
}

async function doLogout() {
    await clearSessionCookie();
    showLogin();
}

async function withSubmitLock(form, busyText, action) {
    if (form.dataset.submitting === 'true') return;

    const submitButton = form.querySelector('button[type="submit"]');
    const originalText = submitButton?.textContent;
    form.dataset.submitting = 'true';
    if (submitButton) {
        submitButton.disabled = true;
        submitButton.textContent = busyText;
    }

    try {
        await action();
    } finally {
        delete form.dataset.submitting;
        if (submitButton) {
            submitButton.disabled = false;
            submitButton.textContent = originalText;
        }
    }
}

// Sidebar rendering
function renderSidebar() {
    const userSection = document.getElementById('user-todo-entry');
    if (state.user) {
        userSection.innerHTML = `
            <div class="entity-item ${isActive('user') ? 'active' : ''}" data-type="user">
                👤 ${esc(state.user.displayName)} <span class="entity-hint">(personal)</span>
            </div>`;
        userSection.querySelector('.entity-item').addEventListener('click', () => selectContext('user'));
    } else {
        userSection.innerHTML = '';
    }

    const orgList = document.getElementById('org-list');
    if (state.organizations.length === 0) {
        orgList.innerHTML = '<p style="font-size:0.8rem;color:#9ca3af;padding:0.25rem 0.5rem">No organizations</p>';
        return;
    }

    orgList.innerHTML = state.organizations.map(org => `
        <div class="entity-item ${isActive('org', org.orgId) ? 'active' : ''}"
             data-type="org" data-org-id="${org.orgId}">
            🏢 ${esc(org.orgName)} <span class="entity-hint">${esc(org.role || 'member')}</span>
        </div>
    `).join('');

    orgList.querySelectorAll('.entity-item').forEach(el => {
        el.addEventListener('click', () => selectContext('org', el.dataset.orgId));
    });
}

function isActive(type, orgId) {
    if (!state.activeContext) return false;
    if (type === 'user') return state.activeContext.type === 'user';
    return state.activeContext.type === 'org' && state.activeContext.orgId === orgId;
}

// Context selection
async function selectContext(type, orgId) {
    if (type === 'user') {
        state.activeContext = { type: 'user', label: `${state.user.displayName}'s Todos` };
    } else {
        const org = state.organizations.find(o => o.orgId === orgId);
        const orgName = org ? org.orgName : orgId;
        state.activeContext = { type: 'org', orgId, label: `${state.user.displayName} @ ${orgName}` };
    }

    renderSidebar();
    document.getElementById('welcome').classList.add('hidden');
    document.getElementById('todo-panel').classList.remove('hidden');
    document.getElementById('todo-panel-title').textContent = state.activeContext.label;

    // Show/hide members button for org contexts (admin only)
    const membersBtn = document.getElementById('btn-show-members');
    const membersSection = document.getElementById('members-section');
    if (type === 'org') {
        const currentOrg = state.organizations.find(o => o.orgId === orgId);
        if (currentOrg && currentOrg.role === 'admin') {
            membersBtn.classList.remove('hidden');
            await loadMembers(orgId);
        } else {
            membersBtn.classList.add('hidden');
            membersSection.classList.add('hidden');
        }
    } else {
        membersBtn.classList.add('hidden');
        membersSection.classList.add('hidden');
    }

    await loadTodos();
}

// Todo CRUD
function todosPath() {
    const ctx = state.activeContext;
    if (ctx.type === 'user') return `/api/users/${state.user.id}/todos`;
    return `/api/organizations/${ctx.orgId}/users/${state.user.id}/todos`;
}

async function loadTodos() {
    try {
        const data = await api('GET', todosPath());
        renderTodos(data.items || data || []);
    } catch (e) {
        toast(`Failed to load todos: ${e.message}`, 'error');
        renderTodos([]);
    }
}

function renderTodos(todos) {
    const list = document.getElementById('todo-list');
    if (todos.length === 0) {
        list.innerHTML = '<div class="empty-state"><p>No todos yet. Click "+ Add Todo" to create one.</p></div>';
        return;
    }

    list.innerHTML = todos.map(t => `
        <div class="todo-item ${t.status === 'done' ? 'done' : ''}" data-id="${t.id}">
            <input type="checkbox" class="todo-checkbox"
                   ${t.status === 'done' ? 'checked' : ''}
                   onchange="toggleTodo('${t.id}', this.checked)">
            <div class="todo-body">
                <div class="todo-title">${esc(t.title)}</div>
                ${t.description ? `<div class="todo-description">${esc(t.description)}</div>` : ''}
                <div class="todo-meta">
                    <span class="status-badge status-${t.status || 'pending'}">${t.status || 'pending'}</span>
                    <span class="priority-badge priority-${t.priority || 'medium'}">${t.priority || 'medium'}</span>
                    ${(t.tags || []).map(tag => `<span class="todo-tag">${esc(tag)}</span>`).join('')}
                </div>
            </div>
            <div class="todo-actions">
                <button class="btn-icon" title="Edit" onclick="editTodo('${t.id}')">✏️</button>
                <button class="btn-icon" title="Delete" onclick="deleteTodo('${t.id}')">🗑️</button>
            </div>
        </div>
    `).join('');
}

async function toggleTodo(todoId, done) {
    try {
        await api('PUT', `${todosPath()}/${todoId}`, { status: done ? 'done' : 'pending' });
        await loadTodos();
    } catch (e) {
        toast(`Failed to update: ${e.message}`, 'error');
        await loadTodos();
    }
}

async function deleteTodo(todoId) {
    if (!confirm('Delete this todo?')) return;
    try {
        await api('DELETE', `${todosPath()}/${todoId}`);
        toast('Todo deleted', 'success');
        await loadTodos();
    } catch (e) {
        toast(`Failed to delete: ${e.message}`, 'error');
    }
}

function editTodo(todoId) {
    const items = document.querySelectorAll('.todo-item');
    for (const el of items) {
        if (el.dataset.id === todoId) {
            const title = el.querySelector('.todo-title').textContent;
            const desc = el.querySelector('.todo-description')?.textContent || '';
            const priority = el.querySelector('.priority-badge')?.textContent || 'medium';
            const status = el.querySelector('.status-badge')?.textContent || 'pending';
            const tags = [...el.querySelectorAll('.todo-tag')].map(t => t.textContent);

            document.getElementById('edit-todo-id').value = todoId;
            document.getElementById('edit-todo-title').value = title;
            document.getElementById('edit-todo-desc').value = desc;
            document.getElementById('edit-todo-priority').value = priority;
            document.getElementById('edit-todo-tags').value = tags.join(', ');
            document.getElementById('edit-todo-status').value = status;

            document.getElementById('dlg-edit-todo').showModal();
            break;
        }
    }
}

// Admin operations (create org, add member)
async function createOrg(name) {
    try {
        const result = await api('POST', '/api/organizations', { name });
        if (!result || !result.id) {
            toast('Organization created but no ID returned', 'error');
            return;
        }
        toast(`Organization "${name}" created — provisioning...`, 'info');

        if (result.provisioningTicket) {
            await waitForProvisioning(result.provisioningTicket);
        }

        // Auto-add creator as admin member
        const memberResult = await api('POST', `/api/organizations/${result.id}/members`, {
            userId: state.user.id, role: 'admin'
        });

        if (memberResult && memberResult.provisioningTicket) {
            toast('Setting up your workspace in this org...', 'info');
            await waitForProvisioning(memberResult.provisioningTicket);
        }

        toast(`Organization "${name}" ready!`, 'success');

        // Refresh organizations from server
        await refreshOrganizations();
        renderSidebar();
    } catch (e) {
        toast(`Failed to create organization: ${e.message}`, 'error');
    }
}

async function addMemberByEmail(orgId, email, role) {
    try {
        // Resolve email to userId
        const lookupRes = await fetch(`/api/users/lookup?email=${encodeURIComponent(email)}`, { credentials: 'same-origin' });
        if (!lookupRes.ok) {
            toast(`User not found: ${email}`, 'error');
            return;
        }
        const user = await lookupRes.json();

        // Add member with resolved userId
        const result = await api('POST', `/api/organizations/${orgId}/members`, { userId: user.id, role });
        toast(`${user.displayName || email} added as ${role}`, 'success');

        if (result && result.provisioningTicket) {
            await waitForProvisioning(result.provisioningTicket);
            toast('Member storage ready!', 'success');
        }

        await refreshOrganizations();
        renderSidebar();

        // Refresh members list if we're viewing this org
        if (state.activeContext?.type === 'org' && state.activeContext.orgId === orgId) {
            await loadMembers(orgId);
        }
    } catch (e) {
        toast(`Failed to add member: ${e.message}`, 'error');
    }
}

async function removeMember(orgId, userId, displayName) {
    if (!confirm(`Remove ${displayName} from this organization?`)) return;
    try {
        const res = await fetch(`/api/organizations/${orgId}/members/${userId}`, {
            method: 'DELETE',
            credentials: 'same-origin'
        });
        if (!res.ok) {
            const err = await res.json().catch(() => ({}));
            toast(err.title || friendlyError(err.type, res.status), 'error');
            return;
        }
        toast(`${displayName} removed`, 'success');
        await refreshOrganizations();
        renderSidebar();
        await loadMembers(orgId);
    } catch (e) {
        toast(`Failed to remove member: ${e.message}`, 'error');
    }
}

async function loadMembers(orgId) {
    const section = document.getElementById('members-section');
    const list = document.getElementById('members-list');
    try {
        const res = await fetch(`/api/organizations/${orgId}/members`, { credentials: 'same-origin' });
        if (!res.ok) { section.classList.add('hidden'); return; }
        const members = await res.json();

        // Determine if current user is admin of this org
        const currentOrg = state.organizations.find(o => o.orgId === orgId);
        const isAdmin = currentOrg && currentOrg.role === 'admin';

        list.innerHTML = members.map(m => `
            <div class="member-item">
                <div class="member-info">
                    <span>${esc(m.displayName)}</span>
                    <span class="member-email">${esc(m.email)}</span>
                    <span class="member-role ${m.role}">${m.role}</span>
                </div>
                ${isAdmin && m.userId !== state.user.id ? `
                    <button class="btn-remove-member" data-user-id="${m.userId}" data-name="${esc(m.displayName)}" title="Remove member">✕</button>
                ` : ''}
            </div>
        `).join('');

        // Wire up remove buttons
        list.querySelectorAll('.btn-remove-member').forEach(btn => {
            btn.addEventListener('click', () => removeMember(orgId, btn.dataset.userId, btn.dataset.name));
        });

        section.classList.remove('hidden');
    } catch {
        section.classList.add('hidden');
    }
}

async function refreshOrganizations() {
    try {
        const orgs = await api('GET', '/api/me/organizations');
        state.organizations = orgs || [];
    } catch {
        // Keep current state
    }
}

async function waitForProvisioning(ticket, maxWaitMs = 15000) {
    const deadline = Date.now() + maxWaitMs;
    while (Date.now() < deadline) {
        try {
            const res = await fetch(`/api/provisioning/status/${ticket}`, { credentials: 'same-origin' });
            if (!res.ok) return;
            const data = await res.json();
            if (data.status === 'ready') return;
            if (data.status === 'failed') {
                toast(`Provisioning failed: ${data.message || 'unknown'}`, 'error');
                return;
            }
            const delay = (data.retryAfterSeconds || 1) * 1000;
            await new Promise(r => setTimeout(r, delay));
        } catch {
            await new Promise(r => setTimeout(r, 1000));
        }
    }
}

// Dialog handlers
document.getElementById('btn-show-members').addEventListener('click', () => {
    const section = document.getElementById('members-section');
    section.classList.toggle('hidden');
});

document.getElementById('btn-add-todo').addEventListener('click', () => {
    document.getElementById('input-todo-title').value = '';
    document.getElementById('input-todo-desc').value = '';
    document.getElementById('input-todo-priority').value = 'medium';
    document.getElementById('input-todo-tags').value = '';
    document.getElementById('dlg-add-todo').showModal();
});

document.getElementById('dlg-add-todo').addEventListener('close', async function () {
    if (this.returnValue === '') return;
    const title = document.getElementById('input-todo-title').value.trim();
    if (!title) return;

    const todo = {
        title,
        description: document.getElementById('input-todo-desc').value.trim() || undefined,
        priority: document.getElementById('input-todo-priority').value,
        tags: document.getElementById('input-todo-tags').value
            .split(',').map(s => s.trim()).filter(Boolean),
    };
    if (todo.tags.length === 0) delete todo.tags;

    try {
        await api('POST', todosPath(), todo);
        toast('Todo created', 'success');
        this.querySelector('form').reset();
        await loadTodos();
    } catch (e) {
        toast(`Failed to create todo: ${e.message}`, 'error');
    }
});

document.getElementById('dlg-edit-todo').addEventListener('close', async function () {
    if (this.returnValue === '') return;
    const todoId = document.getElementById('edit-todo-id').value;
    const update = {
        title: document.getElementById('edit-todo-title').value.trim(),
        description: document.getElementById('edit-todo-desc').value.trim() || undefined,
        status: document.getElementById('edit-todo-status').value,
        priority: document.getElementById('edit-todo-priority').value,
        tags: document.getElementById('edit-todo-tags').value
            .split(',').map(s => s.trim()).filter(Boolean),
    };
    if (update.tags.length === 0) delete update.tags;

    try {
        await api('PUT', `${todosPath()}/${todoId}`, update);
        toast('Todo updated', 'success');
        await loadTodos();
    } catch (e) {
        toast(`Failed to update: ${e.message}`, 'error');
    }
});

document.getElementById('btn-create-org').addEventListener('click', () => {
    document.getElementById('input-org-name').value = '';
    document.getElementById('dlg-create-org').showModal();
});

document.getElementById('dlg-create-org').addEventListener('close', function () {
    if (this.returnValue === '') return;
    const name = document.getElementById('input-org-name').value.trim();
    if (name) {
        this.querySelector('form').reset();
        createOrg(name);
    }
});

document.getElementById('btn-add-member').addEventListener('click', () => {
    // Only show orgs where user is admin
    const adminOrgs = state.organizations.filter(o => o.role === 'admin');
    if (adminOrgs.length === 0) {
        toast('You must be an admin of at least one organization to add members', 'error');
        return;
    }
    const orgSelect = document.getElementById('input-member-org');
    orgSelect.innerHTML = adminOrgs.map(o =>
        `<option value="${o.orgId}">${esc(o.orgName)}</option>`
    ).join('');
    document.getElementById('input-member-email').value = '';
    document.getElementById('input-member-role').value = 'member';
    document.getElementById('dlg-add-member').showModal();
});

document.getElementById('dlg-add-member').addEventListener('close', function () {
    if (this.returnValue === '') return;
    const orgId = document.getElementById('input-member-org').value;
    const email = document.getElementById('input-member-email').value.trim();
    const role = document.getElementById('input-member-role').value;
    if (orgId && email) {
        this.querySelector('form').reset();
        addMemberByEmail(orgId, email, role);
    }
});

// Login form
document.getElementById('form-login')?.addEventListener('submit', async function (e) {
    e.preventDefault();
    const form = this;
    await withSubmitLock(form, 'Signing in...', async () => {
        const email = document.getElementById('login-email').value.trim();
        const password = document.getElementById('login-password').value;
        if (email && password) {
            await doLogin(email, password);
            if (state.user) form.reset();
        }
    });
});

// Register form
document.getElementById('form-register')?.addEventListener('submit', async function (e) {
    e.preventDefault();
    const form = this;
    await withSubmitLock(form, 'Creating account...', async () => {
        const displayName = document.getElementById('reg-name').value.trim();
        const email = document.getElementById('reg-email').value.trim();
        const password = document.getElementById('reg-password').value;
        if (displayName && email && password) {
            await doRegister(email, password, displayName);
            if (state.user) form.reset();
        }
    });
});

// Logout button
document.getElementById('btn-logout')?.addEventListener('click', doLogout);

// Utils
function esc(str) {
    const el = document.createElement('span');
    el.textContent = str || '';
    return el.innerHTML;
}

function shouldRevalidateVisibleApp() {
    return state.user !== null ||
        !document.getElementById('app-screen').classList.contains('hidden');
}

window.addEventListener('pageshow', e => {
    if (e.persisted || shouldRevalidateVisibleApp()) {
        checkSession();
    }
});

window.addEventListener('focus', () => {
    if (shouldRevalidateVisibleApp()) {
        checkSession();
    }
});

// Init
checkSession();
