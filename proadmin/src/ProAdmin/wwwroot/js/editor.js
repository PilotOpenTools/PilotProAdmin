// Редактор автоматизации: CodeMirror (локальный, wwwroot/vendor), загрузка из источников, проверка, diff.
(function () {
    'use strict';

    const data = JSON.parse(document.getElementById('editor-data').textContent);
    const form = document.getElementById('editor-form');
    const triggersArea = document.getElementById('triggers');
    const csharpArea = document.getElementById('csharp');
    const pausedBox = document.getElementById('paused');

    // ---------- Редакторы ----------
    function makeEditor(textarea, mode) {
        if (window.CodeMirror) {
            const cm = CodeMirror.fromTextArea(textarea, {
                mode: mode,
                lineNumbers: true,
                matchBrackets: true,
                indentUnit: 4,
                tabSize: 4,
                indentWithTabs: false,
                lineWrapping: false,
                viewportMargin: Infinity,
                extraKeys: { Tab: cm => cm.execCommand(cm.somethingSelected() ? 'indentMore' : 'insertSoftTab') },
            });
            cm.on('change', markDirty);
            return { cm, get: () => cm.getValue(), set: v => cm.setValue(v || ''), save: () => cm.save() };
        }
        textarea.classList.add('plain');
        textarea.addEventListener('input', markDirty);
        textarea.addEventListener('keydown', e => {
            if (e.key !== 'Tab') return;
            e.preventDefault();
            const s = textarea.selectionStart;
            textarea.setRangeText('    ', s, textarea.selectionEnd, 'end');
        });
        return { cm: null, textarea, get: () => textarea.value, set: v => { textarea.value = v || ''; }, save: () => {} };
    }

    let dirty = false;
    function markDirty() { dirty = true; scheduleLint(); updateVersionStatus(); }

    const triggers = makeEditor(triggersArea, { name: 'javascript', json: true });
    const csharp = makeEditor(csharpArea, 'text/x-csharp');
    pausedBox && pausedBox.addEventListener('change', markDirty);

    window.addEventListener('beforeunload', e => {
        if (dirty) { e.preventDefault(); e.returnValue = ''; }
    });

    function syncAll() { triggers.save(); csharp.save(); }

    function handlerUrl(handler, params) {
        const url = new URL(window.location.href);
        url.searchParams.set('handler', handler);
        Object.entries(params || {}).forEach(([k, v]) => { if (v != null) url.searchParams.set(k, v); });
        return url.toString();
    }

    // ---------- Сообщение коммита ----------
    const description = document.getElementById('description');
    const task = document.getElementById('task');
    const preview = document.getElementById('commit-preview');

    function currentName() {
        return data.name;
    }

    function updatePreview() {
        if (!preview) return;
        const name = currentName() || 'Имя';
        // Строки описания - пункты CHANGELOG.md; в заголовке коммита они через «; » (как AutomationChecks.BuildCommitMessage).
        const d = description
            ? description.value.split(/\r?\n/).map(l => l.trim().replace(/^[-*•]\s+/, '')).filter(Boolean).join('; ')
            : '';
        const t = task ? task.value.trim() : '';
        preview.textContent = t ? `${name}: ${t}, ${d || '…'}` : `${name}: ${d || '…'}`;
    }
    [description, task].forEach(el => el && el.addEventListener('input', updatePreview));
    updatePreview();

    // ---------- Загрузка из источников ----------
    const sourceStatus = document.getElementById('source-status');
    document.querySelectorAll('[data-source]').forEach(btn => {
        btn.addEventListener('click', async () => {
            const name = currentName();
            if (!name) { sourceStatus.textContent = 'Сначала укажите имя автоматизации.'; return; }
            if (dirty && !confirm('Заменить содержимое редактора? Несохранённые правки пропадут.')) return;

            btn.disabled = true;
            sourceStatus.textContent = 'Загрузка…';
            try {
                const res = await fetch(handlerUrl('Source', { from: btn.dataset.source, sourceEnv: btn.dataset.env }));
                const json = await res.json();
                sourceStatus.textContent = json.message || '';
                if (json.ok) {
                    triggers.set(json.triggers);
                    csharp.set(json.csharp);
                    if (typeof json.paused === 'boolean' && pausedBox) pausedBox.checked = json.paused;
                    dirty = true;
                }
            } catch (e) {
                sourceStatus.textContent = 'Ошибка загрузки: ' + e.message;
            } finally {
                btn.disabled = false;
            }
        });
    });

    // ---------- Проверка ----------
    const validateBtn = document.getElementById('btn-validate');
    const validateOut = document.getElementById('validate-output');

    function renderChecks(items) {
        validateOut.innerHTML = '';
        items.forEach(item => {
            const li = document.createElement('li');
            li.className = 'lvl-' + item.level;
            li.textContent = item.text;
            if (item.details) {
                const pre = document.createElement('pre');
                pre.className = 'details';
                pre.textContent = item.details;
                li.appendChild(pre);
            }
            validateOut.appendChild(li);
        });
    }

    validateBtn && validateBtn.addEventListener('click', async () => {
        syncAll();
        const selectedBranch = workSelect?.value;
        validateBtn.disabled = true;
        renderChecks([{ level: 'info', text: 'Проверка…' }]);
        try {
            const res = await fetch(handlerUrl('Validate'), { method: 'POST', body: new FormData(form) });
            const json = await res.json();
            if (selectedBranch === workSelect?.value) renderChecks(json.items || []);
        } catch (e) {
            renderChecks([{ level: 'error', text: 'Ошибка проверки: ' + e.message }]);
        } finally {
            validateBtn.disabled = false;
        }
    });

    // ---------- Отправка ----------
    form.addEventListener('submit', e => {
        syncAll();
        const isDelete = e.submitter && e.submitter.id === 'btn-delete';
        const workBranch = document.getElementById('work-branch');
        const target = workBranch && workBranch.value !== '+new' ? workBranch.value : 'новую ветку';
        const msg = isDelete
            ? `Удалить «${currentName()}» из ветки ${data.branch} и с сервера?`
            : data.workMode
                ? `Сохранить «${currentName()}» в ${target}? Сервер не меняется: ${data.hotfix ? 'на прод - «Hotfix в прод»' : 'на тест - «Влить / тест»'} на странице автоматизации.`
                : `Отправить «${currentName()}» в ветку ${data.branch}? Изменения попадут на сервер после успешного pipeline.`;
        if (!confirm(msg)) { e.preventDefault(); return; }
        dirty = false;
    });

    // Поля имени новой ветки - только для «+ новая ветка».
    const workSelect = document.getElementById('work-branch');
    const newFields = document.getElementById('new-branch-fields');
    if (workSelect && newFields) {
        const toggle = () => { newFields.hidden = workSelect.value !== '+new'; };
        workSelect.addEventListener('change', toggle);
        toggle();
    }

    // ---------- Diff ----------
    const diffCard = document.getElementById('diff-card');
    const diffOut = document.getElementById('diff-output');
    let diffMode = 'git';

    document.getElementById('btn-diff')?.addEventListener('click', () => {
        diffCard.hidden = false;
        renderDiff();
        diffCard.scrollIntoView({ behavior: 'smooth', block: 'start' });
    });
    document.querySelectorAll('[data-diff]').forEach(b => b.addEventListener('click', () => {
        diffMode = b.dataset.diff;
        document.querySelectorAll('[data-diff]').forEach(x => x.classList.toggle('active', x === b));
        renderDiff();
    }));

    function norm(s) { return (s || '').replace(/\r\n?/g, '\n').replace(/\s+$/, ''); }

    function note(text) {
        const p = document.createElement('p');
        p.className = 'muted';
        p.textContent = text;
        return p;
    }

    function renderDiff() {
        const base = diffMode === 'git' ? data.git : data.pilot;
        diffOut.innerHTML = '';
        const label = diffMode === 'git' ? `в ветке ${data.branch}` : 'на сервере';
        if (!base) {
            diffOut.appendChild(note(`Автоматизации ${label} нет - всё содержимое будет добавлено.`));
            return;
        }
        let any = false;
        [['JSON', base.triggers, triggers.get()], ['C#', base.csharp, csharp.get()]].forEach(([title, a, b]) => {
            if (norm(a) === norm(b)) return;
            any = true;
            const h = document.createElement('div');
            h.className = 'diff-title';
            h.textContent = title;
            diffOut.appendChild(h);
            diffOut.appendChild(window.lineDiff(a, b));
        });
        if (diffMode === 'pilot' && pausedBox && base.paused !== pausedBox.checked) {
            any = true;
            diffOut.appendChild(note(`Пауза: ${base.paused ? 'да' : 'нет'} / ${pausedBox.checked ? 'да' : 'нет'}`));
        }
        if (!any) diffOut.appendChild(note(`Отличий от версии ${label} нет.`));
    }


    // ---------- Живая проверка сценария (справочник Pilot + база сервера) ----------
    const lintList = document.getElementById('lint-list');
    const lintSummary = document.getElementById('lint-summary');
    const lintNote = document.getElementById('lint-note');
    let lintTimer = null;
    let lintSeq = 0;
    let lintMarks = [];

    function scheduleLint() {
        if (!lintList) return;
        clearTimeout(lintTimer);
        lintTimer = setTimeout(runLint, 900);
    }

    async function runLint() {
        syncAll();
        const seq = ++lintSeq;
        lintSummary.textContent = 'проверка…';
        try {
            const res = await fetch(handlerUrl('Lint'), { method: 'POST', body: new FormData(form) });
            const json = await res.json();
            if (seq !== lintSeq) return;
            renderLint(json.issues || [], json.note || '');
        } catch (e) {
            lintSummary.textContent = 'проверка недоступна: ' + e.message;
        }
    }

    function renderLint(issues, note) {
        const cm = triggers.cm;
        if (cm) {
            lintMarks.forEach(m => cm.removeLineClass(m.line, 'background', m.cls));
            lintMarks = [];
        }
        lintList.innerHTML = '';
        const errors = issues.filter(i => i.level === 'error').length;
        const warns = issues.filter(i => i.level === 'warn').length;
        lintSummary.innerHTML = '';
        const s = document.createElement('span');
        if (!errors && !warns) { s.className = 'lint-ok'; s.textContent = 'замечаний нет'; }
        else s.textContent = `ошибок: ${errors}, предупреждений: ${warns}`;
        lintSummary.appendChild(s);
        lintNote.textContent = note;

        const icons = { error: 'X', warn: '!', info: '•' };
        issues.forEach(i => {
            const li = document.createElement('li');
            li.className = 'lvl-' + i.level;
            const icon = document.createElement('span'); icon.className = 'lint-icon'; icon.textContent = icons[i.level] || '•';
            const line = document.createElement('span'); line.className = 'lint-line'; line.textContent = 'строка ' + i.line;
            const msg = document.createElement('span'); msg.textContent = i.message;
            li.append(icon, line, msg);
            li.addEventListener('click', () => goToLine(i.line));
            lintList.appendChild(li);

            if (cm && i.level !== 'info' && i.line >= 1 && i.line <= cm.lineCount()) {
                const cls = i.level === 'error' ? 'cm-lint-error' : 'cm-lint-warn';
                const handle = cm.addLineClass(i.line - 1, 'background', cls);
                lintMarks.push({ line: handle, cls });
            }
        });
    }

    function goToLine(line) {
        const cm = triggers.cm;
        if (cm) {
            cm.focus();
            cm.setCursor({ line: line - 1, ch: 0 });
            cm.scrollIntoView({ line: line - 1, ch: 0 }, 120);
            return;
        }
        const ta = triggers.textarea;
        const lines = ta.value.split('\n');
        const pos = lines.slice(0, line - 1).reduce((n, l) => n + l.length + 1, 0);
        ta.focus();
        ta.setSelectionRange(pos, pos + (lines[line - 1] || '').length);
    }


    // ---------- Версия: статус относительно ветки и кнопки +patch / +minor / +major ----------
    const VERSION_RE = /Version\s*:\s*v?(\d+)\.(\d+)\.(\d+)/i;
    const versionStatus = document.getElementById('version-status');
    let versionBase = {
        branch: data.branch, triggers: data.git?.triggers ?? null,
        csharp: data.gitHasCSharp ? data.git?.csharp ?? '' : null, version: data.gitVersion,
    };
    let versionRequest = 0;
    let versionHint = '';

    async function updateVersionBase() {
        const request = ++versionRequest;
        const branch = workSelect.value === '+new' ? data.baseBranch : workSelect.value;
        versionBase = null;
        versionHint = `Загружаем версию из ветки ${branch}…`;
        updateVersionStatus();
        try {
            const response = await fetch(handlerUrl('Source', { from: 'git', workBranch: workSelect.value }));
            const result = await response.json();
            if (request !== versionRequest) return;
            if (result.ok) versionBase = result;
            else versionHint = result.message;
        } catch (e) {
            if (request !== versionRequest) return;
            versionHint = `Не удалось прочитать ветку ${branch}. Повторите проверку.`;
        }
        updateVersionStatus();
    }

    function parseVersion(text) {
        const m = VERSION_RE.exec(text || '');
        return m ? [+m[1], +m[2], +m[3]] : null;
    }
    function cmpVersion(a, b) { for (let i = 0; i < 3; i++) if (a[i] !== b[i]) return a[i] - b[i]; return 0; }
    function fmtVersion(v) { return v ? v.join('.') : null; }
    function normText(s) { return (s || '').replace(/\r\n?/g, '\n').replace(/\s+$/, ''); }

    function updateVersionStatus() {
        if (!versionStatus) return;
        const stored = toStorageLayout(triggers.get());
        const editorV = parseVersion(firstCommentText(stored));
        let level = 'info', hint = versionHint, branchText = '';
        if (versionBase) {
            const branchV = versionBase.version?.split('.').map(Number);
            const jsonChanged = versionBase.triggers === null || normText(versionBase.triggers) !== normText(stored);
            const cs = normText(csharp.get());
            const csChanged = normText(versionBase.csharp) !== cs || (data.workMode && cs === '' && versionBase.csharp !== null);
            branchText = `в ветке ${versionBase.branch}: ${versionBase.triggers === null ? 'автоматизации ещё нет' : fmtVersion(branchV) || 'без версии'}`;
            if (!jsonChanged && !csChanged) hint = 'изменений относительно ветки нет';
            else if (!data.requireVersion) hint = 'проверка версии отключена в настройках';
            else if (!jsonChanged || !editorV || (branchV && cmpVersion(editorV, branchV) <= 0)) {
                level = 'error';
                hint = !editorV ? 'версия не указана - нажмите +patch' : 'версия не повышена - нажмите +patch';
            } else {
                level = 'ok';
                hint = branchV ? 'версия повышена' : 'версия указана';
            }
        }
        document.querySelectorAll('[data-bump]').forEach(button => { button.disabled = !versionBase; });

        versionStatus.className = 'version-status lvl-' + level;
        versionStatus.innerHTML = '';
        const b = document.createElement('span');
        b.className = 'mono';
        b.textContent = 'Версия ' + (fmtVersion(editorV) || '-');
        versionStatus.append(b, document.createTextNode(` · ${[branchText, hint].filter(Boolean).join(' · ')}`));
    }

    // Версия хранится полем "_comment_" первого триггера. Отдельная запись { "_comment_": … } без "when"
    // роняет клиент Pilot (ArgumentNullException в RebuildSnapshot) - её переносим в следующий триггер.
    // Та же логика на сервере: AutomationVersion.ToStorageLayout / WithVersion.
    const STANDALONE_COMMENT_RE = /^(\s*\[\s*)\{\s*"_comment_"\s*:\s*("(?:[^"\\]|\\.)*")\s*\}\s*,\s*\{(\s*)(?=")/;
    const FIRST_TRIGGER_RE = /^(\s*\[\s*)\{(\s*)(?=")/;

    function maskJsonComments(text) {
        return text.replace(/"(?:[^"\\]|\\.)*"|\/\/[^\r\n]*|\/\*[\s\S]*?\*\//g,
            token => token.startsWith('"') ? token : token.replace(/[^\r\n]/g, ' '));
    }

    // Позиция начала значения "_comment_" первого триггера (после кавычки) или -1; вложенные объекты не смотрим.
    function firstTriggerCommentValue(text) {
        text = maskJsonComments(text);
        const first = FIRST_TRIGGER_RE.exec(text);
        if (!first) return -1;
        let depth = 0;
        for (let i = first[1].length; i < text.length; i++) {
            const c = text[i];
            if (c === '"') {
                let end = i + 1;
                while (end < text.length && text[end] !== '"') end += text[end] === '\\' ? 2 : 1;
                if (end >= text.length) return -1;
                if (depth === 1 && text.slice(i, end + 1) === '"_comment_"') {
                    const m = /^\s*:\s*"/.exec(text.slice(end + 1));
                    if (m) return end + 1 + m[0].length;
                }
                i = end;
            } else if (c === '{' || c === '[') depth++;
            else if ((c === '}' || c === ']') && --depth === 0) return -1;
        }
        return -1;
    }

    // Текст (без кавычек) в начало существующего _comment_ первого триггера; null - такого поля нет.
    function prependToComment(text, value) {
        const at = firstTriggerCommentValue(text);
        if (at < 0) return null;
        return text.slice(0, at) + value + (text[at] === '"' ? '' : '. ') + text.slice(at);
    }

    function firstCommentText(text) {
        const at = firstTriggerCommentValue(text);
        if (at < 0) return '';
        const value = /^"(?:[^"\\]|\\.)*"/.exec(text.slice(at - 1));
        try { return value ? JSON.parse(value[0]) : ''; }
        catch (e) { return ''; }
    }

    function toStorageLayout(text) {
        if (text.trimStart().startsWith('{')) text = '[\n' + text.trim() + '\n]\n';
        const m = STANDALONE_COMMENT_RE.exec(text);
        if (!m) return text;
        const [whole, pre, c, ws] = m;
        const merged = pre + '{' + ws + text.slice(whole.length);
        const inner = c.slice(1, -1);
        if (firstTriggerCommentValue(merged) >= 0)
            return inner ? prependToComment(merged, inner) : merged;
        return pre + '{' + ws + '"_comment_": ' + c + ',' + ws + text.slice(whole.length);
    }

    function setVersion(v) {
        const original = triggers.get();
        const text = toStorageLayout(original);
        const value = 'Version: ' + v.join('.');
        const at = firstTriggerCommentValue(text);
        const comment = at >= 0 ? /^(?:[^"\\]|\\.)*/.exec(text.slice(at))[0] : '';
        const m = VERSION_RE.exec(comment);
        if (m) {
            const start = at + m.index;
            const end = start + m[0].length;
            if (triggers.cm && text === original) {
                const cm = triggers.cm;
                cm.replaceRange(value, cm.posFromIndex(start), cm.posFromIndex(end));
            } else {
                triggers.set(text.slice(0, start) + value + text.slice(end));
            }
        } else {
            const first = FIRST_TRIGGER_RE.exec(maskJsonComments(text));
            const entry = `"_comment_": "${value}"`;
            const withComment = prependToComment(text, value);
            if (withComment !== null) {
                triggers.set(withComment);
            } else if (first) {
                const [whole, , ws] = first;
                triggers.set(text.slice(0, whole.length) + entry + ',' + ws + text.slice(whole.length));
            } else {
                // Триггеров ещё нет - заготовка; отправить без триггеров линтер не даст.
                const i = text.indexOf('[');
                const rest = i >= 0 ? text.slice(i + 1) : '';
                triggers.set(i >= 0
                    ? text.slice(0, i + 1) + `\n  { ${entry} }` + (rest.trimStart().startsWith(']') ? '\n' : ',') + rest
                    : `[\n  { ${entry} }\n]\n`);
            }
        }
        markDirty();
    }

    document.querySelectorAll('[data-bump]').forEach(btn => btn.addEventListener('click', () => {
        // Повышение считается от версии в ветке - повторное нажатие не «накручивает» номер.
        const base = versionBase?.version?.split('.').map(Number) || parseVersion(firstCommentText(toStorageLayout(triggers.get())));
        if (!base) { setVersion([1, 0, 0]); return; }
        const next = btn.dataset.bump === 'major' ? [base[0] + 1, 0, 0]
            : btn.dataset.bump === 'minor' ? [base[0], base[1] + 1, 0]
            : [base[0], base[1], base[2] + 1];
        setVersion(next);
    }));

    workSelect?.addEventListener('change', () => {
        if (validateOut) renderChecks([]);
        updateVersionBase();
    });
    if (workSelect && (workSelect.value === '+new' ? data.baseBranch : workSelect.value) !== data.branch)
        updateVersionBase();
    else updateVersionStatus();
    runLint();
})();
