/*
 * LineTrans 网页翻译台
 * 与安卓客户端 / 独立服务端（line-trans-web）共用同一套 API：
 *   GET  api/info | api/docs | api/doc?id=ID
 *   POST api/unit  { docId, index, translation?, source?, done?, starred? }
 *   POST api/doc   { docId, name?, folder?, pinned?, unitMode? }
 *   POST api/ai    { docId, index }
 *   GET  api/export?id=ID&format=txt_bilingual
 */
(function () {
  'use strict';

  var QS = new URLSearchParams(location.search);
  var TOKEN = QS.get('token') || '';
  // 深链：安卓原生「设备」页点「直接编辑」会带 ?peer=<设备id>&doc=<文档id> 过来
  var DEEP_PEER = QS.get('peer') || '';
  var DEEP_DOC = QS.get('doc') || '';
  var THEME_KEY = 'lt-theme';
  var WINDOW_SIZE = 60;

  var state = {
    info: null,
    docs: [],
    doc: null,
    filtered: [],
    filter: 'all',
    query: '',
    docQuery: '',
    current: -1,
    windowStart: 0,
    saving: 0,
    batch: { running: false, stop: false, done: 0, total: 0 },
    peers: [],
    remote: null            // 非空 = 正在直接编辑对端文档 { peerId, peerName, docId, name }
  };

  var $ = function (id) { return document.getElementById(id); };
  var editor = null;
  var sentinelObserver = null;

  // ---------------- 主题 ----------------

  function applyTheme(theme) {
    if (theme === 'light' || theme === 'dark') document.documentElement.dataset.theme = theme;
    else delete document.documentElement.dataset.theme;
    try { localStorage.setItem(THEME_KEY, theme); } catch (e) { /* ignore */ }
    Array.prototype.forEach.call(document.querySelectorAll('#themeCards .theme-card'), function (card) {
      card.classList.toggle('active', card.dataset.theme === theme);
    });
    var meta = document.querySelector('meta[name="theme-color"]');
    if (meta) {
      var dark = theme === 'dark' ||
        (theme !== 'light' && window.matchMedia('(prefers-color-scheme: dark)').matches);
      meta.setAttribute('content', dark ? '#000000' : '#ffffff');
    }
  }

  function initTheme() {
    var saved = 'system';
    try { saved = localStorage.getItem(THEME_KEY) || 'system'; } catch (e) { /* ignore */ }
    applyTheme(saved);
  }

  // ---------------- 请求 ----------------

  function api(path, options) {
    var url = path + (path.indexOf('?') >= 0 ? '&' : '?') + 'token=' + encodeURIComponent(TOKEN);
    options = options || {};
    // charset 不能省：安卓端内置网页台跑在 NanoHTTPD 上，请求体没声明 charset 时它按 US-ASCII 解码，
    // 中文会变成**不可逆**的 U+FFFD（网页台打完字 → 回到安卓端全是乱码，见 项目说明与开发指南.md §10）。
    options.headers = Object.assign({ 'Content-Type': 'application/json; charset=utf-8' }, options.headers || {});
    return fetch(url, options).then(function (res) {
      if (!res.ok) {
        return res.text().then(function (t) { throw new Error(t || ('HTTP ' + res.status)); });
      }
      var type = res.headers.get('content-type') || '';
      return type.indexOf('application/json') >= 0 ? res.json() : res.text();
    });
  }

  var toastTimer = null;
  function toast(msg) {
    var el = $('toast');
    el.textContent = msg;
    el.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { el.classList.remove('show'); }, 2200);
  }

  function setConn(text, ok) {
    var el = $('connState');
    el.textContent = text;
    el.className = 'pill' + (ok ? ' on' : '');
  }

  function pct(done, total) { return total ? Math.round(done * 100 / total) : 0; }

  // ---------------- 侧栏（按文件夹分组） ----------------

  function renderSidebar() {
    var list = $('docList');
    var q = state.docQuery.toLowerCase();
    var docs = state.docs.filter(function (d) { return !q || d.name.toLowerCase().indexOf(q) >= 0; });
    list.innerHTML = '';
    if (!docs.length) {
      list.innerHTML = '<div class="muted" style="padding:12px 18px">没有文档</div>';
      return;
    }
    var folders = [];
    docs.forEach(function (d) {
      var f = d.folder || '默认';
      if (folders.indexOf(f) < 0) folders.push(f);
    });
    folders.forEach(function (folder) {
      var group = docs.filter(function (d) { return (d.folder || '默认') === folder; })
        .sort(function (a, b) {
          if (!!b.pinned - !!a.pinned) return (b.pinned ? 1 : 0) - (a.pinned ? 1 : 0);
          return (b.updatedAt || 0) - (a.updatedAt || 0);
        });
      var label = document.createElement('div');
      label.className = 'folder-label';
      label.innerHTML = '<span></span><span class="count"></span>';
      label.querySelector('span').textContent = folder;
      label.querySelector('.count').textContent = group.length;
      list.appendChild(label);
      group.forEach(function (d) { list.appendChild(renderDocItem(d)); });
    });
  }

  function renderDocItem(d) {
    var el = document.createElement('div');
    el.className = 'doc-item' + (state.doc && state.doc.id === d.id ? ' active' : '');
    el.innerHTML =
      '<div class="doc-name">' + (d.pinned ? '📌 ' : '') + '<span></span></div>' +
      '<div class="doc-sub"><span class="mode"></span><span class="p"></span></div>' +
      '<div class="mini-progress"><i></i></div>';
    el.querySelector('.doc-name span').textContent = d.name;
    el.querySelector('.mode').textContent = d.unitMode === 'SENTENCE' ? '逐句' : '逐行';
    el.querySelector('.p').textContent = d.done + ' / ' + d.total + ' · ' + pct(d.done, d.total) + '%';
    el.querySelector('.mini-progress > i').style.width = pct(d.done, d.total) + '%';
    el.addEventListener('click', function () {
      closeSidebar();
      openDoc(d.id);
    });
    return el;
  }

  // ---------------- 文档 ----------------

  function openDoc(id) {
    api('api/doc?id=' + encodeURIComponent(id)).then(function (doc) {
      state.remote = null;
      state.doc = doc;
      state.current = -1;
      renderSidebar();
      renderDoc();
      setConn('已连接', true);
      $('scrollArea').scrollTop = 0;
    }).catch(function (e) { toast('打开失败：' + e.message); });
  }

  function updateProgress() {
    if (!state.doc) return;
    var p = pct(state.doc.done, state.doc.total);
    $('progressBar').style.width = p + '%';
    $('progressText').textContent = state.doc.done + ' / ' + state.doc.total + ' · ' + p + '%';
    $('ringText').textContent = p + '%';
    var circumference = 2 * Math.PI * 15.5;
    $('ringValue').style.strokeDashoffset = String(circumference * (1 - p / 100));
    $('btnMode').textContent = state.doc.unitMode === 'SENTENCE' ? '逐句' : '逐行';

    var remaining = state.doc.total - state.doc.done;
    var canTranslate = !!(state.info && state.info.canTranslate);
    if (state.batch.running) {
      $('btnBatch').textContent = '停止（' + state.batch.done + '/' + state.batch.total + '）';
      $('btnBatch').disabled = false;
    } else if (!canTranslate) {
      $('btnBatch').textContent = '未配置模型';
      $('btnBatch').disabled = true;
      $('btnBatch').title = '请先配置模型（手机端：设置 → AI 翻译；服务端：config.json）';
    } else {
      $('btnBatch').textContent = 'AI 翻译剩余 ' + remaining;
      $('btnBatch').disabled = remaining === 0;
      $('btnBatch').title = '';
    }
  }

  function applyFilter() {
    if (!state.doc) { state.filtered = []; return; }
    var q = state.query.toLowerCase();
    state.filtered = state.doc.units.filter(function (u) {
      if (state.filter === 'todo' && u.done) return false;
      if (state.filter === 'star' && !u.starred) return false;
      if (q && (u.source + ' ' + u.translation).toLowerCase().indexOf(q) < 0) return false;
      return true;
    });
  }

  function renderDoc() {
    var doc = state.doc;
    if (!doc) return;
    $('docTitle').textContent = doc.name;
    $('docSub').textContent = (doc.folder || '默认') + ' · ' +
      (doc.unitMode === 'SENTENCE' ? '逐句' : '逐行') +
      (state.info && state.info.provider ? ' · 模型：' + state.info.provider : ' · 未配置模型');
    $('subbar').hidden = false;
    $('dock').hidden = false;
    $('emptyState').hidden = true;
    $('btnMode').disabled = false;
    $('btnExport').disabled = false;
    updateProgress();
    applyFilter();
    $('unitCount').textContent = '显示 ' + state.filtered.length + ' / ' + doc.total;
    state.windowStart = 0;
    renderWindow(true);
    renderPeerBar();
  }

  /** 分窗口渲染：默认 60 句，滚到底部自动加载下一批（长文档不再卡） */
  function renderWindow(reset) {
    if (reset) {
      closeLookup();
      editor.innerHTML = '';
      state.windowStart = 0;
      $('editorEmpty').hidden = state.filtered.length > 0;
    }
    var from = state.windowStart;
    var to = Math.min(from + WINDOW_SIZE, state.filtered.length);
    for (var i = from; i < to; i++) editor.appendChild(renderRow(state.filtered[i]));
    state.windowStart = to;
    updateWindowInfo();
    observeSentinel();
  }

  function updateWindowInfo() {
    var el = $('windowInfo');
    if (!state.filtered.length) {
      el.hidden = true;
      return;
    }
    el.hidden = false;
    el.textContent = '已显示 ' + state.windowStart + ' / ' + state.filtered.length;
  }

  function observeSentinel() {
    var old = document.getElementById('moreSentinel');
    if (old) old.remove();
    if (state.windowStart >= state.filtered.length) return;
    var sentinel = document.createElement('div');
    sentinel.className = 'loading-more';
    sentinel.id = 'moreSentinel';
    sentinel.textContent = '加载更多…';
    editor.appendChild(sentinel);
    if (!sentinelObserver) {
      sentinelObserver = new IntersectionObserver(function (entries) {
        entries.forEach(function (entry) {
          if (entry.isIntersecting && state.windowStart < state.filtered.length) renderWindow(false);
        });
      }, { root: $('scrollArea'), rootMargin: '400px' });
    }
    sentinelObserver.observe(sentinel);
  }

  function renderRow(u) {
    var row = document.createElement('div');
    row.className = 'row' + (state.current === u.i ? ' current' : '');
    row.dataset.index = u.i;

    var head = document.createElement('div');
    head.className = 'row-head';
    var pill = document.createElement('span');
    pill.className = 'index-pill';
    pill.textContent = (u.i + 1);
    var star = document.createElement('button');
    star.className = 'star' + (u.starred ? ' on' : '');
    star.textContent = u.starred ? '★' : '☆';
    star.title = '收藏';
    star.addEventListener('click', function () { saveUnit(u.i, { starred: !u.starred }); });
    var spacer = document.createElement('span');
    spacer.className = 'spacer';
    var copyBtn = document.createElement('button');
    copyBtn.className = 'tool';
    copyBtn.textContent = '复制';
    var aiBtn = document.createElement('button');
    aiBtn.className = 'tool';
    aiBtn.textContent = 'AI 翻译';
    head.appendChild(pill);
    head.appendChild(star);
    head.appendChild(spacer);
    head.appendChild(copyBtn);
    head.appendChild(aiBtn);

    // 原文
    var sourceBlock = document.createElement('div');
    sourceBlock.className = 'row-block';
    var sourceLabel = document.createElement('div');
    sourceLabel.className = 'row-label';
    sourceLabel.innerHTML = '<span class="dot' + (u.done ? ' done' : '') + '"></span>原文 · 双击可编辑';
    var source = document.createElement('div');
    source.className = 'source';
    source.textContent = u.source;
    source.addEventListener('dblclick', function () { editSource(u, source); });
    sourceBlock.appendChild(sourceLabel);
    sourceBlock.appendChild(source);

    // 译文
    var transBlock = document.createElement('div');
    transBlock.className = 'row-block';
    var transLabel = document.createElement('div');
    transLabel.className = 'row-label';
    transLabel.textContent = '译文';
    var ta = document.createElement('textarea');
    ta.className = 'translation';
    ta.value = u.translation || '';
    ta.placeholder = '在这里输入译文…';
    ta.spellcheck = false;
    var timer = null;
    ta.addEventListener('input', function () {
      clearTimeout(timer);
      timer = setTimeout(function () { saveUnit(u.i, { translation: ta.value }); }, 500);
    });
    ta.addEventListener('focus', function () {
      state.current = u.i;
      highlight();
    });
    ta.addEventListener('keydown', function (e) {
      if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); ta.blur(); gotoRelative(u.i, 1); }
      if (e.altKey && e.key === 'ArrowDown') { e.preventDefault(); gotoRelative(u.i, 1); }
      if (e.altKey && e.key === 'ArrowUp') { e.preventDefault(); gotoRelative(u.i, -1); }
    });
    transBlock.appendChild(transLabel);
    transBlock.appendChild(ta);

    copyBtn.addEventListener('click', function () {
      navigator.clipboard.writeText(ta.value || u.source).then(function () { toast('已复制'); });
    });
    aiBtn.addEventListener('click', function () { aiTranslate(u.i, aiBtn); });

    row.appendChild(head);
    row.appendChild(sourceBlock);
    row.appendChild(transBlock);
    return row;
  }

  function editSource(u, box) {
    closeLookup();
    var ta = document.createElement('textarea');
    ta.className = 'source-edit';
    ta.value = u.source;
    var restore = function () {
      box.textContent = u.source;
      box.style.display = '';
      if (ta.parentNode) ta.remove();
    };
    var commit = function () {
      var text = ta.value;
      if (text === u.source) { restore(); return; }
      api('api/unit', { method: 'POST', body: JSON.stringify({ docId: state.doc.id, index: u.i, source: text }) })
        .then(function () { u.source = text; restore(); toast('原文已保存'); })
        .catch(function (e) { toast('保存原文失败：' + e.message); });
    };
    ta.addEventListener('blur', commit);
    ta.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') { ta.value = u.source; ta.blur(); }
      if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') ta.blur();
    });
    box.style.display = 'none';
    box.parentNode.insertBefore(ta, box.nextSibling);
    ta.focus();
  }

  function highlight() {
    Array.prototype.forEach.call(document.querySelectorAll('.row'), function (row) {
      row.classList.toggle('current', Number(row.dataset.index) === state.current);
    });
  }

  /** 跳到某一序号：必要时先重排窗口再滚动聚焦 */
  function gotoIndex(index, focus) {
    var pos = state.filtered.findIndex(function (u) { return u.i === index; });
    if (pos < 0) {
      toast('当前筛选下没有这一句');
      return;
    }
    state.current = index;
    // 逐批补渲染到目标行（保留已渲染内容，滚回去不会空白）
    var guard = 0;
    while (state.windowStart <= pos && state.windowStart < state.filtered.length && guard++ < 200) {
      renderWindow(false);
    }
    highlight();
    var row = document.querySelector('.row[data-index="' + index + '"]');
    if (!row) return;
    row.scrollIntoView({ block: 'center', behavior: 'smooth' });
    if (focus) {
      var ta = row.querySelector('textarea.translation');
      if (ta) ta.focus();
    }
  }

  function gotoRelative(index, delta) {
    var pos = state.filtered.findIndex(function (u) { return u.i === index; });
    var next = state.filtered[pos + delta];
    if (!next) return;
    gotoIndex(next.i, true);
  }

  // ---------------- 保存 / AI ----------------

  function saveUnit(index, patch) {
    var body = Object.assign({ docId: state.doc.id, index: index }, patch);
    var remote = state.remote;
    if (remote) body.peer = remote.peerId;
    state.saving++;
    // 直接编辑对端时，保存走服务器的代理接口；回包形状与本地一致（ok/total/done）
    return api(remote ? 'api/remote/unit' : 'api/unit', { method: 'POST', body: JSON.stringify(body) })
      .then(function (res) {
        var before = state.doc.units.find(function (u) { return u.i === index; });
        var wasDone = !!(before && before.done);
        applyLocal(index, patch);
        if (!wasDone && before && before.done) scheduleRefilter();
        if (res && typeof res.done === 'number') {
          state.doc.done = res.done;
          state.doc.total = res.total;
        }
        updateProgress();
        updateDocSummary();
        return res;
      })
      .catch(function (e) { toast('保存失败：' + e.message); })
      .finally(function () { state.saving--; });
  }

  /** 在「未完成 / 收藏」筛选下，句子状态变化后重新整理列表（防抖，避免打字时打断） */
  var refilterTimer = null;
  function scheduleRefilter() {
    if (state.filter === 'all' && !state.query) return;
    clearTimeout(refilterTimer);
    refilterTimer = setTimeout(function () {
      applyFilter();
      $('unitCount').textContent = '显示 ' + state.filtered.length + ' / ' + (state.doc ? state.doc.total : 0);
      renderWindow(true);
    }, 900);
  }

  function updateDocSummary() {
    if (!state.doc) return;
    var item = state.docs.find(function (d) { return d.id === state.doc.id; });
    if (item) {
      item.done = state.doc.done;
      item.total = state.doc.total;
    }
    renderSidebar();
  }

  function applyLocal(index, patch) {
    var unit = state.doc.units.find(function (u) { return u.i === index; });
    if (!unit) return;
    if (typeof patch.translation === 'string') unit.translation = patch.translation;
    if (typeof patch.source === 'string') unit.source = patch.source;
    if (typeof patch.starred === 'boolean') unit.starred = patch.starred;
    if (typeof patch.done === 'boolean') unit.done = patch.done;
    unit.done = unit.done || !!unit.translation.trim();
    var row = document.querySelector('.row[data-index="' + index + '"]');
    if (!row) return;
    var dot = row.querySelector('.row-label .dot');
    if (dot) dot.className = 'dot' + (unit.done ? ' done' : '');
    var star = row.querySelector('.star');
    if (star) {
      star.textContent = unit.starred ? '★' : '☆';
      star.className = 'star' + (unit.starred ? ' on' : '');
    }
  }

  function aiTranslate(index, button) {
    if (button) { button.disabled = true; button.textContent = '翻译中…'; }
    return api('api/ai', { method: 'POST', body: JSON.stringify({ docId: state.doc.id, index: index }) })
      .then(function (res) {
        if (!res.ok) throw new Error(res.error || '翻译失败');
        var unit = state.doc.units.find(function (u) { return u.i === index; });
        unit.translation = res.text;
        unit.done = true;
        if (typeof res.done === 'number') { state.doc.done = res.done; state.doc.total = res.total; }
        updateProgress();
        updateDocSummary();
        var row = document.querySelector('.row[data-index="' + index + '"]');
        if (row) {
          var ta = row.querySelector('textarea.translation');
          if (ta) ta.value = res.text;
          var dot = row.querySelector('.row-label .dot');
          if (dot) dot.className = 'dot done';
        }
        if (res.cost) toast('已翻译（≈' + Number(res.cost).toFixed(4) + ' 元）');
        return res;
      })
      .catch(function (e) { toast('AI 翻译失败：' + e.message); })
      .finally(function () {
        if (button) { button.disabled = false; button.textContent = 'AI 翻译'; }
      });
  }

  function runBatch() {
    if (state.batch.running) { state.batch.stop = true; return; }
    if (!state.info || !state.info.canTranslate) {
      toast('还没有配置 AI 模型，无法批量翻译');
      return;
    }
    var pending = state.doc.units.filter(function (u) { return !u.done; });
    if (!pending.length) { toast('没有待翻译内容'); return; }
    state.batch = { running: true, stop: false, done: 0, total: pending.length };
    updateProgress();
    (function next(i) {
      if (state.batch.stop || i >= pending.length) {
        var stopped = state.batch.stop;
        state.batch.running = false;
        updateProgress();
        toast(stopped ? '已停止批量翻译' : '批量翻译完成');
        return;
      }
      aiTranslate(pending[i].i, null).then(function (res) {
        if (!res || res.ok === false) {
          state.batch.running = false;
          updateProgress();
          return;
        }
        state.batch.done++;
        updateProgress();
        next(i + 1);
      });
    })(0);
  }

// ---------------- 划词查义 ----------------

  var LOOKUP_KEY = 'lt-word-lookup';
  var WORDBOOK_KEY = 'lt-wordbook';
  var LOOKUP_CACHE_MAX = 200;

  var lookupSettings = { enabled: true, definitionLanguage: 'zh', aiFallback: false };
  var lookupCache = new Map();
  var wordbook = {};
  var popupEl = null;
  var popupWord = '';
  var lookupSeq = 0;
  var current = null;

  /** 与安卓 LocalDictionary 的 \p{L} 判定保持一致（老内核退回近似区间） */
  var LETTER_RE = (function () {
    try {
      var re = new RegExp('\\p{L}', 'u');
      if (re.test('a')) return re;
    } catch (e) { /* 不支持 Unicode 属性转义 */ }
    return /[0-9A-Za-z\u00AA-\u02FF\u0370-\u1FFF\u2C00-\uD7FF\uF900-\uFDFF\uFE70-\uFEFF]/;
  })();
  var HAN_RE = /[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff]/;
  var KANA_RE = /[\u3040-\u30ff\uac00-\ud7af]/;
  var LATIN_WORD_RE = /[A-Za-z][A-Za-z'-]*/g;

  function loadLookupSettings() {
    try {
      var raw = localStorage.getItem(LOOKUP_KEY);
      if (!raw) return;
      var obj = JSON.parse(raw);
      if (!obj || typeof obj !== 'object') return;
      if (typeof obj.enabled === 'boolean') lookupSettings.enabled = obj.enabled;
      if (obj.definitionLanguage === 'zh' || obj.definitionLanguage === 'both' || obj.definitionLanguage === 'en') {
        lookupSettings.definitionLanguage = obj.definitionLanguage;
      }
      if (typeof obj.aiFallback === 'boolean') lookupSettings.aiFallback = obj.aiFallback;
    } catch (e) { /* ignore */ }
  }

  function saveLookupSettings() {
    try { localStorage.setItem(LOOKUP_KEY, JSON.stringify(lookupSettings)); } catch (e) { /* ignore */ }
  }

  function loadWordbook() {
    try {
      var raw = localStorage.getItem(WORDBOOK_KEY);
      var obj = raw ? JSON.parse(raw) : null;
      wordbook = (obj && typeof obj === 'object') ? obj : {};
    } catch (e) { wordbook = {}; }
  }

  function updateWordbookCount() {
    var el = $('wordbookCount');
    if (el) el.textContent = Object.keys(wordbook).length + ' 条';
  }

  function saveWordbook() {
    try { localStorage.setItem(WORDBOOK_KEY, JSON.stringify(wordbook)); } catch (e) { /* ignore */ }
    updateWordbookCount();
  }

  /** 取词清洗：与安卓 LocalDictionary.lookup / 服务端规范化保持一致 */
  function normalizeWord(raw) {
    if (raw === null || raw === undefined) return '';
    var text = String(raw).replace(/^\s+|\s+$/g, '');
    var plain = function (ch) { return !LETTER_RE.test(ch) && ch !== '-' && ch !== "'"; };
    var start = 0, end = text.length;
    while (start < end && plain(text.charAt(start))) start++;
    while (end > start && plain(text.charAt(end - 1))) end--;
    return text.slice(start, end).toLowerCase();
  }

  /** 在文本 offset 处取词：拉丁词按词边界，假名 / 谚文取连续串，汉字取单字 */
  function wordSpanAt(text, offset) {
    if (!text) return null;
    var i = offset;
    if (i >= text.length) i = text.length - 1;
    if (i < 0) return null;
    var ch = text.charAt(i);
    if (HAN_RE.test(ch)) return { start: i, end: i + 1 };
    if (KANA_RE.test(ch)) {
      var s = i, e = i + 1;
      while (s > 0 && KANA_RE.test(text.charAt(s - 1))) s--;
      while (e < text.length && KANA_RE.test(text.charAt(e))) e++;
      return { start: s, end: e };
    }
    LATIN_WORD_RE.lastIndex = 0;
    var m;
    while ((m = LATIN_WORD_RE.exec(text)) !== null) {
      if (m[0] === '') { LATIN_WORD_RE.lastIndex++; continue; }
      if (i >= m.index && i < m.index + m[0].length) {
        var word = m[0].replace(/[-']+$/, '');
        if (!word) return null;
        return { start: m.index, end: m.index + word.length };
      }
      if (m.index > i) break;
    }
    return null;
  }

  /** 光标定位：Chromium/WebKit 用 caretRangeFromPoint，Firefox 用 caretPositionFromPoint */
  function caretRangeAtPoint(x, y) {
    if (document.caretRangeFromPoint) {
      try {
        var r = document.caretRangeFromPoint(x, y);
        if (r) return r;
      } catch (e) { /* ignore */ }
    }
    if (document.caretPositionFromPoint) {
      try {
        var pos = document.caretPositionFromPoint(x, y);
        if (pos && pos.offsetNode) {
          var range = document.createRange();
          var max = pos.offsetNode.nodeType === 3 ? pos.offsetNode.data.length : 0;
          range.setStart(pos.offsetNode, Math.min(pos.offset, max));
          range.collapse(true);
          return range;
        }
      } catch (e) { /* ignore */ }
    }
    return null;
  }

  /** 镜像层要复刻的排版属性（textarea 与普通文本的换行 / 字体必须一致） */
  var MIRROR_PROPS = ['fontFamily', 'fontSize', 'fontWeight', 'fontStyle', 'fontStretch', 'fontVariant',
    'letterSpacing', 'lineHeight', 'textTransform', 'textIndent', 'textAlign', 'wordSpacing', 'direction',
    'paddingTop', 'paddingRight', 'paddingBottom', 'paddingLeft',
    'borderTopWidth', 'borderRightWidth', 'borderBottomWidth', 'borderLeftWidth',
    'boxSizing', 'whiteSpace', 'wordBreak', 'overflowWrap', 'tabSize'];

  /**
   * Chromium 的 caretRangeFromPoint 命不中 textarea / input 内部（会落到最近的普通文本上），
   * 因此点击译文时临时铺一层同排版的镜像层：把镜像放在 textarea 正上方，
   * 用 caretRangeFromPoint 取偏移与词矩形，取完立刻移除（不给真实事件留下遮挡）。
   */
  function makeTextareaMirror(ta) {
    var rect = ta.getBoundingClientRect();
    if (!rect.width || !rect.height) return null;
    var cs = window.getComputedStyle(ta);
    var mirror = document.createElement('div');
    mirror.setAttribute('aria-hidden', 'true');
    for (var i = 0; i < MIRROR_PROPS.length; i++) {
      var prop = MIRROR_PROPS[i];
      var value = cs[prop];
      if (!value) continue;
      try { mirror.style[prop] = value; } catch (err) { /* ignore */ }
    }
    mirror.style.position = 'fixed';
    mirror.style.left = rect.left + 'px';
    mirror.style.top = rect.top + 'px';
    mirror.style.width = rect.width + 'px';
    mirror.style.height = rect.height + 'px';
    mirror.style.margin = '0';
    mirror.style.color = 'transparent';
    mirror.style.background = 'transparent';
    mirror.style.overflow = 'hidden';
    mirror.style.zIndex = '70';
    mirror.textContent = ta.value;
    document.body.appendChild(mirror);
    try { mirror.scrollTop = ta.scrollTop; } catch (err) { /* ignore */ }
    return mirror;
  }

  /** 在镜像层上取词：返回 { word, rect }（镜像已移除，rect 为视口坐标） */
  function mirrorWordAt(ta, x, y) {
    var mirror = makeTextareaMirror(ta);
    if (!mirror) return null;
    var out = null;
    try {
      var mr = caretRangeAtPoint(x, y);
      var node = mr ? mr.startContainer : null;
      if (node && node.nodeType === 3 && mirror.contains(node)) {
        var text = mirror.textContent;
        var span = wordSpanAt(text, mr.startOffset);
        if (span) {
          var word = normalizeWord(text.slice(span.start, span.end));
          if (word) {
            var rect = null;
            try {
              var r = document.createRange();
              r.setStart(node, span.start);
              r.setEnd(node, span.end);
              var box = r.getBoundingClientRect();
              if (box && (box.width || box.height)) rect = box;
            } catch (err) { rect = null; }
            out = { word: word, rect: rect };
          }
        }
      }
    } catch (err) { out = null; }
    if (mirror.parentNode) mirror.remove();
    return out;
  }

  /** 取词区域：原文 .source / 译文 textarea.translation，其它地方（按钮、输入框、弹窗）不响应 */
  function lookupHostOf(el) {
    while (el && el.nodeType === 1) {
      if (el.classList) {
        if (el.classList.contains('source')) return el;
        if (el.tagName === 'TEXTAREA' && el.classList.contains('translation')) return el;
      }
      el = el.parentNode;
    }
    return null;
  }

  function textOfHost(host) {
    return host.tagName === 'TEXTAREA' ? host.value : host.textContent;
  }

  /** 已有选区（长按 / 双击 / 拖选）：textarea 走 selectionStart，普通文本走 window.getSelection */
  function selectedTextOf(host) {
    if (host.tagName === 'TEXTAREA') {
      var s = host.selectionStart, e = host.selectionEnd;
      if (typeof s === 'number' && typeof e === 'number' && e > s) return host.value.slice(s, e);
      return '';
    }
    var sel = window.getSelection ? window.getSelection() : null;
    if (!sel || sel.isCollapsed || !sel.rangeCount) return '';
    var range = sel.getRangeAt(0);
    if (!host.contains(range.commonAncestorContainer)) return '';
    return String(range);
  }

  function pointRect(x, y) {
    return { left: x - 30, right: x + 30, top: y - 9, bottom: y + 9, width: 60, height: 18 };
  }

  function onEditorClick(e) {
    if (!lookupSettings.enabled) return;
    if (typeof e.button === 'number' && e.button !== 0) return;
    var host = lookupHostOf(e.target);
    if (!host) return;

    var selected = selectedTextOf(host);
    if (selected) {
      var selWord = normalizeWord(selected);
      if (selWord) {
        var selRect = null;
        var sel = window.getSelection ? window.getSelection() : null;
        if (host.tagName !== 'TEXTAREA' && sel && sel.rangeCount) {
          var box0 = sel.getRangeAt(0).getBoundingClientRect();
          if (box0 && (box0.width || box0.height)) selRect = box0;
        }
        if (!selRect) selRect = pointRect(e.clientX, e.clientY);
        showLookup(selWord, selRect, null);
        return;
      }
    }

    // 译文是 textarea：caretRangeFromPoint 命不中，走镜像层；失败再用点击后的光标位置兜底
    if (host.tagName === 'TEXTAREA') {
      var hit = mirrorWordAt(host, e.clientX, e.clientY);
      if (hit) {
        showLookup(hit.word, hit.rect || pointRect(e.clientX, e.clientY), null, true);
        return;
      }
      var caret = host.selectionStart;
      if (typeof caret !== 'number') return;
      var fallbackSpan = wordSpanAt(textOfHost(host), caret);
      if (!fallbackSpan) return;
      var fallbackWord = normalizeWord(textOfHost(host).slice(fallbackSpan.start, fallbackSpan.end));
      if (!fallbackWord) return;
      showLookup(fallbackWord, pointRect(e.clientX, e.clientY), null, true);
      return;
    }

    var range = caretRangeAtPoint(e.clientX, e.clientY);
    var node = range ? range.startContainer : null;
    if (!node || node.nodeType !== 3) return;
    var root = node.getRootNode ? node.getRootNode() : null;
    var inShadow = !!(root && root.host && root.host === host);
    if (!inShadow && !host.contains(node)) return;

    var text = textOfHost(host);
    var span = wordSpanAt(text, range.startOffset);
    if (!span) return;
    var word = normalizeWord(text.slice(span.start, span.end));
    if (!word) return;

    var rect = null;
    var liveRange = null;
    try {
      if (node.data.slice(span.start, span.end) === text.slice(span.start, span.end)) {
        var r2 = document.createRange();
        r2.setStart(node, span.start);
        r2.setEnd(node, span.end);
        var box = r2.getBoundingClientRect();
        if (box && (box.width || box.height)) { rect = box; liveRange = r2; }
      }
    } catch (err) { rect = null; liveRange = null; }
    if (!rect) rect = pointRect(e.clientX, e.clientY);
    showLookup(word, rect, liveRange);
  }

  function lookupCacheGet(key) {
    if (!lookupCache.has(key)) return null;
    var value = lookupCache.get(key);
    lookupCache.delete(key);
    lookupCache.set(key, value);
    return value;
  }

  function lookupCacheSet(key, value) {
    if (lookupCache.has(key)) lookupCache.delete(key);
    lookupCache.set(key, value);
    while (lookupCache.size > LOOKUP_CACHE_MAX) {
      lookupCache.delete(lookupCache.keys().next().value);
    }
  }

  function showLookup(word, rect, liveRange, stale) {
    if (!popupEl) return;
    if (popupWord && popupWord === word && !popupEl.hidden) { closeLookup(); return; }
    popupEl.hidden = false;
    var key = word.toLowerCase();
    var local = wordbook[key] || null;
    var lang = lookupSettings.definitionLanguage;
    current = { word: word, rect: rect, liveRange: liveRange || null, stale: !!stale, local: local, result: null, loading: false, error: '' };
    popupWord = word;
    var cached = local ? null : lookupCacheGet(key);
    var cacheStale = !!(cached && !cached.found && !cached.aiTried && lookupSettings.aiFallback);
    if (cached && cached.lang === lang && !cacheStale) {
      current.result = cached;
      renderLookup();
      positionLookup();
      return;
    }
    if (local) { renderLookup(); positionLookup(); return; }
    requestLookup(false);
  }

  function requestLookup(forceAi) {
    if (!current) return;
    var word = current.word;
    var key = word.toLowerCase();
    var useAi = !!(forceAi || lookupSettings.aiFallback);
    var lang = lookupSettings.definitionLanguage;
    var mySeq = ++lookupSeq;
    current.loading = true;
    current.error = '';
    if (!current.local) current.result = null;
    renderLookup();
    positionLookup();
    api('api/lookup?word=' + encodeURIComponent(word) +
        '&lang=' + encodeURIComponent(lang) +
        '&ai=' + (useAi ? '1' : '0'))
      .then(function (res) {
        if (mySeq !== lookupSeq || !current) return;
        if (!res || typeof res !== 'object') throw new Error('返回格式不正确');
        if (normalizeWord(res.word || word) !== key) return;   // 回包不是这次查的词 → 丢弃
        var result = {
          found: !!res.found,
          matched: res.matched || null,
          via: res.via || null,
          phonetic: res.phonetic || '',
          meaning: res.meaning || '',
          source: res.source || 'none',
          cost: typeof res.cost === 'number' ? res.cost : null,
          lang: lang,
          aiTried: useAi
        };
        current.loading = false;
        current.result = result;
        if (res.ok === false) current.error = res.error || '查词失败';
        lookupCacheSet(key, result);
        renderLookup();
        positionLookup();
      })
      .catch(function (e) {
        if (mySeq !== lookupSeq || !current) return;
        current.loading = false;
        var msg = (e && e.message) ? String(e.message) : '查词失败';
        if (msg.length > 80) msg = msg.slice(0, 80) + '…';
        current.error = msg;
        renderLookup();
        positionLookup();
      });
  }

  function currentMeaning() {
    if (!current) return '';
    if (current.local) return current.local.meaning || '';
    var r = current.result;
    return (r && r.found) ? (r.meaning || '') : '';
  }

  function currentPhonetic() {
    if (!current) return '';
    if (current.local && current.local.phonetic) return current.local.phonetic;
    return (current.result && current.result.phonetic) || '';
  }

  function lookupPlainText() {
    if (!current) return '';
    var head = current.word;
    var phonetic = currentPhonetic();
    if (phonetic) head += ' ' + phonetic;
    var meaning = currentMeaning();
    return meaning ? head + '\n' + meaning : head;
  }

  /** 拆词性：'n. 跑步' → {pos:'n.', def:'跑步'}，无词性时占满整行 */
  function parseSenses(meaning) {
    if (!meaning) return [];
    return String(meaning).split(/[；;\n]+/)
      .map(function (s) { return s.replace(/^\s+|\s+$/g, ''); })
      .filter(function (s) { return !!s; })
      .map(function (line) {
        var m = line.match(/^([A-Za-z]{1,6}\.\s*(?:&\s*[A-Za-z]{1,6}\.)?)\s*(.*)$/);
        if (m && m[2]) return { pos: m[1].replace(/\s+/g, ' '), def: m[2] };
        return { pos: '', def: line };
      });
  }

  function makeLink(label, handler) {
    var btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'wp-link';
    btn.textContent = label;
    btn.addEventListener('click', handler);
    return btn;
  }

  /**
   * 后端 AI 兜底命中：命中时同时返回 via:'ai' 与 source:'ai'。
   * 契约里 via 的取值是 direct|lemma|variant|ai，为了不让后续扩展把 AI 命中当成未知来源，
   * 这里两个字段都认——任一为 'ai' 即按 AI 来源展示，绝不落进「没查到 / 未知来源」分支。
   */
  function isAiHit(r) {
    return !!r && (r.source === 'ai' || r.via === 'ai');
  }

  function renderLookup() {
    if (!popupEl || !current) return;
    var local = current.local;
    var result = current.result;
    var meaning = currentMeaning();
    var enMode = lookupSettings.definitionLanguage === 'en';

    $('wpWord').textContent = current.word;
    var ph = $('wpPhonetic');
    var phonetic = currentPhonetic();
    ph.textContent = phonetic || '';
    ph.hidden = !phonetic;

    var badge = $('wpBadge');
    var badgeLabel = '';
    if (local) badgeLabel = '我的词库';
    else if (result && result.found) {
      badgeLabel = isAiHit(result) ? 'AI'
        : (result.source === 'import' ? '导入词库' : '本地词库');
    }
    badge.textContent = badgeLabel;
    badge.hidden = !badgeLabel;

    var note = $('wpNote');
    note.textContent = '';
    note.hidden = true;

    var status = $('wpStatus');
    status.className = 'wp-status';
    status.textContent = '';
    status.hidden = true;

    var senses = $('wpSenses');
    senses.innerHTML = '';
    senses.hidden = true;

    if (current.loading) {
      status.hidden = false;
      var spin = document.createElement('span');
      spin.className = 'wp-spinner';
      status.appendChild(spin);
      status.appendChild(document.createTextNode('正在查询…'));
    } else if (current.error) {
      status.hidden = false;
      status.className = 'wp-status error';
      status.appendChild(document.createTextNode('查词失败：' + current.error));
      status.appendChild(makeLink('重试', function () { requestLookup(false); }));
    } else if (!local && (!result || !result.found)) {
      status.hidden = false;
      status.appendChild(document.createTextNode('没有查到释义'));
      if (!result || !result.aiTried) {
        status.appendChild(makeLink('用 AI 补全', function () { requestLookup(true); }));
      }
    } else if (enMode && !isAiHit(result)) {
      // 只有「本地词库给不出英文释义」时才提示转 AI；AI 已经答过就不再提示
      note.hidden = false;
      note.textContent = '本地词库无英文释义';
      note.appendChild(document.createTextNode(' '));
      note.appendChild(makeLink('用 AI 释义', function () { requestLookup(true); }));
    }

    if (meaning) {
      var list = parseSenses(meaning);
      if (list.length) {
        senses.hidden = false;
        list.forEach(function (s) {
          var row = document.createElement('div');
          row.className = 'wp-sense';
          var pos = document.createElement('span');
          pos.className = 'wp-pos';
          pos.textContent = s.pos || '';
          var def = document.createElement('span');
          def.className = 'wp-def' + (enMode ? ' dim' : '');
          def.textContent = s.def;
          row.appendChild(pos);
          row.appendChild(def);
          senses.appendChild(row);
        });
      }
    }

    if (!current.loading && !current.error && result && result.found && result.via === 'lemma' &&
        result.matched && result.matched !== current.word) {
      status.hidden = false;
      status.appendChild(document.createTextNode('词形还原：' + current.word + ' → ' + result.matched));
    }

    $('wpCopy').disabled = !meaning && !current.word;
    var save = $('wpSave');
    save.textContent = local ? '移出我的词库' : '加入我的词库';
    save.disabled = !local && !meaning;
  }

  function anchorRect() {
    if (!current) return null;
    if (current.liveRange) {
      try {
        var box = current.liveRange.getBoundingClientRect();
        if (box && (box.width || box.height)) return box;
      } catch (e) { /* ignore */ }
      return null;
    }
    return current.rect || null;
  }

  /** 词上方居中；上方空间不足翻到下方；距视口边缘 12px；避开底部操作坞 */
  function positionLookup() {
    if (!popupEl || popupEl.hidden) return;
    var rect = anchorRect();
    var w = popupEl.offsetWidth;
    var h = popupEl.offsetHeight;
    if (!w || !h) return;
    var vw = window.innerWidth;
    var vh = window.innerHeight;
    var M = 12;
    var cx = rect ? (rect.left + rect.right) / 2 : vw / 2;
    var left = cx - w / 2;
    var maxLeft = vw - w - M;
    left = maxLeft <= M ? M : Math.min(Math.max(left, M), maxLeft);
    var bottomLimit = vh - M;
    var dock = $('dock');
    if (dock && !dock.hidden) {
      var dockRect = dock.getBoundingClientRect();
      if (dockRect.top > M) bottomLimit = Math.min(bottomLimit, dockRect.top - 8);
    }
    if (bottomLimit - h < M) bottomLimit = vh - M;
    var top = rect ? rect.top - h - 8 : M;
    if (top < M) top = rect ? rect.bottom + 8 : M;
    if (top + h > bottomLimit) top = bottomLimit - h;
    if (top < M) top = M;
    popupEl.style.left = Math.round(left) + 'px';
    popupEl.style.top = Math.round(top) + 'px';
  }

  function followLookup() {
    if (!popupEl || popupEl.hidden || !current) return;
    if (current.stale) { closeLookup(); return; }
    var rect = anchorRect();
    if (!rect) { closeLookup(); return; }
    if (rect.bottom < 0 || rect.top > window.innerHeight) { closeLookup(); return; }
    positionLookup();
  }

  function closeLookup() {
    if (popupEl) popupEl.hidden = true;
    popupWord = '';
    current = null;
    lookupSeq++;
  }

  function copyText(text) {
    var fallback = function () {
      try {
        var ta = document.createElement('textarea');
        ta.value = text;
        ta.setAttribute('readonly', 'readonly');
        ta.style.position = 'fixed';
        ta.style.top = '-1000px';
        ta.style.left = '-1000px';
        document.body.appendChild(ta);
        ta.select();
        var ok = document.execCommand('copy');
        if (ta.parentNode) ta.remove();
        toast(ok ? '已复制' : '复制失败，请手动选择');
      } catch (e) { toast('复制失败：' + e.message); }
    };
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(function () { toast('已复制'); }, fallback);
    } else {
      fallback();
    }
  }

  function saveToWordbook() {
    if (!current) return;
    var key = current.word.toLowerCase();
    if (wordbook[key]) {
      delete wordbook[key];
      saveWordbook();
      toast('已移出我的词库');
    } else {
      var meaning = currentMeaning();
      if (!meaning) return;
      wordbook[key] = {
        word: current.word,
        phonetic: currentPhonetic(),
        meaning: meaning,
        updatedAt: Date.now()
      };
      saveWordbook();
      toast('已加入我的词库');
    }
    renderLookup();
    positionLookup();
  }

  function bindLookup() {
    popupEl = $('wordPopup');
    if (!popupEl) return;
    editor.addEventListener('click', onEditorClick);
    document.addEventListener('mousedown', function (e) {
      if (!popupEl.hidden && !popupEl.contains(e.target) && !lookupHostOf(e.target)) closeLookup();
    }, true);
    document.addEventListener('scroll', followLookup, true);
    window.addEventListener('resize', function () {
      if (popupEl && !popupEl.hidden) positionLookup();
    });
    $('wpClose').addEventListener('click', closeLookup);
    $('wpCopy').addEventListener('click', function () { copyText(lookupPlainText()); });
    $('wpSave').addEventListener('click', saveToWordbook);
    $('wpRetry').addEventListener('click', function () { requestLookup(true); });

    var optEnabled = $('optLookupEnabled');
    var optLang = $('optDefinitionLang');
    var optAi = $('optLookupAi');
    optEnabled.checked = lookupSettings.enabled;
    optLang.value = lookupSettings.definitionLanguage;
    optAi.checked = lookupSettings.aiFallback;
    optEnabled.addEventListener('change', function () {
      lookupSettings.enabled = optEnabled.checked;
      saveLookupSettings();
      if (!lookupSettings.enabled) closeLookup();
    });
    optLang.addEventListener('change', function () {
      lookupSettings.definitionLanguage = optLang.value;
      saveLookupSettings();
      if (current && !popupEl.hidden) requestLookup(false);
    });
    optAi.addEventListener('change', function () {
      lookupSettings.aiFallback = optAi.checked;
      saveLookupSettings();
    });
    updateWordbookCount();
  }


  // ---------------- 设备（v1.9.0） ----------------
  //
  // 界面只跟「本机服务器」说话：发现设备、列对端文件、同步到本地、直接写对端，
  // 全部由服务器代理（浏览器拿不到 UDP 广播，也不该被迫直连对端）。
  // 安卓内置网页台用的是同一套界面，所以改这里等于三端同时受益。

  function loadPeers() {
    return api('api/peers').then(function (res) {
      state.peers = (res && res.peers) || [];
      renderPeerList();
      return state.peers;
    }).catch(function () { /* 老服务端没有这个接口，静默忽略 */ });
  }

  function renderPeerList() {
    var list = $('peerList');
    if (!list) return;
    list.innerHTML = '';
    if (!state.peers.length) {
      list.innerHTML = '<div class="muted peer-empty">没发现其它设备</div>';
      return;
    }
    state.peers.forEach(function (p) {
      var el = document.createElement('div');
      el.className = 'peer-item' + (p.online ? ' online' : '');
      el.innerHTML = '<span class="pdot"></span><span class="pname"></span><span class="ptag"></span>';
      el.querySelector('.pname').textContent = p.name;
      el.querySelector('.ptag').textContent = p.manual ? '远程' : (p.platform || '设备');
      el.title = (p.url || '') + (p.manual ? '（手填的远程服务器）' : '（局域网发现的设备）');
      el.addEventListener('click', function () { closeSidebar(); openPeer(p); });
      list.appendChild(el);
    });
  }

  function openPeer(p) {
    $('peerTitle').textContent = p.name;
    $('peerMask').hidden = false;
    var box = $('peerDocs');
    box.innerHTML = '<div class="muted peer-empty">正在读取文件列表…</div>';
    api('api/remote/docs?peer=' + encodeURIComponent(p.id)).then(function (res) {
      box.innerHTML = '';
      if (!res || res.ok === false) {
        box.innerHTML = '<div class="peer-hint">连不上：' + esc((res && res.error) || '未知错误') + '</div>';
        return;
      }
      var docs = res.docs || [];
      if (!docs.length) {
        box.innerHTML = '<div class="peer-hint">这台设备还没有文档</div>';
        return;
      }
      docs.forEach(function (d) { box.appendChild(renderPeerDoc(p, d)); });
      var hint = document.createElement('div');
      hint.className = 'peer-hint';
      hint.textContent = '「同步到本地」= 复制一份到本机，对端不变；「直接编辑」= 改的就是对端那份。';
      box.appendChild(hint);
    });
  }

  function renderPeerDoc(p, d) {
    var row = document.createElement('div');
    row.className = 'peer-doc';
    row.innerHTML = '<span class="dname"></span><span class="dmeta"></span><span class="dacts"></span>';
    row.querySelector('.dname').textContent = d.name;
    row.querySelector('.dmeta').textContent = (d.unitMode === 'SENTENCE' ? '逐句' : '逐行') +
      ' · ' + d.done + '/' + d.total;
    var acts = row.querySelector('.dacts');

    var pull = document.createElement('button');
    pull.className = 'btn ghost sm';
    pull.textContent = '同步到本地';
    pull.addEventListener('click', function () { pullPeerDoc(p, d, pull); });

    var edit = document.createElement('button');
    edit.className = 'btn sm';
    edit.textContent = '直接编辑';
    edit.addEventListener('click', function () { openRemoteDoc(p, d.id); });

    acts.appendChild(pull);
    acts.appendChild(edit);
    return row;
  }

  function pullPeerDoc(p, d, btn) {
    if (btn) { btn.disabled = true; btn.textContent = '同步中…'; }
    api('api/remote/pull', { method: 'POST', body: JSON.stringify({ peer: p.id, docId: d.id }) })
      .then(function (res) {
        if (!res || res.ok === false) throw new Error((res && res.error) || '同步失败');
        toast('已同步到本地：' + res.name);
        return api('api/docs');
      })
      .then(function (res) {
        if (res && res.docs) state.docs = res.docs;
        renderSidebar();
      })
      .catch(function (e) { toast('同步失败：' + e.message); })
      .finally(function () {
        if (btn) { btn.disabled = false; btn.textContent = '同步到本地'; }
      });
  }

  /** 直接编辑对端文档：读回来用同一套编辑器渲染，保存走 /api/remote/unit。 */
  function openRemoteDoc(p, docId) {
    api('api/remote/doc?peer=' + encodeURIComponent(p.id) + '&id=' + encodeURIComponent(docId))
      .then(function (doc) {
        if (!doc || doc.error) throw new Error((doc && doc.error) || '读取失败');
        state.remote = { peerId: p.id, peerName: p.name, docId: doc.id, name: doc.name };
        state.doc = doc;
        state.current = -1;
        $('peerMask').hidden = true;
        renderSidebar();
        renderDoc();
        $('scrollArea').scrollTop = 0;
        toast('正在直接编辑「' + p.name + '」上的文档');
      })
      .catch(function (e) { toast('打开失败：' + e.message); });
  }

  function exitRemote() {
    state.remote = null;
    state.doc = null;
    renderPeerBar();
    api('api/docs').then(function (res) {
      state.docs = (res && res.docs) || [];
      renderSidebar();
      if (state.docs.length) {
        openDoc(state.docs[0].id);
      } else {
        $('emptyState').hidden = false;
        $('subbar').hidden = true;
        $('dock').hidden = true;
      }
    }).catch(function (e) { toast('回到本机失败：' + e.message); });
  }

  function renderPeerBar() {
    var bar = $('remoteBar');
    if (!bar) return;
    var on = !!state.remote;
    bar.hidden = !on;
    if (on) {
      $('remoteBarText').textContent = '正在直接编辑「' + state.remote.peerName + '」的：' +
        (state.remote.name || '未命名文档');
    }
    // 远程文档不支持切换切分方式 / 导出 / 批量 AI（这些接口没有代理）
    $('btnMode').disabled = on;
    $('btnExport').disabled = on;
    $('btnBatch').disabled = on;
  }

  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
    });
  }

  /** 添加远程服务器：填地址（可选令牌/名字），先探通再入册。 */
  function openPeerAdd() {
    $('peerTitle').textContent = '添加远程服务器';
    var box = $('peerDocs');
    box.innerHTML = '';
    var form = document.createElement('div');
    form.className = 'peer-add-form';
    form.innerHTML =
      '<div class="peer-hint">填一台跑着 LineTrans 服务端的地址：局域网里的手机 / 电脑，或者公网服务器。</div>' +
      '<input id="peerUrl" placeholder="http://192.168.1.10:8787">' +
      '<input id="peerToken" placeholder="访问令牌（对端设了 token 才需要）">' +
      '<input id="peerName" placeholder="显示名（可留空，默认用对端自己的名字）">' +
      '<button class="btn" id="btnPeerSave" type="button">连接并保存</button>' +
      '<div class="peer-hint" id="peerAddHint"></div>';
    box.appendChild(form);
    $('peerMask').hidden = false;

    $('btnPeerSave').addEventListener('click', function () {
      var url = $('peerUrl').value.trim();
      if (!url) { toast('先填地址'); return; }
      $('peerAddHint').textContent = '正在连接…';
      api('api/peers', {
        method: 'POST',
        body: JSON.stringify({
          url: url,
          token: $('peerToken').value.trim(),
          name: $('peerName').value.trim()
        })
      }).then(function (res) {
        if (!res || res.ok === false) throw new Error((res && res.error) || '连接失败');
        toast('已连接：' + res.peer.name);
        return loadPeers();
      }).then(function () {
        $('peerMask').hidden = true;
      }).catch(function (e) {
        $('peerAddHint').textContent = '失败：' + e.message;
      });
    });

    setTimeout(function () { try { $('peerUrl').focus(); } catch (e) { /* ignore */ } }, 30);
  }

  // ---------------- 设置 ----------------

  function openSettings() {
    closeLookup();
    updateWordbookCount();
    $('settingsMask').hidden = false;
    $('infoMode').textContent = state.info ? (state.info.mode || '—') : '—';
    $('infoVersion').textContent = state.info ? state.info.version : '—';
    $('infoModel').textContent = state.info && state.info.provider ? state.info.provider : '未配置';
    $('infoLang').textContent = state.info ? state.info.targetLang : '—';
    $('infoDocs').textContent = state.docs.length + ' 篇';
    $('infoToken').textContent = TOKEN ? '已启用' : '未设置';
  }

  function closeSettings() { $('settingsMask').hidden = true; }

  function openSidebar() {
    $('sidebar').classList.add('open');
    $('scrim').classList.add('show');
  }

  function closeSidebar() {
    $('sidebar').classList.remove('open');
    $('scrim').classList.remove('show');
  }

  function bind() {
    editor = $('editor');
    $('docSearch').addEventListener('input', function (e) { state.docQuery = e.target.value; renderSidebar(); });
    $('unitSearch').addEventListener('input', function (e) {
      state.query = e.target.value;
      applyFilter();
      $('unitCount').textContent = '显示 ' + state.filtered.length + ' / ' + (state.doc ? state.doc.total : 0);
      renderWindow(true);
    });
    Array.prototype.forEach.call(document.querySelectorAll('.chip'), function (chip) {
      chip.addEventListener('click', function () {
        Array.prototype.forEach.call(document.querySelectorAll('.chip'), function (c) { c.classList.remove('active'); });
        chip.classList.add('active');
        state.filter = chip.dataset.filter;
        applyFilter();
        $('unitCount').textContent = '显示 ' + state.filtered.length + ' / ' + (state.doc ? state.doc.total : 0);
        renderWindow(true);
      });
    });
    $('btnBatch').addEventListener('click', runBatch);
    $('btnExport').addEventListener('click', function () {
      if (!state.doc) return;
      window.open('api/export?id=' + encodeURIComponent(state.doc.id) +
        '&format=txt_bilingual&token=' + encodeURIComponent(TOKEN), '_blank');
    });
    $('btnMode').addEventListener('click', function () {
      if (!state.doc) return;
      var next = state.doc.unitMode === 'SENTENCE' ? 'LINE' : 'SENTENCE';
      api('api/doc', { method: 'POST', body: JSON.stringify({ docId: state.doc.id, unitMode: next }) })
        .then(function () { openDoc(state.doc.id); toast('已切换为' + (next === 'SENTENCE' ? '逐句' : '逐行')); })
        .catch(function (e) { toast('切换失败：' + e.message); });
    });
    $('jumpInput').addEventListener('keydown', function (e) {
      if (e.key !== 'Enter') return;
      var n = Number($('jumpInput').value);
      if (!state.doc || !n) return;
      var target = state.doc.units[Math.min(Math.max(1, Math.round(n)), state.doc.total) - 1];
      if (target) gotoIndex(target.i, true);
      $('jumpInput').value = '';
    });
    $('btnSidebar').addEventListener('click', openSidebar);
    $('scrim').addEventListener('click', closeSidebar);

    $('btnSettings').addEventListener('click', openSettings);
    $('btnCloseSettings').addEventListener('click', closeSettings);
    $('settingsMask').addEventListener('click', function (e) {
      if (e.target === $('settingsMask')) closeSettings();
    });
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') { closeLookup(); closeSettings(); closeSidebar(); }
    });
    Array.prototype.forEach.call(document.querySelectorAll('#themeCards .theme-card'), function (card) {
      card.addEventListener('click', function () { applyTheme(card.dataset.theme); });
    });
    Array.prototype.forEach.call(document.querySelectorAll('#settingsNav button'), function (btn) {
      btn.addEventListener('click', function () {
        Array.prototype.forEach.call(document.querySelectorAll('#settingsNav button'), function (b) { b.classList.remove('active'); });
        btn.classList.add('active');
        ['general', 'conn', 'about'].forEach(function (name) {
          $('pane-' + name).hidden = name !== btn.dataset.pane;
        });
      });
    });
    // 设备（v1.9.0）
    $('btnAddPeer').addEventListener('click', openPeerAdd);
    $('btnClosePeer').addEventListener('click', function () { $('peerMask').hidden = true; });
    $('peerMask').addEventListener('click', function (e) { if (e.target === $('peerMask')) $('peerMask').hidden = true; });
    $('btnRemoteExit').addEventListener('click', exitRemote);

    bindLookup();
    window.addEventListener('beforeunload', function (e) {
      if (state.saving > 0) { e.preventDefault(); e.returnValue = ''; }
    });
  }

  // ---------------- 启动 ----------------

  initTheme();
  loadLookupSettings();
  loadWordbook();
  bind();

  api('api/info').then(function (info) {
    state.info = info;
    $('serverInfo').textContent = (info.mode || '网页翻译台') + ' · v' + info.version;
    if (info.targetLang) $('dockHint').textContent = '目标语言 ' + info.targetLang + ' · 输入即自动保存';
    setConn('已连接', true);
    return api('api/docs');
  }).then(function (res) {
    state.docs = res.docs || [];
    renderSidebar();
    if (state.docs.length && !(DEEP_PEER && DEEP_DOC)) openDoc(state.docs[0].id);
    // 设备发现：先是立刻一次，之后每 10 秒刷新（服务器那边每 3 秒广播一次）
    loadPeers().then(function (list) {
      if (!DEEP_PEER || !DEEP_DOC) return;
      var target = (list || []).filter(function (p) { return p.id === DEEP_PEER; })[0];
      if (target) openRemoteDoc(target, DEEP_DOC);
      else toast('找不到设备：' + DEEP_PEER);
    });
    setInterval(loadPeers, 10000);
  }).catch(function (e) {
    setConn('连接失败', false);
    toast('无法连接服务：' + e.message + '（若手机端设置了访问令牌，请在网址后加 ?token=你的令牌）');
  });
})();
