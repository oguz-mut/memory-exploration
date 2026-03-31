static class DashboardHtml
{
    public const string PAGE = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Match-3 Solver</title>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{background:#0a0e17;color:#e0e0e0;font-family:'Segoe UI',system-ui,sans-serif;padding:16px}
h1{color:#ffd700;font-size:1.4em;margin-bottom:8px}
.status{padding:8px 16px;border-radius:6px;font-weight:bold;display:inline-block;margin-bottom:12px}
.status.waiting{background:#1a1a2e;color:#888}
.status.solving{background:#2a1a00;color:#ffa500}
.status.solved{background:#0a2a0a;color:#4caf50}
.status.error{background:#2a0a0a;color:#f44336}
.source-badge{background:#0a2a0a;color:#4caf50;padding:4px 12px;border-radius:4px;font-size:12px;display:inline-block;margin-left:8px}
.container{display:grid;grid-template-columns:auto 1fr;gap:16px;max-width:1200px}
.board-panel{background:#12162a;border:1px solid #2a2e4a;border-radius:8px;padding:16px}
.info-panel{background:#12162a;border:1px solid #2a2e4a;border-radius:8px;padding:16px}
.board{display:inline-grid;gap:2px;margin:8px 0}
.cell{width:44px;height:44px;border-radius:6px;display:flex;align-items:center;justify-content:center;font-size:11px;font-weight:bold;color:#fff;text-shadow:0 1px 2px rgba(0,0,0,.6);position:relative}
.cell.highlight{outline:3px solid #ffd700;outline-offset:-1px;z-index:1}
.cell .arrow{position:absolute;font-size:18px;color:#ffd700;filter:drop-shadow(0 0 4px #ffd700)}
.colors{display:flex;gap:6px;flex-wrap:wrap;margin:8px 0;font-size:12px}
.colors span{padding:2px 8px;border-radius:4px;font-weight:bold}
h2{color:#ccc;font-size:1.1em;margin:12px 0 6px}
.move{padding:6px 10px;margin:3px 0;border-radius:4px;background:#1a1e2e;border-left:3px solid #555;font-family:monospace;font-size:13px}
.move.best{border-left-color:#ffd700;background:#1a1a0a}
.move .badge{background:#ffd700;color:#000;padding:1px 6px;border-radius:3px;font-size:10px;margin-right:6px}
.config-grid{display:grid;grid-template-columns:auto 1fr;gap:2px 12px;font-size:13px}
.config-grid .label{color:#888}
.config-grid .val{color:#ddd;font-family:monospace}
.stats{font-size:12px;color:#888;margin-top:8px}
.score-big{font-size:2em;color:#ffd700;font-weight:bold}
</style>
</head>
<body>
<h1>Match-3 Solver</h1>
<div id="root"><div class="status waiting">Connecting...</div></div>
<script>
const PC = ['#e74c3c','#3498db','#2ecc71','#f39c12','#9b59b6','#1abc9c','#e67e22','#e91e63','#00bcd4','#8bc34a'];
const AR = {right:'\u2192',left:'\u2190',up:'\u2191',down:'\u2193'};

function render(d) {
  const root = document.getElementById('root');
  if (d.status === 'waiting') { root.innerHTML = '<div class="status waiting">' + d.message + '</div>'; return; }
  const c = d.config, sol = d.solution;
  const firstMove = sol && sol.moves.length > 0 ? sol.moves[0] : null;

  let boardHtml = '';
  if (d.board) {
    boardHtml = '<div class="board" style="grid-template-columns:repeat('+c.width+',44px)">';
    for (let y = c.height - 1; y >= 0; y--) {
      for (let x = 0; x < c.width; x++) {
        const p = d.board[y * c.width + x];
        const color = p >= 0 ? PC[p % PC.length] : '#333';
        const label = d.pieceLabels && p >= 0 ? d.pieceLabels[p] : '';
        const short = label.length > 5 ? label.substring(0, 5) : label;
        let hl = '', arrow = '';
        if (firstMove && firstMove.x === x && firstMove.y === y) {
          hl = ' highlight';
          arrow = '<span class="arrow">' + (AR[firstMove.direction] || '') + '</span>';
        }
        boardHtml += '<div class="cell' + hl + '" style="background:' + color + '">' + short + arrow + '</div>';
      }
    }
    boardHtml += '</div>';
  }

  let colorsHtml = '<div class="colors">';
  if (c.pieces) c.pieces.forEach((p, i) => {
    colorsHtml += '<span style="background:' + PC[i % PC.length] + '">' + p.label + '</span>';
  });
  colorsHtml += '</div>';

  let movesHtml = '';
  if (sol && sol.moves.length > 0) {
    sol.moves.forEach((m, i) => {
      const cls = i === 0 ? 'move best' : 'move';
      const badge = i === 0 ? '<span class="badge">NEXT</span>' : '';
      movesHtml += '<div class="' + cls + '">' + badge + '#' + (i+1) + ': ' + m.description + ' (score: ' + m.scoreAfter + ')</div>';
    });
  }

  root.innerHTML = `
    <div class="status ${d.status}">${d.status.toUpperCase()}${d.error ? ': ' + d.error : ''}</div>
    <span class="source-badge">Source: ${d.source || 'memory'}</span>
    <div class="container">
      <div class="board-panel">
        <h2>${c.title || 'Match-3'} <span style="color:#888;font-size:.8em">(${c.width}x${c.height})</span></h2>
        ${colorsHtml}
        ${boardHtml}
        <div class="stats">Seed: ${c.randomSeed} | Session: ${d.sessionId} | ${d.receivedAt}</div>
      </div>
      <div class="info-panel">
        <h2>Predicted Score</h2>
        <div class="score-big">${sol ? sol.predictedScore : '...'}</div>
        <h2>Optimal Moves (${c.numTurns} turns)</h2>
        ${movesHtml || '<div style="color:#666">Solving...</div>'}
        ${sol ? '<div class="stats">' + sol.strategy + ' | ' + sol.statesExplored.toLocaleString() + ' states | ' + sol.solveTimeMs + 'ms</div>' : ''}
        <h2>Scoring</h2>
        <div class="config-grid">
          <span class="label">3-match:</span><span class="val">${c.scoreFor3s}</span>
          <span class="label">4-match:</span><span class="val">${c.scoreFor4s}</span>
          <span class="label">5-match:</span><span class="val">${c.scoreFor5s}</span>
          <span class="label">Tier deltas:</span><span class="val">[${(c.scoreDeltasPerTier||[]).join(', ')}]</span>
          <span class="label">Chain mult:</span><span class="val">[${(c.scoresPerChainLevel||[]).join(', ')}]</span>
          <span class="label">Tier reqs:</span><span class="val">[${(c.pieceReqsPerTier||[]).join(', ')}]</span>
        </div>
      </div>
    </div>`;
}

async function poll() {
  try { const r = await fetch('/api/state'); render(await r.json()); }
  catch(e) { document.getElementById('root').innerHTML = '<div class="status error">Connection lost</div>'; }
}
setInterval(poll, 2000);
poll();
</script>
</body>
</html>
""";
}
