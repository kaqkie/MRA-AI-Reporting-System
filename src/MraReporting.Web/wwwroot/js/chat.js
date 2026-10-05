// MRA Reporting Assistant - chat page.
// The conversation is held here while it is open, sent with every question, and saved on the
// server after each answer so it appears under "Your conversations" in the side panel.
// Tables and charts are drawn from the query rows the server sends; the model never touches them.
(function () {
  'use strict';

  const MAX_TABLE_ROWS = 200;

  const messagesEl = document.getElementById('messages');
  const form = document.getElementById('ask-form');
  const input = document.getElementById('question');
  const sendBtn = document.getElementById('send');
  const welcome = document.getElementById('welcome');
  const desk = document.querySelector('.desk');

  const numberFmt = new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 });
  // conversation = { id, title, turns: [{ question, answer, events, failed }] }
  // "events" keeps what was shown under an answer (lookups, tables, report links, warnings),
  // so a saved conversation opens exactly as it looked.
  let conversation = newConversationState();
  let busy = false;
  let controller = null;

  // ------------------------------------------------------------------ helpers

  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = text;
    return node;
  }

  // Follow new text only while the reader is at the bottom; once they scroll up, leave them there.
  let followBottom = true;
  messagesEl.addEventListener('scroll', () => {
    followBottom = messagesEl.scrollHeight - messagesEl.scrollTop - messagesEl.clientHeight < 80;
  });

  function scrollDown(force) {
    if (force) followBottom = true;
    if (followBottom) messagesEl.scrollTop = messagesEl.scrollHeight;
  }

  function formatValue(v) {
    if (v === null || v === undefined) return '';
    if (typeof v === 'number') return numberFmt.format(v);
    if (typeof v === 'boolean') return v ? 'yes' : 'no';
    if (typeof v === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/.test(v)) return v.slice(0, 16).replace('T', ' ');
    return String(v);
  }

  // Shows the answer with **bold** rendered; everything else stays plain text (escaped).
  function renderAnswer(target, text) {
    const escaped = text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    target.innerHTML = escaped.replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>');
  }

  // While an answer is coming, the Ask button becomes Stop.
  function setBusy(value) {
    busy = value;
    sendBtn.classList.toggle('btn-stop', value);
    sendBtn.innerHTML = value
      ? '<svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true"><rect x="6" y="6" width="12" height="12" rx="2" fill="currentColor"/></svg>Stop'
      : 'Ask';
    sendBtn.title = value ? 'Stop this answer' : '';
  }

  function stopAnswer() {
    if (controller) controller.abort();
  }

  // ------------------------------------------------------------------ messages

  function newConversationState() {
    return { id: null, title: '', turns: [] };
  }

  function leaveWelcome() {
    if (welcome) welcome.hidden = true;
    if (desk.classList.contains('empty')) {
      desk.classList.remove('empty');
      input.placeholder = 'Ask a follow-up question';
    }
  }

  function addMessage(role, text) {
    leaveWelcome();
    const wrap = el('div', 'msg ' + role);
    const body = el('div', 'text', text);
    wrap.appendChild(body);
    const extras = el('div', 'extras');
    wrap.appendChild(extras);
    messagesEl.appendChild(wrap);
    scrollDown();
    return { wrap, body, extras };
  }

  const PENCIL = '<svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true"><path d="M4 20h4L19 9l-4-4L4 16v4z" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"/><path d="M13.5 6.5l4 4" fill="none" stroke="currentColor" stroke-width="1.7"/></svg>';

  // A question bubble with an edit button: editing it asks again from that point.
  function addQuestion(turn) {
    const msg = addMessage('user', turn.question);
    const edit = el('button', 'icon-btn edit-btn');
    edit.type = 'button';
    edit.innerHTML = PENCIL;
    edit.title = 'Edit this question';
    edit.setAttribute('aria-label', 'Edit this question');
    edit.addEventListener('click', () => startEdit(turn, msg));
    msg.wrap.insertBefore(edit, msg.body);
    return msg;
  }

  function startEdit(turn, msg) {
    if (busy) return;
    const index = conversation.turns.indexOf(turn);
    if (index < 0) return;
    msg.wrap.classList.add('editing');
    const original = Array.from(msg.wrap.childNodes);
    original.forEach(n => { n.hidden = true; });

    const box = el('div', 'edit-box');
    const area = el('textarea');
    area.value = turn.question;
    area.maxLength = 1000;
    area.setAttribute('aria-label', 'Edit your question');
    const actions = el('div', 'edit-actions');
    const later = conversation.turns.length - index - 1;
    actions.appendChild(el('span', 'edit-note',
      later > 0 ? 'The answers after this question will be replaced.' : 'The answer will be replaced.'));
    const cancel = el('button', 'btn btn-small btn-quiet', 'Cancel');
    cancel.type = 'button';
    const save = el('button', 'btn btn-small btn-primary', 'Ask again');
    save.type = 'button';
    actions.appendChild(cancel);
    actions.appendChild(save);
    box.appendChild(area);
    box.appendChild(actions);
    msg.wrap.appendChild(box);
    const fit = () => { area.style.height = 'auto'; area.style.height = Math.min(area.scrollHeight, 220) + 'px'; };
    area.addEventListener('input', fit);
    fit();
    area.focus();
    area.setSelectionRange(area.value.length, area.value.length);

    const close = () => {
      box.remove();
      msg.wrap.classList.remove('editing');
      original.forEach(n => { n.hidden = false; });
    };
    cancel.addEventListener('click', close);
    area.addEventListener('keydown', e => {
      if (e.key === 'Escape') { e.preventDefault(); close(); }
      if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); save.click(); }
    });
    save.addEventListener('click', () => {
      const text = area.value.trim();
      if (!text) { area.focus(); return; }
      // Drop this question and everything after it, then ask the edited question.
      conversation.turns.slice(index).forEach(t => (t.nodes || []).forEach(n => n.remove()));
      conversation.turns.length = index;
      if (index === 0) conversation.title = ''; // the first question names the conversation
      ask(text);
    });
  }

  // "GetVatByTaxRate" with {fromDate, toDate} becomes "Looked up VAT by tax rate, 2026-01-01 to 2026-01-31".
  function describeLookup(tool, rawArgs) {
    const name = String(tool || '').replace(/^(Get|Create)/, '')
      .replace(/([a-z])([A-Z])/g, '$1 $2').replace(/([A-Z])([A-Z][a-z])/g, '$1 $2').toLowerCase()
      .replace(/\bvat\b/g, 'VAT').replace(/\btin\b/g, 'TIN');
    let args = {};
    try { args = JSON.parse(rawArgs || '{}') || {}; } catch (e) { /* ignore */ }
    const parts = [];
    if (args.fromDate && args.toDate) parts.push(args.fromDate === args.toDate ? args.fromDate : args.fromDate + ' to ' + args.toDate);
    else if (args.date) parts.push(args.date);
    Object.entries(args).forEach(([k, v]) => {
      if (['fromDate', 'toDate', 'date'].includes(k) || v === null || v === '' || typeof v === 'object') return;
      parts.push(String(v));
    });
    return name + (parts.length ? ', ' + parts.join(', ') : '');
  }

  function addToolNote(extras, ev) {
    const verb = /^Create/.test(ev.tool || '') ? ['Could not prepare ', 'Prepared '] : ['Could not look up ', 'Looked up '];
    const text = (ev.message ? verb[0] : verb[1]) + describeLookup(ev.tool, ev.arguments)
      + (ev.message ? '. ' + ev.message : '');
    extras.appendChild(el('div', 'tool-note' + (ev.message ? ' failed' : ''), text));
  }

  function showError(msg, text) {
    msg.extras.appendChild(el('div', 'error', text));
  }

  // ------------------------------------------------------------------ report downloads

  function addReportButtons(extras, report) {
    const card = el('div', 'result report-card');
    card.appendChild(el('div', 'doc-mark'));
    const body = el('div', 'doc-body');
    body.appendChild(el('div', 'result-title', report.label));
    body.appendChild(el('div', 'result-meta', 'The file is built when you click. Longer periods can take a minute or two.'));
    const actions = el('div', 'result-actions');
    actions.appendChild(downloadMenu('Download report', 'btn btn-small btn-primary', [
      { label: 'Excel', ext: '.xlsx', href: report.excelUrl },
      { label: 'Word', ext: '.docx', href: report.wordUrl },
      { label: 'PDF', ext: '.pdf', href: report.pdfUrl }
    ]));
    body.appendChild(actions);
    card.appendChild(body);
    extras.appendChild(card);
  }

  // If the browser shows this page from its memory (Back button after signing out), ask the server again.
  window.addEventListener('pageshow', e => { if (e.persisted) window.location.reload(); });

  // The session ended (signed out elsewhere, or 8 hours without use): go to the sign-in page and come back here.
  function signInAgain() {
    window.location.href = '/login?ReturnUrl=%2F';
  }

  // ------------------------------------------------------------------ download menu
  // One "Download" button; clicking it opens a short menu of formats. Items are links (reports)
  // or actions (chat results, which are built from the table on screen).

  function downloadMenu(text, buttonClass, items) {
    const wrap = el('div', 'dl');
    const toggle = el('button', buttonClass + ' dl-toggle', text);
    toggle.type = 'button';
    toggle.setAttribute('aria-haspopup', 'menu');
    toggle.setAttribute('aria-expanded', 'false');
    const menu = el('div', 'dl-menu');
    menu.setAttribute('role', 'menu');
    menu.hidden = true;
    items.forEach(item => {
      if (!item.href && !item.run) return;
      const entry = el(item.href ? 'a' : 'button');
      entry.setAttribute('role', 'menuitem');
      entry.appendChild(document.createTextNode(item.label + ' '));
      entry.appendChild(el('span', null, item.ext));
      if (item.href) entry.href = item.href;
      else {
        entry.type = 'button';
        entry.addEventListener('click', () => { closeMenus(); item.run(); });
      }
      menu.appendChild(entry);
    });
    wrap.appendChild(toggle);
    wrap.appendChild(menu);
    return wrap;
  }

  function closeMenus(except) {
    document.querySelectorAll('.dl').forEach(d => {
      if (d === except) return;
      const m = d.querySelector('.dl-menu');
      if (m && !m.hidden) {
        m.hidden = true;
        d.querySelector('.dl-toggle').setAttribute('aria-expanded', 'false');
      }
    });
  }

  function openMenu(d) {
    const menu = d.querySelector('.dl-menu');
    const toggle = d.querySelector('.dl-toggle');
    closeMenus(d);
    menu.hidden = false;
    toggle.setAttribute('aria-expanded', 'true');
    // Open upwards when there is no room below (e.g. a report card just above the question box).
    d.classList.remove('dl-up');
    const below = window.innerHeight - toggle.getBoundingClientRect().bottom;
    const composer = document.getElementById('ask-form');
    const reserved = composer && !d.closest('.rail') ? composer.getBoundingClientRect().height : 0;
    if (below - reserved < menu.offsetHeight + 8) d.classList.add('dl-up');
    const first = menu.querySelector('[role="menuitem"]');
    if (first) first.focus();
  }

  // One set of listeners serves every menu on the page, including ones added later.
  document.addEventListener('click', e => {
    const toggle = e.target.closest('.dl-toggle');
    if (toggle) {
      if (toggle.disabled) return;
      const d = toggle.closest('.dl');
      if (d.querySelector('.dl-menu').hidden) openMenu(d); else closeMenus();
      return;
    }
    if (e.target.closest('.dl-menu a')) { setTimeout(closeMenus, 0); return; }
    if (!e.target.closest('.dl-menu')) closeMenus();
  });

  document.addEventListener('keydown', e => {
    const d = e.target.closest && e.target.closest('.dl');
    if (!d) return;
    const items = Array.from(d.querySelectorAll('[role="menuitem"]'));
    const i = items.indexOf(document.activeElement);
    if (e.key === 'Escape') {
      closeMenus();
      d.querySelector('.dl-toggle').focus();
    } else if (e.key === 'ArrowDown' && items.length) {
      e.preventDefault();
      if (d.querySelector('.dl-menu').hidden) openMenu(d);
      else items[(i + 1) % items.length].focus();
    } else if (e.key === 'ArrowUp' && items.length) {
      e.preventDefault();
      items[(i - 1 + items.length) % items.length].focus();
    } else if (e.key === 'Tab') {
      closeMenus();
    }
  });

  // ------------------------------------------------------------------ results: chart + table

  function addResult(extras, r) {
    const card = el('div', 'result');
    const head = el('div', 'result-head');
    const titles = el('div');
    titles.appendChild(el('div', 'result-title', r.title));
    const metaParts = [r.rows.length + (r.rows.length === 1 ? ' row' : ' rows'), 'as of ' + formatValue(r.asOf)];
    if (r.truncated) metaParts.push('more rows exist');
    titles.appendChild(el('div', 'result-meta', metaParts.join(', ')));
    head.appendChild(titles);

    const actions = el('div', 'result-actions');
    const menu = downloadMenu('Download', 'btn btn-small btn-outline', [
      { label: 'Excel', ext: '.xlsx', run: () => downloadResult(r, toggle, 'xlsx') },
      { label: 'Word', ext: '.docx', run: () => downloadResult(r, toggle, 'docx') },
      { label: 'PDF', ext: '.pdf', run: () => downloadResult(r, toggle, 'pdf') }
    ]);
    const toggle = menu.querySelector('.dl-toggle');
    actions.appendChild(menu);
    head.appendChild(actions);
    card.appendChild(head);

    if (r.note) card.appendChild(el('div', 'result-note', r.note));

    const visual = String(r.visual || 'None').toLowerCase();
    if (r.rows.length > 0 && ['line', 'bar', 'pie'].includes(visual) && r.chartValueColumns && window.Chart) {
      const holder = el('div', 'chart-holder');
      const canvas = document.createElement('canvas');
      holder.appendChild(canvas);
      card.appendChild(holder);
      drawChart(canvas, r, visual);
    }

    if (r.rows.length > 0) card.appendChild(buildTable(r));
    else card.appendChild(el('div', 'result-empty', 'No data for this period. Try another period inside the range given in the answer.'));

    extras.appendChild(card);
  }

  // Same design rules as the PDF reports: rankings are horizontal bars with the names on the left,
  // time series are vertical bars or lines, MRA green throughout (the deepest green marks the leader), no pie charts.
  const GREEN = '#1E6B3C';
  const LEADER = '#0B391B';
  const SECOND = '#8DBE9C';
  const REST = '#5E9C74';
  if (window.Chart) {
    window.Chart.defaults.font.family = '"Public Sans", "Segoe UI", system-ui, sans-serif';
    window.Chart.defaults.font.size = 12;
    window.Chart.defaults.color = '#56685C';
    window.Chart.defaults.borderColor = '#E3ECE6';
  }
  const HEADERS = {
    GrossSales: 'Gross sales (MWK)', VAT: 'VAT (MWK)', TaxLines: 'Tax lines', TaxableAmount: 'Taxable amount (MWK)',
    BusinessName: 'Business name', FlagType: 'Flag type', RedFlagged: 'Red-flagged', Amount: 'Amount (MWK)',
    InvestigationStatus: 'Investigation status', StationCode: 'Code', AssessedAmount: 'Assessed (MWK)',
    PaidAmount: 'Paid (MWK)', TaxOffice: 'Tax office', FirstFailure: 'First failure', LastFailure: 'Last failure',
    RecalledInvoices: 'Recalled invoices', RecalledValue: 'Recalled value (MWK)', RecallType: 'Recall type',
    TerminalId: 'Terminal', TaxpayersRegistered: 'Taxpayers registered', ImportDeclarations: 'Import declarations',
    FoundIn: 'Found in', RateID: 'Tax rate', RateChargedPct: 'Rate charged (%)', CustomsStationName: 'Customs station name', TaxOfficeName: 'Tax office name',
    GrossSales1: 'Gross sales, period 1 (MWK)', GrossSales2: 'Gross sales, period 2 (MWK)', VAT1: 'VAT, period 1 (MWK)', VAT2: 'VAT, period 2 (MWK)', Invoices1: 'Invoices, period 1', Invoices2: 'Invoices, period 2', TurnoverChangePct: 'Sales change (%)', VatChangePct: 'VAT change (%)', CIFValue: 'CIF value (MWK)', TaxesAssessed: 'Taxes assessed (MWK)', ReceiptedDeclarations: 'Paid declarations', AmountApplied: 'Amount applied (MWK)', AmountPaid: 'Amount paid (MWK)', SettledApplications: 'Paid applications', AverageMonthlyRentAmount: 'Average monthly rent (MWK)', UnitPriceAmount: 'Unit price (MWK)', PriceAmount: 'Price (MWK)', DiscountAmount: 'Discount (MWK)', LineValue: 'Line value (MWK)', StockValue: 'Stock value (MWK)', SalesValue: 'Sales value (MWK)', InvoiceValue: 'Invoice value (MWK)', HsChapterCode: 'HS chapter', HsChapter: 'Tariff chapter', OriginCountryCode: 'Origin code', OriginCountry: 'Country of origin', POSVersion: 'POS version', TaxName: 'Tax', TaxCode: 'Tax code', VatRegistered: 'VAT-registered', WeekStarting: 'Week starting', BuyerTIN: 'Buyer TIN', TPIN: 'TPIN', UOM: 'Unit'
  };
  const header = name => HEADERS[name] || splitWords(name);
  const isMoney = name => /Sales|VAT|Amount|Value|Paid|Assessed/.test(name);
  const money = new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

  function splitWords(text) {
    if (typeof text !== 'string' || text.includes(' ') || !/[a-z]/.test(text) || !/[A-Z]/.test(text.slice(1))) return text;
    const spaced = text.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/([A-Z])([A-Z][a-z])/g, '$1 $2');
    return (spaced[0].toUpperCase() + spaced.slice(1).toLowerCase()).replace('In accurate', 'Inaccurate');
  }

  function cellText(column, v) {
    if (typeof v === 'number' && isMoney(column)) return money.format(v);
    if (typeof v === 'string' && !/^\d{4}-\d{2}-\d{2}/.test(v)) return splitWords(v);
    return formatValue(v);
  }

  function shortMoney(v) {
    const a = Math.abs(v);
    if (a >= 1e9) return (v / 1e9).toFixed(2) + ' bn';
    if (a >= 1e6) return (v / 1e6).toFixed(1) + ' m';
    return numberFmt.format(v);
  }

  function labelColumn(r) {
    for (const name of ['BusinessName', 'Station', 'FlagType', 'TaxOffice', 'InvestigationStatus']) {
      const i = r.columns.indexOf(name);
      if (i >= 0) return i;
    }
    return 0;
  }

  function drawChart(canvas, r, visual) {
    const column = r.chartValueColumns[0];
    const vi = r.columns.findIndex(c => c.toLowerCase() === column.toLowerCase());
    if (vi < 0) return;
    const num = v => (typeof v === 'number' ? v : Number(v) || 0);
    const isDate = typeof r.rows[0][0] === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(r.rows[0][0]);
    const moneyAxis = isMoney(column);
    const tick = v => (moneyAxis ? shortMoney(v) : numberFmt.format(v));

    if (isDate) {
      // Time series: one dataset per chart column, strong colours, readable dates.
      const datasets = r.chartValueColumns
        .map((name, n) => ({ name, index: r.columns.indexOf(name), n }))
        .filter(x => x.index >= 0)
        .map(x => ({
          label: header(x.name),
          data: r.rows.map(row => num(row[x.index])),
          backgroundColor: x.n === 0 ? GREEN : SECOND,
          borderColor: x.n === 0 ? GREEN : SECOND,
          borderWidth: 3, pointRadius: 3, tension: 0.2
        }));
      canvas.parentElement.style.height = '300px';
      new window.Chart(canvas, {
        type: visual === 'line' ? 'line' : 'bar',
        data: { labels: r.rows.map(row => row[0]), datasets },
        options: {
          responsive: true, maintainAspectRatio: false, animation: false,
          plugins: {
            legend: { display: datasets.length > 1 },
            tooltip: { callbacks: { label: c => c.dataset.label + ': ' + (moneyAxis ? money.format(c.parsed.y) : numberFmt.format(c.parsed.y)) } }
          },
          scales: {
            x: { ticks: { maxRotation: 0, autoSkip: true, autoSkipPadding: 12 } },
            y: { beginAtZero: true, ticks: { callback: tick } }
          }
        }
      });
      return;
    }

    // Ranking: horizontal bars, largest at the top, names in full on the left, leader in the deepest green.
    const li = labelColumn(r);
    const items = r.rows
      .map(row => ({ label: String(cellText(r.columns[li], row[li]) || cellText(r.columns[0], row[0]) || '(not set)'), value: num(row[vi]) }))
      .sort((a, b) => b.value - a.value)
      .slice(0, 15);
    const total = r.rows.reduce((t, row) => t + num(row[vi]), 0);
    canvas.parentElement.style.height = (60 + 30 * items.length) + 'px';
    new window.Chart(canvas, {
      type: 'bar',
      data: {
        labels: items.map(i => (i.label.length > 34 ? i.label.slice(0, 33) + '…' : i.label)),
        datasets: [{
          label: header(column),
          data: items.map(i => i.value),
          backgroundColor: items.map((_, n) => (n === 0 ? LEADER : REST)),
          borderRadius: 3, barPercentage: 0.8
        }]
      },
      options: {
        indexAxis: 'y', responsive: true, maintainAspectRatio: false, animation: false,
        plugins: {
          legend: { display: false },
          tooltip: {
            callbacks: {
              label: c => {
                const v = c.parsed.x;
                const share = visual === 'pie' && total > 0 ? '  (' + (100 * v / total).toFixed(1) + '%)' : '';
                return header(column) + ': ' + (moneyAxis ? money.format(v) : numberFmt.format(v)) + share;
              }
            }
          }
        },
        scales: {
          x: { beginAtZero: true, ticks: { callback: tick, maxRotation: 0, autoSkipPadding: 16 } },
          y: { ticks: { autoSkip: false } }
        }
      }
    });
  }

  function buildTable(r) {
    const wrap = el('div', 'table-wrap');
    const table = el('table');
    const thead = el('thead');
    const headRow = el('tr');
    const tbody = el('tbody');
    let rows = r.rows.slice();
    let sortCol = -1;
    let sortAsc = true;

    const numericCols = r.columns.map((_, i) => r.rows.some(row => typeof row[i] === 'number'));

    r.columns.forEach((name, i) => {
      const th = el('th', numericCols[i] ? 'num' : '', header(name));
      th.title = 'Sort';
      th.addEventListener('click', () => {
        sortAsc = sortCol === i ? !sortAsc : true;
        sortCol = i;
        headRow.querySelectorAll('th').forEach(h => h.removeAttribute('aria-sort'));
        th.setAttribute('aria-sort', sortAsc ? 'ascending' : 'descending');
        rows.sort((a, b) => {
          const x = a[i], y = b[i];
          if (x === y) return 0;
          if (x === null || x === undefined) return 1;
          if (y === null || y === undefined) return -1;
          return (x < y ? -1 : 1) * (sortAsc ? 1 : -1);
        });
        renderBody();
      });
      headRow.appendChild(th);
    });
    thead.appendChild(headRow);

    function renderBody() {
      tbody.textContent = '';
      rows.slice(0, MAX_TABLE_ROWS).forEach(row => {
        const tr = el('tr');
        row.forEach((v, i) => tr.appendChild(el('td', numericCols[i] ? 'num' : '', cellText(r.columns[i], v))));
        tbody.appendChild(tr);
      });
    }
    renderBody();

    table.appendChild(thead);
    table.appendChild(tbody);
    wrap.appendChild(table);
    if (r.rows.length > MAX_TABLE_ROWS) {
      wrap.appendChild(el('div', 'result-meta', 'Showing the first ' + MAX_TABLE_ROWS + ' rows. Download as Excel for all of them.'));
    }
    return wrap;
  }

  async function downloadResult(r, button, format) {
    const label = button.textContent;
    button.disabled = true;
    button.textContent = 'Preparing…';
    try {
      const res = await fetch('/api/export/' + format, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(r)
      });
      if (res.status === 401) { signInAgain(); return; }
      if (!res.ok) throw new Error(await res.text());
      const blob = await res.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = (r.title || 'result').replace(/[\\/:*?"<>|]/g, '-').slice(0, 80) + '.' + format;
      document.body.appendChild(a);
      a.click();
      a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 5000);
      button.textContent = label;
    } catch (e) {
      button.textContent = 'Download failed';
      button.title = 'The file could not be created. Try again, or tell the administrator.';
      setTimeout(() => { button.textContent = label; button.title = ''; }, 4000);
    } finally {
      button.disabled = false;
    }
  }

  // ------------------------------------------------------------------ asking

  // What the model sees: earlier questions and answers (failed ones left out), most recent last.
  function modelHistory(upTo) {
    const messages = [];
    conversation.turns.slice(0, upTo).forEach(t => {
      if (t.failed) return;
      messages.push({ role: 'user', content: t.question });
      messages.push({ role: 'assistant', content: (t.answer || '(Results shown as a table.)') + tablesNote(t) });
    });
    return messages;
  }

  // A short note of the tables shown under an answer, so follow-ups like "the first one" or
  // "what about by gross sales?" can refer to them. Kept small for the model's memory.
  function tablesNote(turn) {
    const notes = (turn.events || []).filter(e => e.type === 'result' && e.result && e.result.rows).map(e => {
      const r = e.result;
      const rows = r.rows.slice(0, 5).map((row, i) => (i + 1) + '. ' + row.slice(0, 3).map(v => formatValue(v)).join(' | '));
      return 'Table "' + r.title + '" (' + r.columns.slice(0, 3).join(' | ') + '): ' + rows.join('; ');
    });
    if (notes.length === 0) return '';
    const text = '\n[Shown to the user: ' + notes.join(' ') + ']';
    return text.length > 700 ? text.slice(0, 697) + '...]' : text;
  }

  // Shows one stored event under an answer (used live and when a saved conversation is opened).
  function showEvent(reply, ev) {
    switch (ev.type) {
      case 'tool': addToolNote(reply.extras, ev); break;
      case 'result': addResult(reply.extras, ev.result); break;
      case 'report': addReportButtons(reply.extras, ev.report); break;
      case 'warning': reply.wrap.insertBefore(el('div', 'warning', ev.message), reply.body); break;
      case 'error': showError(reply, ev.message); break;
      case 'stopped': reply.extras.appendChild(el('div', 'stopped-note', ev.message)); break;
    }
  }

  async function ask(question) {
    question = question.trim();
    if (busy || !question) return;
    setBusy(true);

    const turn = { question, answer: '', events: [], failed: false };
    const messages = modelHistory(conversation.turns.length).slice(-9);
    messages.push({ role: 'user', content: question });
    conversation.turns.push(turn);
    if (!conversation.title) setTitle(question);

    const q = addQuestion(turn);
    const reply = addMessage('assistant', '');
    turn.nodes = [q.wrap, reply.wrap];
    reply.body.classList.add('pending');
    reply.body.textContent = 'Thinking…';
    scrollDown(true);

    let failed = false;
    controller = new AbortController();

    function handle(ev) {
      switch (ev.type) {
        case 'delta':
          if (!turn.answer) reply.body.classList.remove('pending');
          turn.answer += ev.text;
          renderAnswer(reply.body, turn.answer);
          break;
        case 'reset':
          // The first reply described a query instead of running it; the server is retrying.
          turn.answer = '';
          // A caution about the discarded text no longer applies to the new one.
          reply.wrap.querySelectorAll(':scope > .warning').forEach(w => w.remove());
          turn.events = turn.events.filter(e => e.type !== 'warning');
          reply.body.classList.add('pending');
          reply.body.textContent = 'Looking that up…';
          break;
        case 'error':
          failed = true;
          turn.events.push(ev);
          showEvent(reply, ev);
          break;
        case 'tool': case 'result': case 'report': case 'warning':
          turn.events.push(ev);
          showEvent(reply, ev);
          break;
      }
      scrollDown();
    }

    try {
      const res = await fetch('/api/chat', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ messages }),
        signal: controller.signal
      });
      if (res.status === 401) { signInAgain(); return; }
      if (!res.ok || !res.body) throw new Error((await res.text()) || res.statusText);

      const reader = res.body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';
      for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });
        let nl;
        while ((nl = buffer.indexOf('\n')) >= 0) {
          const line = buffer.slice(0, nl).trim();
          buffer = buffer.slice(nl + 1);
          if (line) handle(JSON.parse(line));
        }
      }
      if (buffer.trim()) handle(JSON.parse(buffer));
    } catch (e) {
      failed = true;
      const ev = e.name === 'AbortError'
        ? { type: 'stopped', message: 'You stopped this answer.' }
        : { type: 'error', message: 'Request failed: ' + e.message };
      if (conversation.turns.includes(turn)) {
        turn.events.push(ev);
        showEvent(reply, ev);
      }
    } finally {
      reply.body.classList.remove('pending');
      if (!turn.answer) reply.body.textContent = failed ? '' : '(No answer text. See the results below.)';
      if (!reply.body.textContent) reply.body.hidden = true;
      turn.failed = failed;
      controller = null;
      setBusy(false);
      input.focus();
      if (conversation.turns.includes(turn)) saveConversation();
    }
  }

  // ------------------------------------------------------------------ saved conversations

  const historyList = document.getElementById('history-list');
  const historyEmpty = document.getElementById('history-empty');
  const deskTitle = document.getElementById('desk-title');

  function setTitle(text) {
    conversation.title = text.length > 60 ? text.slice(0, 57).trimEnd() + '…' : text;
    deskTitle.textContent = conversation.title;
    document.title = conversation.title + ' | Reporting Assistant';
  }

  function newId() {
    if (window.crypto && crypto.randomUUID) return crypto.randomUUID();
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
      const r = Math.random() * 16 | 0;
      return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
    });
  }

  async function saveConversation() {
    if (conversation.turns.length === 0) return;
    if (!conversation.id) conversation.id = newId();
    const body = JSON.stringify({ title: conversation.title, turns: conversation.turns },
      (key, value) => (key === 'nodes' ? undefined : value));
    try {
      const res = await fetch('/api/history/' + conversation.id, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body
      });
      if (res.status === 401) { signInAgain(); return; }
      await loadHistoryList();
    } catch (e) { /* not saved; the conversation stays on screen */ }
  }

  async function loadHistoryList() {
    let items = [];
    try {
      const res = await fetch('/api/history');
      if (res.status === 401) { signInAgain(); return; }
      if (res.ok) items = await res.json();
    } catch (e) { /* list stays as it was */ }
    renderHistory(items);
  }

  // Groups: Today, Yesterday, Previous 7 days, then by month.
  function groupName(iso) {
    const d = new Date(iso);
    const today = new Date(); today.setHours(0, 0, 0, 0);
    const day = new Date(d); day.setHours(0, 0, 0, 0);
    const diff = Math.round((today - day) / 86400000);
    if (diff <= 0) return 'Today';
    if (diff === 1) return 'Yesterday';
    if (diff < 7) return 'Previous 7 days';
    return d.toLocaleDateString('en-GB', { month: 'long', year: 'numeric' });
  }

  const TRASH = '<svg viewBox="0 0 24 24" width="15" height="15" aria-hidden="true"><path d="M5 7h14M10 7V5h4v2M7 7l1 12h8l1-12" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round" stroke-linecap="round"/></svg>';

  function renderHistory(items) {
    historyList.textContent = '';
    historyEmpty.hidden = items.length > 0;

    let group = null;
    let list = null;
    items.forEach(item => {
      const g = groupName(item.updatedAt);
      if (g !== group) {
        group = g;
        const section = el('div', 'history-group');
        section.appendChild(el('p', 'history-group-title', g));
        list = el('ul');
        section.appendChild(list);
        historyList.appendChild(section);
      }
      const li = el('li', 'history-item' + (item.id === conversation.id ? ' current' : ''));
      const open = el('button', 'history-open', item.title || 'Untitled conversation');
      open.type = 'button';
      open.title = item.title || '';
      open.addEventListener('click', () => openConversation(item.id));
      const del = el('button', 'history-delete');
      del.type = 'button';
      del.innerHTML = TRASH;
      del.title = 'Delete this conversation';
      del.setAttribute('aria-label', 'Delete conversation: ' + (item.title || 'untitled'));
      let timer = null;
      del.addEventListener('click', async () => {
        if (!del.classList.contains('confirm')) {
          // First click asks for confirmation; a second click within 4 seconds deletes.
          del.classList.add('confirm');
          del.textContent = 'Delete';
          timer = setTimeout(() => { del.classList.remove('confirm'); del.innerHTML = TRASH; }, 4000);
          return;
        }
        clearTimeout(timer);
        await fetch('/api/history/' + item.id, { method: 'DELETE' }).catch(() => {});
        if (item.id === conversation.id) startNewConversation();
        loadHistoryList();
      });
      li.appendChild(open);
      li.appendChild(del);
      list.appendChild(li);
    });
  }

  function clearScreen() {
    if (controller) controller.abort();
    messagesEl.querySelectorAll('.msg').forEach(m => m.remove());
  }

  function startNewConversation() {
    clearScreen();
    conversation = newConversationState();
    deskTitle.textContent = '';
    document.title = 'MRA Reporting Assistant';
    if (welcome) welcome.hidden = false;
    desk.classList.add('empty');
    greet();
    input.value = '';
    fitInput();
    input.placeholder = 'Ask a question';
    historyList.querySelectorAll('.history-item.current').forEach(li => li.classList.remove('current'));
    closeRailOnPhone();
    input.focus();
  }

  async function openConversation(id, findText) {
    if (busy) return;
    let saved;
    try {
      const res = await fetch('/api/history/' + id);
      if (res.status === 401) { signInAgain(); return; }
      if (!res.ok) { loadHistoryList(); return; }
      saved = await res.json();
    } catch (e) { return; }

    clearScreen();
    conversation = { id: saved.id || id, title: saved.title || '', turns: [] };
    setTitle(conversation.title || 'Untitled conversation');
    (saved.turns || []).forEach(t => {
      const turn = { question: t.question || '', answer: t.answer || '', events: t.events || [], failed: !!t.failed };
      conversation.turns.push(turn);
      const q = addQuestion(turn);
      const reply = addMessage('assistant', '');
      turn.nodes = [q.wrap, reply.wrap];
      if (turn.answer) renderAnswer(reply.body, turn.answer);
      turn.events.forEach(ev => showEvent(reply, ev));
    });
    messagesEl.style.scrollBehavior = 'auto';
    scrollDown(true);
    messagesEl.style.scrollBehavior = '';
    if (findText) setTimeout(() => jumpToText(findText), 60); // after charts have taken their space
    historyList.querySelectorAll('.history-item').forEach(li => li.classList.remove('current'));
    loadHistoryList();
    closeRailOnPhone();
  }

  // ------------------------------------------------------------------ jump to searched words

  // Marks the searched words in yellow in the open conversation and scrolls to the first one.
  // The whole phrase is looked for first; if it is not there, each word on its own.
  function jumpToText(query) {
    clearFound();
    const q = query.trim();
    if (!q) return;
    const esc = t => t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    let pattern = new RegExp(esc(q), 'gi');
    let marks = markText(pattern);
    if (marks.length === 0) {
      const words = q.split(/\s+/).filter(Boolean).map(esc);
      pattern = new RegExp('(' + words.join('|') + ')', 'gi');
      marks = markText(pattern);
    }
    if (marks.length === 0) return;
    marks[0].classList.add('current');
    marks[0].scrollIntoView({ block: 'center', behavior: 'smooth' });
    followBottom = false;
  }

  function markText(pattern) {
    const marks = [];
    const walker = document.createTreeWalker(messagesEl, NodeFilter.SHOW_TEXT, {
      acceptNode: n => (n.nodeValue.trim() && !n.parentElement.closest('script, style, button, .edit-box')
        ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_REJECT)
    });
    const nodes = [];
    while (walker.nextNode()) nodes.push(walker.currentNode);
    nodes.forEach(node => {
      const text = node.nodeValue;
      pattern.lastIndex = 0;
      if (!pattern.test(text)) return;
      pattern.lastIndex = 0;
      const frag = document.createDocumentFragment();
      let last = 0;
      let m;
      while ((m = pattern.exec(text)) !== null) {
        if (m[0].length === 0) { pattern.lastIndex++; continue; }
        frag.appendChild(document.createTextNode(text.slice(last, m.index)));
        const mark = el('mark', 'found', m[0]);
        frag.appendChild(mark);
        marks.push(mark);
        last = m.index + m[0].length;
      }
      frag.appendChild(document.createTextNode(text.slice(last)));
      node.parentNode.replaceChild(frag, node);
    });
    return marks;
  }

  function clearFound() {
    messagesEl.querySelectorAll('mark.found').forEach(m => m.replaceWith(document.createTextNode(m.textContent)));
    messagesEl.normalize();
  }

  // ------------------------------------------------------------------ greeting

  const greetingEl = document.getElementById('welcome-title');
  const leadEl = document.getElementById('welcome-lead');
  const firstName = (document.body.dataset.firstName || '').trim();

  function pick(list) { return list[Math.floor(Math.random() * list.length)]; }

  function greet() {
    const now = new Date();
    const h = now.getHours();
    const day = now.getDay();
    const name = firstName ? ', ' + firstName : '';
    const part = h < 12 ? 'Good morning' : h < 17 ? 'Good afternoon' : 'Good evening';
    const options = [part + name, 'Welcome back' + name, 'Hello' + name, 'Good to see you' + name];
    if (h >= 5 && h < 7) options.push("You're up early" + name);
    if (h >= 20 || h < 5) options.push('Working late' + name + '?');
    if (day === 1 && h < 12) options.push('A new week begins' + name);
    if (day === 5) options.push('Happy Friday' + name);
    if (now.getDate() === 1) options.push('A new month begins' + name);
    greetingEl.textContent = pick(options);
    leadEl.textContent = pick([
      'What would you like to know? Ask in plain words, and every answer comes with the table it was taken from.',
      'Which figures shall we look at today? Sales, VAT, customs payments or compliance.',
      'Ask about any period, office, station or taxpayer, and download the results when you need them.',
      'Ask a question in your own words. You can also prepare a summary report from the side panel.'
    ]);
  }

  // ------------------------------------------------------------------ wiring

  greet();

  // The question box grows with the text, up to a limit.
  function fitInput() {
    input.style.height = 'auto';
    input.style.height = Math.min(input.scrollHeight, 180) + 'px';
  }
  input.addEventListener('input', fitInput);

  form.addEventListener('submit', e => {
    e.preventDefault();
    if (busy) { stopAnswer(); return; } // the button reads Stop while an answer is coming
    const q = input.value;
    input.value = '';
    fitInput();
    ask(q);
  });

  input.addEventListener('keydown', e => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      if (!busy) form.requestSubmit(); // Enter never stops an answer by accident
    }
  });

  document.getElementById('new-chat').addEventListener('click', startNewConversation);

  // ---------------- side panel: open and close from inside the panel (a slim icon bar when closed);
  // on phones it slides in from the left, opened from the top bar.
  const shell = document.getElementById('shell');
  const railToggle = document.getElementById('rail-toggle');
  const railOpen = document.getElementById('rail-open');
  const scrim = document.getElementById('scrim');
  const phone = window.matchMedia('(max-width: 860px)');

  function remember(key, value) { try { localStorage.setItem(key, value); } catch (e) { /* not available */ } }
  function recall(key) { try { return localStorage.getItem(key); } catch (e) { return null; } }

  function railVisible() {
    return phone.matches ? shell.classList.contains('rail-open') : !shell.classList.contains('rail-collapsed');
  }
  function syncToggle() {
    const open = railVisible();
    railToggle.setAttribute('aria-expanded', String(open));
    const label = open ? 'Close the side panel' : 'Open the side panel';
    railToggle.setAttribute('aria-label', label);
    railToggle.title = label;
  }
  function setRail(open) {
    if (phone.matches) {
      shell.classList.toggle('rail-open', open);
      if (scrim) scrim.hidden = !open;
    } else {
      shell.classList.toggle('rail-collapsed', !open);
      remember('mra.rail', open ? 'open' : 'closed');
    }
    syncToggle();
  }
  function closeRailOnPhone() { if (phone.matches) setRail(false); }
  function applyLayout() {
    // The slim bar is for wide screens only; phones always use the full sliding panel.
    shell.classList.toggle('rail-collapsed', !phone.matches && recall('mra.rail') === 'closed');
    shell.classList.remove('rail-open');
    if (scrim) scrim.hidden = true;
    syncToggle();
  }

  railToggle.addEventListener('click', () => setRail(!railVisible()));
  if (railOpen) railOpen.addEventListener('click', () => setRail(true));
  if (scrim) scrim.addEventListener('click', () => setRail(false));
  phone.addEventListener('change', applyLayout);
  applyLayout();

  // ---------------- search: a pop-up window. Recent conversations first; typing searches titles,
  // questions, answers and names in the tables (on the server).
  // If the page is an older version without the search window, build it here so the button still works.
  if (!document.getElementById('search-dialog')) {
    const d = document.createElement('dialog');
    d.className = 'search-dialog';
    d.id = 'search-dialog';
    d.setAttribute('aria-label', 'Search conversations');
    d.innerHTML =
      '<div class="search-head"><input type="search" id="search-input" placeholder="Search..." aria-label="Search your conversations" autocomplete="off" spellcheck="false">' +
      '<button type="button" class="search-clear-text" id="search-clear" hidden>Clear</button><span class="search-divider" aria-hidden="true"></span>' +
      '<form method="dialog"><button type="submit" class="icon-btn" aria-label="Close search" title="Close">' +
      '<svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg></button></form></div>' +
      '<div class="search-tabs" id="search-tabs" role="tablist" hidden>' +
      ['all:All', 'question:Questions', 'answer:Answers', 'table:Tables'].map((t, i) => {
        const [kind, label] = t.split(':');
        return '<button type="button" role="tab" class="search-tab' + (i === 0 ? ' active' : '') + '" data-kind="' + kind + '" aria-selected="' + (i === 0) + '">' + label + '</button>';
      }).join('') + '</div>' +
      '<p class="search-label" id="search-label">Recent conversations</p><ul class="search-results" id="search-results" role="listbox"></ul>' +
      '<p class="search-empty" id="search-empty" hidden></p>';
    document.body.appendChild(d);
  }
  const searchDialog = document.getElementById('search-dialog');
  const searchInput = document.getElementById('search-input');
  const searchResults = document.getElementById('search-results');
  const searchLabel = document.getElementById('search-label');
  const searchEmpty = document.getElementById('search-empty');
  const BUBBLE = '<svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true"><path d="M12 4c4.7 0 8.5 3.2 8.5 7.2S16.7 18.4 12 18.4c-1 0-2-.1-2.9-.4L5 20l.9-3.6C4.4 15 3.5 13.2 3.5 11.2 3.5 7.2 7.3 4 12 4z" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"/></svg>';
  const searchClear = document.getElementById('search-clear');
  const searchTabs = document.getElementById('search-tabs');
  let searchTimer = null;
  let searchSeq = 0;
  let activeIndex = -1;
  let searchKind = 'all';
  let lastItems = [];
  let lastQuery = '';

  // Escapes text for HTML and puts each search word in bold.
  function highlight(text, query) {
    const safe = String(text).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    const words = query.split(/\s+/).filter(Boolean).map(w => w.replace(/[.*+?^${}()|[\]\\]/g, '\\$&').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'));
    if (!words.length) return safe;
    return safe.replace(new RegExp('(' + words.join('|') + ')', 'gi'), '<mark class="hit">$1</mark>');
  }

  function shortWhen(iso) {
    const g = groupName(iso);
    return g === 'Previous 7 days' ? new Date(iso).toLocaleDateString('en-GB', { weekday: 'long' }) : g;
  }

  async function runSearch() {
    const q = searchInput.value.trim();
    const seq = ++searchSeq;
    let items = [];
    try {
      const res = await fetch('/api/history' + (q ? '?q=' + encodeURIComponent(q) : ''));
      if (res.status === 401) { signInAgain(); return; }
      if (res.ok) items = await res.json();
    } catch (e) { /* show nothing new */ }
    if (seq !== searchSeq) return; // a newer search has started
    if (!q) items = items.slice(0, 8);
    lastItems = items;
    lastQuery = q;
    renderSearch();
  }

  function renderSearch() {
    const q = lastQuery;
    const pickMatch = item => {
      const m = item.match || {};
      return searchKind === 'all' ? m.best : m[searchKind];
    };
    const items = q && searchKind !== 'all' ? lastItems.filter(pickMatch) : lastItems;

    searchClear.hidden = !searchInput.value;
    searchTabs.hidden = !q;
    searchLabel.textContent = q ? (items.length === 1 ? '1 conversation' : items.length + ' conversations') : 'Recent conversations';
    searchLabel.hidden = !q && items.length === 0;
    searchEmpty.hidden = items.length > 0;
    searchEmpty.textContent = q ? 'No conversations match "' + q + '"' + (searchKind === 'all' ? '.' : ' here. Try All.') : 'Your conversations will appear here once you ask a question.';
    searchResults.textContent = '';
    activeIndex = items.length ? 0 : -1;
    items.forEach((item, i) => {
      const li = el('li');
      const b = el('button', 'search-item' + (i === 0 ? ' active' : ''));
      b.type = 'button';
      b.setAttribute('role', 'option');
      b.innerHTML = BUBBLE;
      const text = el('span', 'search-item-text');
      const title = el('span', 'search-title');
      title.innerHTML = highlight(item.title || 'Untitled conversation', q);
      text.appendChild(title);
      const snippet = q ? pickMatch(item) : null;
      if (snippet) {
        const m = el('span', 'search-match');
        m.innerHTML = highlight(snippet, q);
        text.appendChild(m);
      }
      b.appendChild(text);
      b.appendChild(el('span', 'search-when', shortWhen(item.updatedAt)));
      b.addEventListener('click', () => { searchDialog.close(); openConversation(item.id, q); });
      b.addEventListener('mousemove', () => setActive(i));
      li.appendChild(b);
      searchResults.appendChild(li);
    });
  }

  function setActive(i) {
    const items = searchResults.querySelectorAll('.search-item');
    if (!items.length) return;
    activeIndex = (i + items.length) % items.length;
    items.forEach((b, n) => b.classList.toggle('active', n === activeIndex));
    items[activeIndex].scrollIntoView({ block: 'nearest' });
  }

  function openSearch() {
    if (searchDialog.open) { searchInput.focus(); return; }
    searchInput.value = '';
    searchKind = 'all';
    searchTabs.querySelectorAll('.search-tab').forEach((t, i) => {
      t.classList.toggle('active', i === 0);
      t.setAttribute('aria-selected', String(i === 0));
    });
    closeRailOnPhone();
    if (typeof searchDialog.showModal === 'function') searchDialog.showModal();
    else searchDialog.setAttribute('open', '');
    searchInput.focus();
    runSearch();
  }

  // Any search button opens the window (the icon in the panel header, or an older "Search" button).
  document.addEventListener('click', e => {
    if (e.target.closest('#search-btn, [data-open-search]')) { e.preventDefault(); openSearch(); }
  });
  searchDialog.addEventListener('click', e => { if (e.target === searchDialog) searchDialog.close(); }); // click outside closes
  searchClear.addEventListener('click', () => {
    searchInput.value = '';
    searchInput.focus();
    runSearch();
  });
  searchTabs.addEventListener('click', e => {
    const tab = e.target.closest('.search-tab');
    if (!tab) return;
    searchKind = tab.dataset.kind;
    searchTabs.querySelectorAll('.search-tab').forEach(t => {
      t.classList.toggle('active', t === tab);
      t.setAttribute('aria-selected', String(t === tab));
    });
    renderSearch();
    searchInput.focus();
  });
  searchInput.addEventListener('input', () => {
    searchClear.hidden = !searchInput.value;
    clearTimeout(searchTimer);
    searchTimer = setTimeout(runSearch, 220);
  });
  searchInput.addEventListener('keydown', e => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setActive(activeIndex + 1); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(activeIndex - 1); }
    else if (e.key === 'Enter') {
      e.preventDefault();
      const items = searchResults.querySelectorAll('.search-item');
      if (items[activeIndex]) items[activeIndex].click();
    }
  });
  // Ctrl+K (or Cmd+K) opens search from anywhere.
  document.addEventListener('keydown', e => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); openSearch(); }
  });

  // ---------------- the user menu (sign out, delete all conversations)
  const userChip = document.getElementById('user-chip');
  const userPop = document.getElementById('user-pop');
  if (userChip && userPop) {
    const setUserMenu = open => {
      userPop.hidden = !open;
      userChip.setAttribute('aria-expanded', String(open));
      if (open) { const first = userPop.querySelector('[role="menuitem"]'); if (first) first.focus(); }
    };
    userChip.addEventListener('click', e => { e.stopPropagation(); setUserMenu(userPop.hidden); });
    document.addEventListener('click', e => { if (!userPop.hidden && !e.target.closest('#user-menu')) setUserMenu(false); });
    userPop.addEventListener('keydown', e => {
      if (e.key === 'Escape') { setUserMenu(false); userChip.focus(); }
    });

    const clearAll = document.getElementById('clear-history');
    let clearTimer = null;
    clearAll.addEventListener('click', async () => {
      if (!clearAll.classList.contains('confirm')) {
        clearAll.classList.add('confirm');
        clearAll.textContent = 'Click again to delete all';
        clearTimer = setTimeout(() => { clearAll.classList.remove('confirm'); clearAll.textContent = 'Delete all conversations'; }, 4000);
        return;
      }
      clearTimeout(clearTimer);
      clearAll.classList.remove('confirm');
      clearAll.textContent = 'Delete all conversations';
      await fetch('/api/history', { method: 'DELETE' }).catch(() => {});
      setUserMenu(false);
      startNewConversation();
      loadHistoryList();
    });
  }

  // ---------------- summary report window
  const reportDialog = document.getElementById('report-dialog');
  document.getElementById('open-report').addEventListener('click', () => {
    closeRailOnPhone();
    if (reportDialog.showModal) reportDialog.showModal(); else reportDialog.setAttribute('open', '');
  });
  reportDialog.addEventListener('click', e => { if (e.target === reportDialog) reportDialog.close(); }); // click outside closes

  // Report links: default to yesterday for both dates.
  const fromInput = document.getElementById('report-from');
  const toInput = document.getElementById('report-to');
  const pdfLink = document.getElementById('report-pdf');
  const xlsxLink = document.getElementById('report-xlsx');
  const docxLink = document.getElementById('report-docx');
  const pad = n => String(n).padStart(2, '0');
  const isoDate = d => d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate());

  function updateReportLinks() {
    if (toInput.value && fromInput.value && toInput.value < fromInput.value) toInput.value = fromInput.value;
    const q = 'from=' + encodeURIComponent(fromInput.value) + '&to=' + encodeURIComponent(toInput.value || fromInput.value);
    pdfLink.href = '/api/reports/summary?format=pdf&' + q;
    xlsxLink.href = '/api/reports/summary?format=xlsx&' + q;
    docxLink.href = '/api/reports/summary?format=docx&' + q;
  }
  const yesterday = new Date(Date.now() - 86400000);
  fromInput.value = isoDate(yesterday);
  toInput.value = isoDate(yesterday);
  fromInput.addEventListener('change', updateReportLinks);
  toInput.addEventListener('change', updateReportLinks);
  updateReportLinks();

  loadHistoryList();
})();
