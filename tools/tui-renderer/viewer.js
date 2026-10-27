async function render() {
  const response = await fetch('/manifest.json');
  if (!response.ok) throw new Error('尚未生成终端样稿，请先运行 render-preview.ps1。');
  const manifest = await response.json();
  const frames = Array.isArray(manifest) ? manifest : manifest.snapshots;
  const selected = new URLSearchParams(location.search).get('state') || 'main';
  const frame = frames.find(item => item.state === selected) || frames[0];
  if (!frame) throw new Error('没有可显示的终端样稿。');

  const nav = document.getElementById('states');
  const labels = { main: '主界面', rename: '改名对话框', preview: '修改预览', result: '执行结果', narrow: '窄窗口', 'narrow-rename': '窄屏改名' };
  for (const item of frames) {
    const link = document.createElement('a');
    link.href = `/?state=${encodeURIComponent(item.state)}`;
    link.textContent = labels[item.state] || item.title;
    if (item.state === frame.state) link.setAttribute('aria-current', 'page');
    nav.appendChild(link);
  }

  const terminal = new Terminal({
    cols: frame.cols,
    rows: frame.rows,
    fontFamily: '"Cascadia Mono", Consolas, "Microsoft YaHei UI", monospace',
    fontSize: 16,
    lineHeight: 1.2,
    letterSpacing: 0,
    scrollback: 0,
    cursorBlink: false,
    disableStdin: true,
    convertEol: true,
    overviewRulerWidth: 0,
    theme: {
      background: '#ffffff', foreground: '#20252b', cursor: '#0067c0',
      black: '#20252b', red: '#a82222', green: '#187245', yellow: '#936400',
      blue: '#0067c0', magenta: '#704797', cyan: '#087b90', white: '#dce2e8',
      brightBlack: '#667383', brightRed: '#c42b1c', brightGreen: '#198754',
      brightYellow: '#b98200', brightBlue: '#3b78ff', brightMagenta: '#9769c2',
      brightCyan: '#168da3', brightWhite: '#ffffff',
    },
  });
  terminal.open(document.getElementById('terminal-host'));
  await document.fonts.ready;
  const content = await fetch(`/snapshots/${encodeURIComponent(frame.file)}`);
  if (!content.ok) throw new Error('无法读取终端输出。');
  // ToAnsi terminates every row; its final newline would scroll a full-size frame.
  const ansi = (await content.text()).replace(/\r?\n$/, '');
  await new Promise(resolve => terminal.write(`\x1b[2J\x1b[H${ansi}\x1b[?25l`, resolve));
  await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
  document.getElementById('state-title').textContent = frame.title;
  document.getElementById('dimensions').textContent = `${frame.cols} × ${frame.rows} 字符`;
  document.body.dataset.state = frame.state;
  document.title = `${frame.title} · Device Radar`;
  window.__radarReady = true;
  window.__radarTerminal = terminal;
}

render().catch(error => {
  const element = document.getElementById('error');
  element.textContent = error.message;
  element.hidden = false;
  window.__radarError = error.message;
});
