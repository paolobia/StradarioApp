# Session Handoff

> Generated: 2026-08-05 | Branch: main

## Completed
- Chiarito all'utente il paste da PuTTY via SSH (click destro del mouse, non Ctrl+V) — nessuna modifica al repo.
- Installati 3 plugin/marketplace .NET (`dotnet-claude-kit`, `dotnet-skills`, `csharp-lsp`) più `frontend-design` (effetto collaterale).
- Eseguito `/doctor` completo (read-only): unico finding concreto è la sovrapposizione tra `dotnet-claude-kit` e `dotnet-skills` (skill listing già troncato, routing potenzialmente degradato). L'utente ha rifiutato la proposta di spostare le sezioni publish/release del `CLAUDE.md` root in una skill lazy-load ("allora lascia stare").
- Elencate a richiesta le skill attualmente attive (nessuna modifica).
- Salvata memoria di sessione (3 file: `user-profile.md` aggiornato, `dotnet-plugins-installed.md` e `feedback-claude-md-stays-monolithic.md` creati) e indice `MEMORY.md` aggiornato.
- `/compact` eseguito su richiesta esplicita dell'utente.

## Pending
- Nessun task di codice in sospeso da questa sessione — è stata una sessione di sola configurazione/manutenzione dell'ambiente Claude Code, non di sviluppo su StradarioApp.
- Se in futuro il routing delle skill sembra degradato in questo progetto, valutare la disabilitazione di uno dei due marketplace .NET sovrapposti (`dotnet-claude-kit` vs `dotnet-skills`) — solo su richiesta esplicita dell'utente, vedi memoria `dotnet-plugins-installed.md`.
- `CLAUDE.md.old` (73.543 byte, file non tracciato in git) resta nella working tree, non toccato né chiarito in questa sessione — potrebbe essere un residuo da chiedere all'utente se serve ancora.

## Learned
- L'utente si collega via SSH da PuTTY: `Ctrl+V` non funziona nel terminale, si usa il click destro del mouse.
- L'utente preferisce che il `CLAUDE.md` root resti monolitico (73k+ caratteri): rifiuta spostamenti verso skill lazy-load per risparmi modesti (centinaia di token), perché perderebbe la garanzia di essere sempre in contesto — vedi `feedback-claude-md-stays-monolithic.md` in memoria.
- `/compact` è un comando nativo della CLI, non invocabile tramite lo strumento Skill.

## Context
- Branch: main | Ultimo commit: "Fix import KML, colori distinti import, visibilità albero; redesign pannello alternative OSRM" (2d74387)
- Uncommitted changes: `.claude/settings.json` e `CLAUDE.md.old` non tracciati (preesistenti, non toccati in questa sessione)
- Nessuna solution `.sln`/`.slnx` — progetto singolo `StradarioApp.csproj` alla radice
