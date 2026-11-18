// Раздел «Миграция».
(function () {
    // Выбор «что переносить» - только для выборочного режима; поле Id - только для переноса выбранных объектов.
    const form = document.getElementById('mig-form');
    if (form) {
        const selective = form.querySelector('[data-selective]');
        const tree = form.querySelector('[data-tree]');
        const file = form.querySelector('[data-file]');
        const fileArchive = form.querySelector('[data-file-archive]');
        const exportFields = form.querySelector('[data-export]');
        const contourOnly = form.querySelectorAll('[data-contour]');
        const sync = () => {
            // Файловая база переносится и выгружается только целиком: режимы и настройки сервера к ней не относятся.
            const isFile = form.querySelector('select[name="Form.Source"]')?.value === 'file';
            const isExport = form.querySelector('select[name="Form.Target"]')?.value === 'file';
            if (file) file.hidden = !isFile;
            if (exportFields) {
                exportFields.hidden = !isExport;
                exportFields.disabled = !isExport;
            }
            contourOnly.forEach(el => el.hidden = isFile || isExport);
            if (fileArchive) {
                const archiveOn = form.querySelector('input[name="Form.ImportFileArchive"][type="checkbox"]')?.checked;
                fileArchive.classList.toggle('mig-off', !archiveOn);
            }

            const mode = isFile || isExport ? 'Full' : form.querySelector('input[name="Form.Mode"]:checked')?.value;
            if (selective) {
                selective.hidden = mode !== 'Selective';
                selective.disabled = mode !== 'Selective';
            }
            const treeOn = form.querySelector('input[name="Form.Tree"][type="checkbox"]')?.checked;
            if (tree) tree.classList.toggle('mig-off', !treeOn);
            const byIds = form.querySelector('input[name="Form.TreeAll"]:checked')?.value === 'false';
            const ids = form.querySelector('textarea[name="Form.TreeIds"]');
            if (ids) ids.hidden = !(treeOn && byIds);
        };
        form.addEventListener('change', sync);
        sync();
    }

    // Индикатор ожидания: проверка и запуск идут до минуты - кнопка крутится, повторно не нажать.
    document.querySelectorAll('form').forEach(f => {
        f.addEventListener('submit', e => {
            const button = e.submitter;
            if (!button || !button.dataset.busy) return;
            // Отключаем после того, как браузер собрал данные формы, иначе потеряется обработчик кнопки.
            setTimeout(() => {
                f.querySelectorAll('button[type="submit"]').forEach(b => b.disabled = true);
                button.innerHTML = '<span class="spinner"></span>' + button.dataset.busy;
                button.classList.add('mig-busy');
                const note = document.createElement('span');
                note.className = 'muted small mig-busy-note';
                note.textContent = 'Подключаюсь к серверам Pilot, PostgreSQL и архивам - это может занять до минуты.';
                button.parentElement?.appendChild(note);
            }, 0);
        });
    });
})();
