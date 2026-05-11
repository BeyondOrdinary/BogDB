// Force-directed graph canvas renderer for SocialGraph · BogDB
// Called via IJSRuntime.InvokeVoidAsync("drawForceGraph", canvasId, nodes, edges)

window.drawForceGraph = function (canvasId, nodes, edges) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;

    const ctx    = canvas.getContext('2d');
    const W      = canvas.offsetWidth;
    const H      = canvas.offsetHeight;
    canvas.width  = W;
    canvas.height = H;

    const groupColors = {
        'Engineering': '#8b5cf6',
        'Product':     '#ec4899',
        'Operations':  '#22d3ee',
    };

    // Build position map
    const posMap = {};
    nodes.forEach((n, i) => {
        const angle = (i / nodes.length) * Math.PI * 2;
        const r = Math.min(W, H) * 0.35;
        posMap[n.id] = {
            x: W / 2 + r * Math.cos(angle),
            y: H / 2 + r * Math.sin(angle),
            vx: 0, vy: 0,
            node: n,
        };
    });

    // Build adjacency for force
    const adj = {};
    edges.forEach(e => {
        if (!adj[e.from]) adj[e.from] = [];
        if (!adj[e.to])   adj[e.to]   = [];
        adj[e.from].push({ id: e.to, strength: e.strength });
        adj[e.to].push({ id: e.from, strength: e.strength });
    });

    const REPEL = 80, ATTRACT = 0.012, DAMPEN = 0.88, MAX_FRAMES = 200;
    let frame = 0;
    let animId = null;

    // Cancel any previous animation
    if (canvas._animId) cancelAnimationFrame(canvas._animId);

    function tick() {
        frame++;

        // Repulsion between all nodes
        const ps = Object.values(posMap);
        for (let i = 0; i < ps.length; i++) {
            for (let j = i + 1; j < ps.length; j++) {
                const a = ps[i], b = ps[j];
                const dx = b.x - a.x, dy = b.y - a.y;
                const dist = Math.sqrt(dx * dx + dy * dy) || 1;
                const force = REPEL / (dist * dist);
                a.vx -= force * dx / dist;
                a.vy -= force * dy / dist;
                b.vx += force * dx / dist;
                b.vy += force * dy / dist;
            }
        }

        // Attraction along edges
        edges.forEach(e => {
            const a = posMap[e.from], b = posMap[e.to];
            if (!a || !b) return;
            const dx = b.x - a.x, dy = b.y - a.y;
            const dist = Math.sqrt(dx * dx + dy * dy) || 1;
            const target = 60 + (1 - e.strength) * 80;
            const force  = (dist - target) * ATTRACT * e.strength;
            a.vx += force * dx / dist;
            a.vy += force * dy / dist;
            b.vx -= force * dx / dist;
            b.vy -= force * dy / dist;
        });

        // Centre gravity
        ps.forEach(p => {
            p.vx += (W / 2 - p.x) * 0.002;
            p.vy += (H / 2 - p.y) * 0.002;
            p.vx *= DAMPEN; p.vy *= DAMPEN;
            p.x += p.vx; p.y += p.vy;
            p.x = Math.max(12, Math.min(W - 12, p.x));
            p.y = Math.max(12, Math.min(H - 12, p.y));
        });

        draw();

        if (frame < MAX_FRAMES) {
            animId = requestAnimationFrame(tick);
        }
        canvas._animId = animId;
    }

    function draw() {
        ctx.clearRect(0, 0, W, H);
        ctx.fillStyle = '#06070f';
        ctx.fillRect(0, 0, W, H);

        // Edges
        edges.forEach(e => {
            const a = posMap[e.from], b = posMap[e.to];
            if (!a || !b) return;
            ctx.beginPath();
            ctx.strokeStyle = `rgba(139,92,246,${0.05 + e.strength * 0.2})`;
            ctx.lineWidth   = 0.5 + e.strength;
            ctx.moveTo(a.x, a.y);
            ctx.lineTo(b.x, b.y);
            ctx.stroke();
        });

        // Nodes
        Object.values(posMap).forEach(p => {
            const color = groupColors[p.node.group] || '#8b5cf6';
            ctx.beginPath();
            ctx.arc(p.x, p.y, 5, 0, Math.PI * 2);
            ctx.fillStyle   = color;
            ctx.shadowBlur  = 8;
            ctx.shadowColor = color;
            ctx.fill();
            ctx.shadowBlur = 0;
        });
    }

    tick();

    // Drag to pan
    let drag = null;
    canvas.addEventListener('mousedown', e => {
        const rect = canvas.getBoundingClientRect();
        const mx = e.clientX - rect.left, my = e.clientY - rect.top;
        let best = null, bestDist = 16;
        Object.values(posMap).forEach(p => {
            const d = Math.hypot(p.x - mx, p.y - my);
            if (d < bestDist) { best = p; bestDist = d; }
        });
        if (best) drag = best;
    });

    canvas.addEventListener('mousemove', e => {
        if (!drag) return;
        const rect = canvas.getBoundingClientRect();
        drag.x = e.clientX - rect.left;
        drag.y = e.clientY - rect.top;
        drag.vx = 0; drag.vy = 0;
        if (frame >= MAX_FRAMES) draw();
    });

    canvas.addEventListener('mouseup', () => drag = null);
    canvas.addEventListener('mouseleave', () => drag = null);
};
