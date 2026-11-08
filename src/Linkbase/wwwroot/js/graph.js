// Draws the graph and reports clicks. The Explore page owns which nodes and edges are visible.
// In load order: fcose's two dependencies must come before it, and Cytoscape before them all. They're served from
// wwwroot/lib, so Explore works offline. See README.md for how to update one.
const scriptUrls = [
    'lib/cytoscape-3.30.4/cytoscape.min.js',
    'lib/layout-base-2.0.1/layout-base.js',
    'lib/cose-base-2.2.0/cose-base.js',
    'lib/cytoscape-fcose-2.2.0/cytoscape-fcose.js',
];
const maxFitZoom = 1.2;
const menuWidth = 240;
const menuHeight = 340;

// How dragging feels. Stiffness is how hard links pull back, damping is how quickly the wobble dies away,
// and each hop further from the dragged node moves less, until the graph beyond them holds still.
const dragHops = 3;
const dragHopFalloff = 0.55;
const dragStiffness = 0.22;
const dragDamping = 0.6;
const dragGap = 70;
const dragRestSpeed = 0.05;
const dragMaxSettleFrames = 240;
const labelGap = 10;
const spiralStep = 30;

let cy = null;
let resizeObserver = null;
let loading = null;
let squish = null;

function loadScript(url) {
    return new Promise((resolve, reject) => {
        const script = document.createElement('script');
        script.src = url;
        script.onload = resolve;
        script.onerror = () => reject(new Error(`Could not load ${url}.`));
        document.head.appendChild(script);
    });
}

function loadCytoscape() {
    loading ??= (async () => {
        for (const url of scriptUrls) {
            await loadScript(url);
        }
    })().catch(error => {
        loading = null;
        throw error;
    });
    return loading;
}

function iconImage(svgPaths) {
    const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="#ffffff">${svgPaths}</svg>`;
    return `data:image/svg+xml;utf8,${encodeURIComponent(svg)}`;
}

export async function create(element, dotNetRef, labelStyles) {
    await loadCytoscape();
    destroy();

    const nodeStyles = labelStyles.map(s => ({
        selector: `node[label = "${s.label}"]`,
        style: { 'background-color': s.color, 'background-image': iconImage(s.icon) },
    }));

    cy = window.cytoscape({
        container: element,
        minZoom: 0.1,
        maxZoom: 3,
        style: [
            {
                selector: 'node',
                style: {
                    label: 'data(caption)',
                    width: 34,
                    height: 34,
                    'background-fit': 'none',
                    'background-width': '62%',
                    'background-height': '62%',
                    'font-size': 11,
                    color: '#d7dbe0',
                    'text-valign': 'bottom',
                    'text-margin-y': 4,
                    'text-wrap': 'ellipsis',
                    'text-max-width': 140,
                    'min-zoomed-font-size': 6,
                },
            },
            ...nodeStyles,
            {
                selector: 'node:selected',
                style: { 'border-width': 3, 'border-color': '#ffffff' },
            },
            {
                selector: 'edge',
                style: {
                    width: 1.5,
                    'line-color': '#4a525e',
                    'curve-style': 'bezier',
                    'target-arrow-shape': 'triangle',
                    'target-arrow-color': '#4a525e',
                    label: 'data(relType)',
                    'font-size': 8,
                    color: '#9aa3af',
                    'text-rotation': 'autorotate',
                    'text-background-color': '#14171c',
                    'text-background-opacity': 1,
                    'text-background-padding': 1,
                    'min-zoomed-font-size': 8,
                },
            },
        ],
    });

    // As in Bloom: a click selects, and a right-click opens the menu. A double-click expands, as a shortcut.
    element.addEventListener('contextmenu', event => event.preventDefault());
    cy.on('tap', 'node', event => dotNetRef.invokeMethodAsync('OnNodeClicked', event.target.id()));
    cy.on('dbltap', 'node', event => dotNetRef.invokeMethodAsync('OnNodeDoubleClicked', event.target.id()));
    cy.on('cxttap', 'node', event => {
        // Keeps the menu on screen when the node is near the canvas's edge.
        const x = Math.max(0, Math.min(event.renderedPosition.x, cy.width() - menuWidth));
        const y = Math.max(0, Math.min(event.renderedPosition.y, cy.height() - menuHeight));
        dotNetRef.invokeMethodAsync('OnNodeRightClicked', event.target.id(), x, y);
    });
    cy.on('tap cxttap', event => {
        if (event.target === cy) {
            dotNetRef.invokeMethodAsync('OnBackgroundClicked');
        }
    });
    cy.on('viewport', () => dotNetRef.invokeMethodAsync('OnViewportChanged'));
    cy.on('drag', 'node', () => {
        squish ??= startSquish(cy.nodes(':grabbed'));
    });
    cy.on('free', 'node', () => {
        if (squish) {
            squish.released = true;
        }
    });

    // The canvas changes size when a notice appears above it, or the window is resized.
    resizeObserver = new ResizeObserver(() => cy?.resize());
    resizeObserver.observe(element);
}

// Links act as springs while a node is dragged, so what's joined to it follows and then settles. Each spring's
// natural length is what it was when the drag began, so the graph keeps its shape and nothing drifts afterwards.
function startSquish(grabbed) {
    const moving = new Map();
    let seen = grabbed;
    let frontier = grabbed;
    for (let hop = 1; hop <= dragHops; hop++) {
        frontier = frontier.neighborhood().nodes().difference(seen);
        seen = seen.union(frontier);
        const give = Math.pow(dragHopFalloff, hop - 1);
        frontier.forEach(node => moving.set(node.id(), { node, give, links: 0, vx: 0, vy: 0, fx: 0, fy: 0 }));
    }

    const springs = cy.edges()
        .filter(edge => moving.has(edge.source().id()) || moving.has(edge.target().id()))
        .map(edge => {
            const [a, b] = [edge.source(), edge.target()];
            for (const end of [a, b]) {
                const body = moving.get(end.id());
                if (body) {
                    body.links++;
                }
            }

            return { a, b, length: distance(a.position(), b.position()) };
        });

    const state = { released: false, settling: 0, frame: 0 };
    const step = () => {
        const speed = stepSquish(moving, springs);
        state.settling += state.released ? 1 : 0;
        const settled = state.released && (speed < dragRestSpeed || state.settling > dragMaxSettleFrames);
        if (settled) {
            squish = null;
        } else {
            state.frame = requestAnimationFrame(step);
        }
    };
    state.frame = requestAnimationFrame(step);
    return state;
}

function stepSquish(moving, springs) {
    const push = (body, x, y) => {
        if (body) {
            body.fx += x;
            body.fy += y;
        }
    };

    for (const { a, b, length } of springs) {
        const [from, to] = [a.position(), b.position()];
        const gap = distance(from, to) || 1;
        const pull = (dragStiffness * (gap - length)) / gap;
        const [x, y] = [(to.x - from.x) * pull, (to.y - from.y) * pull];
        push(moving.get(a.id()), x, y);
        push(moving.get(b.id()), -x, -y);
    }

    // Hubs share the pull between their links, or a node with many of them would shoot off.
    const everything = cy.nodes().map(node => ({ id: node.id(), position: node.position() }));
    let fastest = 0;
    cy.batch(() => {
        for (const [id, body] of moving) {
            const position = body.node.position();
            let [x, y] = [(body.fx / body.links) * body.give, (body.fy / body.links) * body.give];
            for (const other of everything) {
                const gap = distance(position, other.position);
                if (other.id !== id && gap < dragGap) {
                    const away = (dragGap - gap) / (gap || 1) * 0.3;
                    x += (position.x - other.position.x) * away;
                    y += (position.y - other.position.y) * away;
                }
            }

            body.vx = (body.vx + x) * dragDamping;
            body.vy = (body.vy + y) * dragDamping;
            body.fx = 0;
            body.fy = 0;
            fastest = Math.max(fastest, Math.abs(body.vx), Math.abs(body.vy));
            body.node.position({ x: position.x + body.vx, y: position.y + body.vy });
        }
    });
    return fastest;
}

function stopSquish() {
    if (squish) {
        cancelAnimationFrame(squish.frame);
        squish = null;
    }
}

function distance(a, b) {
    return Math.hypot(a.x - b.x, a.y - b.y);
}

// The layout spaces nodes by their centres, so long labels can still collide. This moves each new node to the
// nearest free spot, labels included, spiralling out from where the layout put it. Nodes already on the canvas never move.
function separateNew(added) {
    const newIds = new Set(added.map(node => node.id()));
    const taken = cy.nodes().filter(node => !newIds.has(node.id())).map(boxOf);
    for (const node of added) {
        const box = boxOf(node);
        const spot = freeSpot(box, taken);
        const position = node.position();
        node.position({ x: position.x + spot.x, y: position.y + spot.y });
        taken.push({ x1: box.x1 + spot.x, x2: box.x2 + spot.x, y1: box.y1 + spot.y, y2: box.y2 + spot.y });
    }
}

function boxOf(node) {
    return node.boundingBox({ includeLabels: true, includeOverlays: false });
}

function freeSpot(box, taken) {
    const isFree = (x, y) => !taken.some(other =>
        box.x1 + x < other.x2 + labelGap && other.x1 < box.x2 + x + labelGap
        && box.y1 + y < other.y2 + labelGap && other.y1 < box.y2 + y + labelGap);

    for (let ring = 0; ; ring++) {
        const spots = Math.max(1, ring * 8);
        for (let i = 0; i < spots; i++) {
            const angle = (2 * Math.PI * i) / spots;
            const [x, y] = [ring * spiralStep * Math.cos(angle), ring * spiralStep * Math.sin(angle)];
            if (isFree(x, y)) {
                return { x, y };
            }
        }
    }
}

// Fits everything in view, without blowing a handful of nodes up to fill the canvas.
function fitAll() {
    cy.fit(cy.elements(), 40);
    if (cy.zoom() > maxFitZoom) {
        cy.zoom(maxFitZoom);
        cy.center(cy.elements());
    }
}

function placeAround(anchor, nodes) {
    const centre = anchor
        ? anchor.position()
        : { x: cy.extent().x1 + cy.extent().w / 2, y: cy.extent().y1 + cy.extent().h / 2 };
    const radius = 90 + nodes.length * 5;
    nodes.forEach((node, index) => {
        const angle = (2 * Math.PI * index) / nodes.length;
        node.position({ x: centre.x + radius * Math.cos(angle), y: centre.y + radius * Math.sin(angle) });
    });
}

// Applies the visible graph. Nodes already on the canvas stay exactly where they are: they're pinned
// while the layout settles the new ones around them.
export function update(nodes, edges, anchorId) {
    if (!cy) {
        return;
    }

    stopSquish();
    const wantedNodes = new Set(nodes.map(n => n.id));
    const wantedEdges = new Set(edges.map(e => e.id));
    cy.elements().filter(e => (e.isNode() ? !wantedNodes.has(e.id()) : !wantedEdges.has(e.id()))).remove();

    const added = nodes
        .filter(n => cy.getElementById(n.id).empty())
        .map(n => cy.add({ group: 'nodes', data: { id: n.id, label: n.label, caption: n.caption } }));
    for (const edge of edges) {
        if (cy.getElementById(edge.id).empty()) {
            cy.add({ group: 'edges', data: { id: edge.id, source: edge.source, target: edge.target, relType: edge.relType } });
        }
    }

    if (added.length === 0) {
        return;
    }

    const anchor = anchorId ? cy.getElementById(anchorId) : null;
    placeAround(anchor && anchor.nonempty() ? anchor : null, added);

    const addedIds = new Set(added.map(n => n.id()));
    const existing = cy.nodes().filter(n => !addedIds.has(n.id()));
    const pinned = existing.map(n => ({ nodeId: n.id(), position: { ...n.position() } }));
    const layout = cy.layout({
        name: 'fcose',
        animate: false,
        // A blank canvas can start from scratch. Adding to one has to grow from what's there.
        randomize: pinned.length === 0,
        fit: false,
        nodeDimensionsIncludeLabels: true,
        idealEdgeLength: () => 130,
        nodeRepulsion: () => 30000,
        nodeSeparation: 120,
        fixedNodeConstraint: pinned,
    });
    layout.one('layoutstop', () => {
        restorePinned(pinned);
        separateNew(added);
        fitAll();
    });
    layout.run();
}

// fcose keeps pinned nodes in the right places relative to each other, but can shift the whole result.
// This undoes the shift, so nodes already on the canvas end up exactly where they started.
function restorePinned(pinned) {
    if (pinned.length === 0) {
        return;
    }

    const first = cy.getElementById(pinned[0].nodeId).position();
    const shift = { x: pinned[0].position.x - first.x, y: pinned[0].position.y - first.y };
    cy.nodes().forEach(node => {
        const position = node.position();
        node.position({ x: position.x + shift.x, y: position.y + shift.y });
    });
    for (const { nodeId, position } of pinned) {
        cy.getElementById(nodeId).position(position);
    }
}

export function select(nodeId) {
    if (!cy) {
        return;
    }

    cy.nodes(':selected').unselect();
    if (nodeId) {
        cy.getElementById(nodeId).select();
    }
}

export function positions() {
    return cy ? cy.nodes().map(n => ({ nodeId: n.id(), x: n.position('x'), y: n.position('y') })) : [];
}

// Shows a saved scene with its nodes exactly where they were saved.
export function restore(nodes, edges, savedPositions) {
    if (!cy) {
        return;
    }

    stopSquish();
    const positionById = new Map(savedPositions.map(p => [p.nodeId, p]));
    cy.elements().remove();
    for (const node of nodes) {
        const saved = positionById.get(node.id);
        cy.add({
            group: 'nodes',
            data: { id: node.id, label: node.label, caption: node.caption },
            position: saved ? { x: saved.x, y: saved.y } : { x: 0, y: 0 },
        });
    }

    for (const edge of edges) {
        cy.add({ group: 'edges', data: { id: edge.id, source: edge.source, target: edge.target, relType: edge.relType } });
    }

    fitAll();
}

// Where a node is on screen, relative to the canvas.
export function renderedPosition(nodeId) {
    return cy?.getElementById(nodeId).renderedPosition() ?? null;
}

// The space each node takes up, including its label.
export function boxes() {
    return cy ? cy.nodes().map(n => ({ nodeId: n.id(), ...n.boundingBox({ includeLabels: true, includeOverlays: false }) })) : [];
}

export function fit() {
    if (cy && cy.elements().nonempty()) {
        fitAll();
    }
}

// The whole graph as a picture, at twice the screen's resolution, on the page's own background. Returns false when
// there's nothing to draw.
export async function savePng(fileName) {
    if (!cy || cy.elements().empty()) {
        return false;
    }

    // The theme always sets this, so the picture matches the page.
    const background = getComputedStyle(document.documentElement).getPropertyValue('--mud-palette-background').trim();
    const blob = cy.png({ output: 'blob', full: true, scale: 2, bg: background });
    const { saveBlob } = await import('./download.js');
    saveBlob(fileName, blob);
    return true;
}

export function destroy() {
    stopSquish();
    resizeObserver?.disconnect();
    resizeObserver = null;
    if (cy) {
        cy.destroy();
        cy = null;
    }
}
