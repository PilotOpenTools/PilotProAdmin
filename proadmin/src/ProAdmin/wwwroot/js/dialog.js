// Подтверждения в стиле админки вместо системного окна confirm().
// Нативный confirm() синхронный, поэтому подмена отвечает «нет», показывает своё окно,
// а после «Да» повторяет исходное действие (submit формы или клик), где confirm() уже вернёт true.
(() => {
    const nativeConfirm = window.confirm.bind(window);
    const dangerWords = /удалить|откатить|отключить|прод|без code review|пропад/i;
    let bypass = false;
    let dialog;

    function build() {
        dialog = document.createElement('dialog');
        dialog.className = 'modal';
        dialog.innerHTML = `
            <form method="dialog" class="modal-body">
                <div class="modal-icon" aria-hidden="true"></div>
                <div class="modal-content">
                    <h2 class="modal-title"></h2>
                    <p class="modal-text"></p>
                </div>
                <div class="modal-actions">
                    <button type="submit" value="cancel" class="btn">Отмена</button>
                    <button type="submit" value="ok" class="btn modal-ok"></button>
                </div>
            </form>`;
        // Клик по затемнению вокруг окна - отмена.
        dialog.addEventListener('click', e => { if (e.target === dialog) dialog.close('cancel'); });
        document.body.appendChild(dialog);
    }

    function ask(message) {
        if (!dialog) build();
        const danger = dangerWords.test(message);
        dialog.classList.toggle('modal-danger', danger);
        dialog.querySelector('.modal-title').textContent = danger ? 'Подтвердите действие' : 'Подтверждение';
        dialog.querySelector('.modal-text').textContent = message;
        const ok = dialog.querySelector('.modal-ok');
        ok.textContent = danger ? 'Да, выполнить' : 'Продолжить';
        ok.classList.toggle('btn-danger', danger);
        ok.classList.toggle('btn-primary', !danger);
        dialog.returnValue = 'cancel';
        return new Promise(resolve => {
            dialog.addEventListener('close', () => resolve(dialog.returnValue === 'ok'), { once: true });
            dialog.showModal();
            ok.focus();
        });
    }

    window.ask = ask;

    function replayOf(ev) {
        if (!ev) return null;
        if (ev.type === 'submit' && ev.target instanceof HTMLFormElement) {
            const form = ev.target, submitter = ev.submitter;
            return () => submitter && submitter.form === form ? form.requestSubmit(submitter) : form.requestSubmit();
        }
        if (ev.type === 'click' && ev.currentTarget instanceof HTMLElement) {
            const el = ev.currentTarget;
            return () => el.click();
        }
        return null;
    }

    window.confirm = message => {
        if (bypass) return true;
        const replay = replayOf(window.event);
        if (!replay || typeof HTMLDialogElement === 'undefined') return nativeConfirm(message);
        ask(String(message ?? '')).then(yes => {
            if (!yes) return;
            bypass = true;
            try { replay(); } finally { bypass = false; }
        });
        return false;
    };
})();

// Декларативные действия вместо inline-обработчиков (CSP не разрешает inline-скрипты, а имена веток и модулей
// из GitLab нельзя подставлять в код):
//   data-confirm="текст" | data-confirm-template="Выложить {Tag}?" - подтверждение на форме (submit) или кнопке/ссылке (click);
//   data-confirm-if="имя флажка" - спрашивать, только если флажок отмечен;
//   data-autosubmit - отправить форму при выборе в списке;  data-show-target/data-show-when - показать блок по значению;
//   data-remove-row - скрыть строку таблицы;  data-check-all="имя" data-check-value="true|false" - отметить все флажки;
//   data-no-submit - форма без отправки.
(() => {
    const approved = new WeakSet();
    const textOf = el => {
        const form = el instanceof HTMLFormElement ? el : el.form;
        if (el.dataset.confirmIf && !form?.elements[el.dataset.confirmIf]?.checked) return null;
        const text = el.dataset.confirm ?? el.dataset.confirmTemplate?.replace(/\{(\w+)\}/g, (_, name) => form?.elements[name]?.value ?? '');
        return text || null;
    };
    const guard = (e, el, replay) => {
        if (approved.delete(el)) return;
        const text = textOf(el);
        if (!text) return;
        e.preventDefault();
        window.ask(text).then(yes => { if (yes) { approved.add(el); replay(); } });
    };
    document.addEventListener('submit', e => {
        const form = e.target;
        if (!(form instanceof HTMLFormElement)) return;
        if ('noSubmit' in form.dataset) { e.preventDefault(); return; }
        if ('confirm' in form.dataset || 'confirmTemplate' in form.dataset)
            guard(e, form, () => form.requestSubmit(e.submitter ?? undefined));
    });
    document.addEventListener('click', e => {
        const target = e.target instanceof Element ? e.target : null;
        if (!target) return;
        const confirmable = target.closest('[data-confirm], [data-confirm-template]');
        if (confirmable && !(confirmable instanceof HTMLFormElement))
            guard(e, confirmable, () => confirmable.click());
        const all = target.closest('[data-check-all]');
        if (all) {
            e.preventDefault();
            document.querySelectorAll(`[name="${CSS.escape(all.dataset.checkAll)}"]`).forEach(c => { c.checked = all.dataset.checkValue === 'true'; });
        }
        const remove = target.closest('[data-remove-row]');
        if (remove) {
            const row = remove.closest('tr');
            const select = row?.querySelector('select');
            if (select) select.value = '';
            if (row) row.hidden = true;
        }
    });
    document.addEventListener('change', e => {
        const target = e.target instanceof Element ? e.target : null;
        if (!target) return;
        if ('autosubmit' in target.dataset) target.form?.submit();
        if (target.dataset.showTarget) {
            const box = document.getElementById(target.dataset.showTarget);
            if (box) box.hidden = target.value !== target.dataset.showWhen;
        }
    });
})();

// Поля без подписи (строки таблиц, поля «Причина»/«Подтверждение»): имя для программ чтения с экрана - по placeholder
// или заголовку столбца таблицы, иначе по name.
(() => {
    const hasName = el => el.labels?.length || el.hasAttribute('aria-label') || el.hasAttribute('aria-labelledby') || el.hasAttribute('title');
    const nameOf = el => {
        if (el.placeholder) return el.placeholder;
        const cell = el.closest('td');
        const header = cell?.closest('table')?.tHead?.rows[0]?.cells[cell.cellIndex]?.textContent?.trim();
        return header || el.getAttribute('name') || '';
    };
    const fix = root => root.querySelectorAll('input:not([type=hidden]):not([type=submit]):not([type=button]), select, textarea').forEach(el => {
        if (hasName(el)) return;
        const name = nameOf(el);
        if (name) el.setAttribute('aria-label', name);
    });
    document.addEventListener('DOMContentLoaded', () => {
        fix(document);
        new MutationObserver(list => list.forEach(m => m.addedNodes.forEach(n => n instanceof Element && fix(n))))
            .observe(document.body, { childList: true, subtree: true });
    });
})();

// Окна-формы (<dialog class="modal modal-form">): открываются кнопкой data-dialog="id" или ссылкой на #id
// (кнопки «следующего шага» ведут на #tasks, #release, #hotfix - если там окно, оно и откроется).
(() => {
    const open = id => {
        const el = id && document.getElementById(id);
        // Методы через прототип: у формы поле с именем closest/scrollIntoView перекрывает метод (el?.closest is not a function).
        const d = el instanceof HTMLDialogElement ? el : el instanceof Element ? Element.prototype.closest.call(el, 'dialog') : null;
        if (!(d instanceof HTMLDialogElement) || !d.classList.contains('modal-form')) return false;
        if (!d.open) d.showModal();
        if (el !== d) Element.prototype.scrollIntoView.call(el, { block: 'nearest' });
        return true;
    };
    document.addEventListener('click', e => {
        const target = e.target instanceof Element ? e.target : null;
        if (!target) return;
        if (target instanceof HTMLDialogElement && target.classList.contains('modal-form')) { target.close(); return; } // клик по затемнению
        const closer = target.closest('[data-close]');
        if (closer) { closer.closest('dialog')?.close(); return; }
        const btn = target.closest('[data-dialog]');
        if (btn) { e.preventDefault(); open(btn.dataset.dialog); return; }
        const a = target.closest('a[href^="#"]');
        if (a && open(a.getAttribute('href').slice(1))) e.preventDefault();
    });
    const fromHash = () => open(decodeURIComponent(location.hash.slice(1)));
    window.addEventListener('hashchange', fromHash);
    document.addEventListener('DOMContentLoaded', fromHash);
})();

// Обязательные поля с data-required-message: браузер показывает понятную причину прямо у поля (в том же окне).
(() => {
    document.addEventListener('invalid', e => {
        const el = e.target;
        if (el instanceof HTMLInputElement && el.dataset.requiredMessage && el.validity.valueMissing)
            el.setCustomValidity(el.dataset.requiredMessage);
    }, true);
    document.addEventListener('input', e => {
        if (e.target instanceof HTMLInputElement && e.target.dataset.requiredMessage) e.target.setCustomValidity('');
    });
})();
