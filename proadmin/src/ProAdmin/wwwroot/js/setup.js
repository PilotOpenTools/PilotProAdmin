(() => {
    const form = document.querySelector('[data-setup-wizard="steps"]');
    if (!form) return;

    const steps = Array.from(form.querySelectorAll('[data-setup-step]'));
    const back = form.querySelector('[data-setup-back]');
    const next = form.querySelector('[data-setup-next]');
    const submit = form.querySelector('button[type="submit"]');
    const progress = document.querySelector('[data-setup-progress]');
    const marks = progress ? Array.from(progress.children) : [];
    if (progress) progress.hidden = false;
    let step = 0;

    function showStep(index, moveFocus = true) {
        step = index;
        steps.forEach((section, i) => { section.hidden = i !== step; });
        marks.forEach((mark, i) => {
            mark.classList.toggle('is-done', i < step);
            if (i === step) mark.setAttribute('aria-current', 'step');
            else mark.removeAttribute('aria-current');
        });
        back.hidden = step === 0;
        next.hidden = step === steps.length - 1;
        submit.hidden = !next.hidden;
        if (moveFocus) {
            const heading = steps[step].querySelector('h2');
            heading.tabIndex = -1;
            heading.focus();
        }
    }

    function advance() {
        for (const field of steps[step].querySelectorAll('input')) {
            if (!field.reportValidity()) return;
        }
        showStep(step + 1);
    }

    back.addEventListener('click', () => showStep(step - 1));
    next.addEventListener('click', advance);
    form.addEventListener('submit', event => {
        if (step === steps.length - 1) return;
        event.preventDefault();
        advance();
    });
    showStep(0, false);
})();
