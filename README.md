# CalendarioDnD

Calendario delle presenze per le sessioni di D&D: ogni giocatore indica per ogni giorno se c'è o se manca, e sotto ogni giorno compare il riepilogo **P: n** / **A: n**.

Programma nativo per Windows (WinForms, .NET Framework 4, già incluso in Windows 10/11): nessuna installazione richiesta.

## Funzioni

- Accesso scegliendo il proprio nome dalla lista dei giocatori approvati.
- I nuovi giocatori chiedono la registrazione; il **Master** la approva o la rifiuta (Gestisci → Giocatori e richieste).
- Il Master entra con una password, scelta al primo accesso e sincronizzata su tutti i PC; può creare, rinominare ed eliminare le campagne e importare dati.
- Più campagne, ognuna con le sue presenze.
- **Conflitti di sessione del Master:** il Master è uno solo, quindi non può essere "Ci sono" in due campagne lo stesso giorno. L'app avvisa quando succede (con l'opzione di spostare la sessione), evidenzia in rosso i giorni in conflitto e in Gestisci → Verifica conflitti di sessione li elenca e permette di risolverli.
- Clic su un giorno → "Ci sono" / "Manco". Passando il mouse su un giorno si vedono i nomi.
- Menu **Dati**: esporta/importa JSON (unione o sostituzione; accetta anche i dati del vecchio `Calendario.html`).
- Sincronizzazione tra più PC tramite questo repository GitHub: i dati vengono salvati in `dati/calendario.json`; per ogni giorno vince la modifica più recente, e le campagne eliminate/rinominate si propagano.

## Avvio

**Opzione 1 — compilare sul proprio PC (consigliato, evita i blocchi di SmartScreen):**
scarica `Calendario.cs` e `compila.bat` nella stessa cartella e fai doppio clic su `compila.bat`. Viene creato `Calendario.exe`.

**Opzione 2 — usare l'exe già pronto:** scarica `Calendario.exe`, poi clic destro → Proprietà → spunta *Annulla blocco* → OK.

## Sincronizzazione GitHub

1. Crea un token: GitHub → Settings → Developer settings → **Fine-grained tokens** → Generate new token.
   - Repository access: *Only select repositories* → `CalendarioDnD`
   - Permissions → Repository permissions → **Contents: Read and write**
2. Nell'app: **Dati ▾ → Impostazioni GitHub**, incolla il token (repository predefinito: `digennarot/CalendarioDnD`).
3. Ripeti su ogni PC. Il token viene salvato cifrato con l'account Windows.

Chi non ha accesso in scrittura al repository va aggiunto come collaboratore (Settings → Collaborators) e crea il proprio token.

## File locali (accanto all'exe, esclusi da git)

| File | Contenuto |
|---|---|
| `calendario_dati.json` | copia locale dei dati |
| `calendario_impostazioni.ini` | impostazioni GitHub e ultima campagna aperta (per PC) |
| `calendario_errore.txt` | creato solo in caso di errore |

## Sicurezza: cosa sapere

- **Rendi il repository privato** (Settings → General → Danger zone → Change visibility). Se è pubblico, chiunque vede nomi, presenze e l'hash della password del Master.
- La protezione è "tra amici": chi ha un token con permesso di scrittura può modificare direttamente `dati/calendario.json` su GitHub (approvare utenti, cambiare la password del Master, cancellare dati). Dai il token solo a persone fidate; la cronologia dei commit mostra chi ha cambiato cosa.
- I giocatori non hanno password: chiunque usi l'app può scegliere qualsiasi nome approvato.
- **Consigliato:** tieni i dati in un repository separato (es. `CalendarioDnD-dati`, privato) e imposta quello nell'app. Così i token dei giocatori non possono modificare `Calendario.exe` o il codice in questo repository.
- Il token GitHub è cifrato con l'account Windows (DPAPI) e non viene mai salvato in chiaro.
