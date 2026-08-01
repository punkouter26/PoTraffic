// pt-hotkeys.js — global keyboard shortcuts.
//
// Listens on the document so a shortcut works wherever focus happens to be, and
// suppresses bare-letter shortcuts while the user is typing: `n` must insert an
// "n" in an address field, not navigate away mid-sentence.

const EDITABLE = new Set(["INPUT", "TEXTAREA", "SELECT"]);

function isTyping(target) {
    if (!target) return false;
    if (EDITABLE.has(target.tagName)) return true;
    return target.isContentEditable === true;
}

/**
 * Invokes `<method>(key)` on `dotnetRef` for each recognised shortcut, where key is
 * one of: "palette" | "new" | "search" | "check" | "help" | "escape".
 * Returns a cleanup function reference for disposal from .NET.
 */
export function register(dotnetRef, method) {
    const send = (key, e) => {
        e.preventDefault();
        try { dotnetRef.invokeMethodAsync(method, key); } catch { /* component gone */ }
    };

    const onKeyDown = (e) => {
        // Ctrl/Cmd+K opens the palette from anywhere, including a focused input —
        // that is the point of it.
        if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
            send("palette", e);
            return;
        }

        if (e.key === "Escape") {
            send("escape", e);
            return;
        }

        // Everything below is a bare key, so it must not fire mid-typing, and must
        // not hijack a browser or OS chord.
        if (isTyping(e.target) || e.ctrlKey || e.metaKey || e.altKey) return;

        switch (e.key) {
            case "/": send("search", e); break;
            case "n": send("new", e); break;
            case "c": send("check", e); break;
            case "?": send("help", e); break;
            default: break;
        }
    };

    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
}
