// «Активные работы»: период (по умолчанию 2 недели) запоминается в браузере, таблица подгружается из /ActiveWork.
(function () {
    const KEY = 'activeWork.period';

    function load(key) {
        try { return JSON.parse(localStorage.getItem(key) || 'null'); } catch { return null; }
    }

    function save(key, value) {
        try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* приватный режим - не запоминаем */ }
    }

    function init(box) {
        if (box.dataset.ready) return;
        box.dataset.ready = '1';

        const area = box.dataset.area;
        const form = box.querySelector('.aw-controls');
        const period = form.elements.period;
        const from = form.elements.from;
        const to = form.elements.to;
        const stage = form.elements.stage;
        const custom = box.querySelectorAll('.aw-custom');
        const body = box.querySelector('.aw-body');
        const open = box.querySelector('.aw-open');
        const openKey = 'activeWork.open.' + area;

        const saved = load(KEY);
        if (saved) {
            period.value = saved.period || '14';
            from.value = saved.from || '';
            to.value = saved.to || '';
            stage.value = saved.stage || '';
        }
        const wasOpen = load(openKey);
        if (typeof wasOpen === 'boolean') box.open = wasOpen;

        function query(fresh) {
            const p = new URLSearchParams({ area, tz: String(new Date().getTimezoneOffset()) });
            if (period.value === 'custom' && from.value) {
                p.set('from', from.value);
                if (to.value) p.set('to', to.value);
            } else {
                p.set('days', period.value === 'custom' ? '14' : period.value);
            }
            if (stage.value) p.set('stage', stage.value);
            if (fresh) p.set('fresh', 'true');
            return p;
        }

        let seq = 0;
        async function refresh(fresh) {
            custom.forEach(el => el.hidden = period.value !== 'custom');
            save(KEY, { period: period.value, from: from.value, to: to.value, stage: stage.value });
            const p = query(fresh);
            const link = new URLSearchParams(p);
            link.delete('fresh');
            open.href = '/ActiveWork?' + link;
            if (!box.open) return;
            if (period.value === 'custom' && !from.value) {
                body.innerHTML = '<div class="aw-note">Укажите дату начала периода.</div>';
                return;
            }

            const my = ++seq;
            body.classList.add('aw-loading');
            if (!body.querySelector('table')) body.innerHTML = '<div class="aw-note">Загрузка из GitLab…</div>';
            try {
                p.set('fragment', 'true');
                const r = await fetch('/ActiveWork?' + p, { credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' } });
                if (my !== seq) return;
                if (r.redirected || !r.ok) {
                    body.innerHTML = '<div class="aw-note">Не удалось загрузить (' + (r.redirected ? 'нужно войти заново' : 'HTTP ' + r.status) + ').</div>';
                    return;
                }
                body.innerHTML = await r.text();
            } catch (e) {
                if (my === seq) body.innerHTML = '<div class="aw-note">Не удалось загрузить: ' + e.message + '</div>';
            } finally {
                if (my === seq) body.classList.remove('aw-loading');
            }
        }

        period.addEventListener('change', () => refresh(false));
        from.addEventListener('change', () => refresh(false));
        to.addEventListener('change', () => refresh(false));
        stage.addEventListener('change', () => refresh(false));
        box.querySelector('[data-refresh]').addEventListener('click', () => refresh(true));
        box.addEventListener('toggle', () => {
            save(openKey, box.open);
            if (box.open && !body.classList.contains('aw-loading') && !body.querySelector('table')) refresh(false);
        });
        refresh(false);
    }

    function start() { document.querySelectorAll('details.aw[data-area]').forEach(init); }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
})();
