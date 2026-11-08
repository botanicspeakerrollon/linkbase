// Keyboard shortcuts. One listener for the whole app: the layout installs it for the keys that work everywhere, and a page
// registers the keys it handles itself while it's open. Keys typed into a field, or pressed with Ctrl, Alt or the
// Windows or Command key, are left alone, so typing and the browser's own shortcuts still work.

const chordTimeout = 1500;

let layout = null;
let pages = [];
let nextHandle = 1;
let chordStarted = 0;

function typingInto(target) {
    if (!(target instanceof Element)) {
        return false;
    }

    return target.isContentEditable || target.closest('input, textarea, select, [contenteditable="true"]') !== null;
}

function visible(element) {
    return element.offsetParent !== null || element.getClientRects().length > 0;
}

// The page's own search box if it marks one, or else the first field on the page.
function focusSearch() {
    const marked = [...document.querySelectorAll('[data-hotkey-search]')].find(visible);
    const field = marked?.matches('input, textarea') ? marked : marked?.querySelector('input, textarea');
    const target = field ?? [...document.querySelectorAll('.app-page input:not([type=hidden]):not([type=checkbox]):not([type=radio]):not([type=file]), .app-page textarea')].find(visible);
    if (!target) {
        return false;
    }

    target.focus();
    target.select?.();
    return true;
}

// The rail's links in order, so g then 1 is the first one.
function goToRailLink(index) {
    const links = [...document.querySelectorAll('.app-rail a.mud-nav-link')];
    const link = links[index];
    if (!link) {
        return false;
    }

    link.click();
    return true;
}

function onKeyDown(event) {
    if (event.defaultPrevented || event.ctrlKey || event.altKey || event.metaKey || typingInto(event.target)) {
        return;
    }

    // While a dialog is open only it should react, apart from Escape, which MudBlazor's dialogs handle themselves.
    if (document.querySelector('.mud-dialog-container')) {
        return;
    }

    const key = event.key;

    // Esc belongs to an open menu or dropdown first.
    if (key === 'Escape' && document.querySelector('.mud-popover-open')) {
        return;
    }

    if (chordStarted && Date.now() - chordStarted < chordTimeout) {
        chordStarted = 0;
        const handled = /^[1-9]$/.test(key) ? goToRailLink(Number(key) - 1)
            : key === 'h' ? goToRailLink(0)
            : key === 'e' ? goToRailLink(1)
            : false;
        if (handled) {
            event.preventDefault();
        }

        return;
    }

    chordStarted = 0;
    if (key === 'g') {
        chordStarted = Date.now();
        event.preventDefault();
        return;
    }

    if (key === '/') {
        if (focusSearch()) {
            event.preventDefault();
        }

        return;
    }

    if (key === '?') {
        event.preventDefault();
        layout?.invokeMethodAsync('OnHotkey', key);
        return;
    }

    // The most recently opened page gets first refusal.
    const page = [...pages].reverse().find(p => p.keys.includes(key));
    if (page) {
        event.preventDefault();
        page.dotnet.invokeMethodAsync('OnHotkey', key);
    }
}

export function install(dotnet) {
    if (!layout) {
        document.addEventListener('keydown', onKeyDown);
    }

    layout = dotnet;
}

export function uninstall() {
    document.removeEventListener('keydown', onKeyDown);
    layout = null;
    pages = [];
}

export function register(dotnet, keys) {
    const handle = nextHandle++;
    pages.push({ handle, dotnet, keys });
    return handle;
}

export function unregister(handle) {
    pages = pages.filter(p => p.handle !== handle);
}
