// Построчный diff (LCS) с контекстом в 3 строки. Общий для редактора и переноса на прод.
window.lineDiff = (function () {
    'use strict';
    function note(text) {
        const p = document.createElement('p');
        p.className = 'muted';
        p.textContent = text;
        return p;
    }

    // LCS по строкам + вывод с контекстом в 3 строки.
    function buildDiff(a, b) {
        const pre = document.createElement('div');
        pre.className = 'diff-body';
        if (a.length * b.length > 9e6) {
            pre.appendChild(note('Файл слишком большой для построчного сравнения.'));
            return pre;
        }
        const n = a.length, m = b.length;
        const dp = new Array(n + 1);
        for (let i = 0; i <= n; i++) dp[i] = new Uint32Array(m + 1);
        for (let i = n - 1; i >= 0; i--)
            for (let j = m - 1; j >= 0; j--)
                dp[i][j] = a[i] === b[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);

        const ops = [];
        let i = 0, j = 0;
        while (i < n && j < m) {
            if (a[i] === b[j]) { ops.push([' ', a[i], i + 1, j + 1]); i++; j++; }
            else if (dp[i + 1][j] >= dp[i][j + 1]) { ops.push(['-', a[i], i + 1, null]); i++; }
            else { ops.push(['+', b[j], null, j + 1]); j++; }
        }
        while (i < n) { ops.push(['-', a[i], i + 1, null]); i++; }
        while (j < m) { ops.push(['+', b[j], null, j + 1]); j++; }

        const keep = new Array(ops.length).fill(false);
        ops.forEach((op, k) => {
            if (op[0] === ' ') return;
            for (let x = Math.max(0, k - 3); x <= Math.min(ops.length - 1, k + 3); x++) keep[x] = true;
        });

        let skipped = false;
        ops.forEach((op, k) => {
            if (!keep[k]) {
                if (!skipped) { const s = document.createElement('div'); s.className = 'dl dl-skip'; s.textContent = '⋯'; pre.appendChild(s); }
                skipped = true;
                return;
            }
            skipped = false;
            const line = document.createElement('div');
            line.className = 'dl ' + (op[0] === '+' ? 'dl-add' : op[0] === '-' ? 'dl-del' : '');
            const num = document.createElement('span');
            num.className = 'dl-num';
            num.textContent = op[0] === '+' ? op[3] : op[2];
            const txt = document.createElement('span');
            txt.textContent = op[0] + ' ' + op[1];
            line.append(num, txt);
            pre.appendChild(line);
        });
        return pre;
    }
    return function (a, b) {
        const norm = s => (s || '').replace(/\r\n?/g, '\n').replace(/\s+$/, '');
        return buildDiff(norm(a).split('\n'), norm(b).split('\n'));
    };
})();
