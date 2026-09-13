// Collocated entry for Components/Pages/Home.razor.
//
// The static SSR render has no circuit to invoke JavaScript through, so the
// browser loads this module directly. It pulls in the shared runtime, which
// finds the [data-portal] root and attaches the status poller and xterm view.
import "/js/terminal.js";
