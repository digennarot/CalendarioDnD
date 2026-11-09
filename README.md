# CalendarioDnD

Calendario delle presenze per le sessioni di D&D. Ogni giocatore indica per ogni giorno se c'è o se manca; sotto ogni giorno compare il riepilogo **P: n** (presenti) / **A: n** (assenti), con la propria scelta sottolineata.

Programma nativo per Windows (WinForms, .NET Framework 4.8, già incluso in Windows 10/11): nessuna installazione richiesta. I dati si sincronizzano tra più PC tramite questo repository GitHub.

## Avvio

**Opzione 1 — usare l'exe già pronto:** scarica `Calendario.exe`, poi clic destro → Proprietà → spunta *Annulla blocco* → OK, e avvialo.

**Opzione 2 — compilare sul proprio PC** (evita gli avvisi di SmartScreen): scarica `Calendario.cs` e `compila.bat` nella stessa cartella e fai doppio clic su `compila.bat`. Viene creato `Calendario.exe` con il compilatore già presente in Windows.

Metti l'exe in una cartella dove puoi scrivere (es. Documenti o Desktop, non *Programmi*): accanto all'exe vengono salvati i file locali.

## Uso

### Accesso
- Scegli il tuo nome dalla lista e premi **Entra** (o doppio clic).
- **Nuovo giocatore?** Scrivi il tuo nome sotto *Registrati*: la richiesta resta in attesa finché il Master non la approva, poi il nome compare nella lista.
- **Master** entra con una password, scelta al suo primo accesso e uguale su tutti i PC.

### Calendario
| Azione | Come |
|---|---|
| Segnare "Ci sono" / "Manco" | clic sinistro sul giorno → scegli |
| Cancellare la propria scelta | **clic destro** sul giorno (oppure clic sinistro → *Cancella*) |
| Vedere chi c'è e chi manca | passa il mouse sul giorno |
| Cambiare mese | frecce ← → |
| Cambiare campagna | menu a tendina *Campagna* |

Ogni campagna ha le sue presenze. L'app ricorda l'ultima campagna aperta da ciascun utente.

### Funzioni del Master (pulsante **Gestisci ▾**)
- **Giocatori e richieste:** approva o rifiuta le registrazioni, rimuove giocatori.
- **Verifica conflitti di sessione:** il Master è uno solo, quindi non può essere "Ci sono" in due campagne lo stesso giorno. I giorni in conflitto sono evidenziati in rosso con un "!"; da qui si elencano e si risolvono scegliendo quale campagna tiene la sessione. Quando il Master segna una sessione in un giorno già occupato, l'app avvisa e propone di spostarla.
- **Nuova / Rinomina / Elimina campagna.**
- **Cambia password del Master.**

Il numero tra parentesi su *Gestisci* indica richieste in attesa + conflitti nei prossimi giorni.

### Menu Dati ▾
- **Sincronizza con GitHub ora** e **Impostazioni GitHub**.
- **Esporta in JSON:** copia di sicurezza di tutti i dati.
- **Importa JSON** (solo Master): *unisci* oppure *sostituisci tutto*. Accetta anche i dati del vecchio `Calendario.html`.

## Sincronizzazione GitHub

1. Crea un token: GitHub → Settings → Developer settings → **Fine-grained tokens** → Generate new token.
   - Repository access: *Only select repositories* → `CalendarioDnD`
   - Permissions → Repository permissions → **Contents: Read and write**
2. Nell'app: **Dati ▾ → Impostazioni GitHub** (raggiungibile anche dalla schermata di accesso), incolla il token. Repository predefinito: `digennarot/CalendarioDnD`, file `dati/calendario.json`.
3. Ripeti su ogni PC. Il token viene salvato cifrato con l'account Windows.

La sincronizzazione avviene all'avvio, all'accesso e dopo ogni modifica. Ogni scelta registra l'ora della modifica: se due PC cambiano lo stesso giorno vince la più recente, e cancellazioni, campagne eliminate o rinominate arrivano anche sugli altri PC. Senza connessione l'app lavora sui dati locali e si riallinea alla sincronizzazione successiva.

Chi non ha accesso in scrittura al repository va aggiunto come collaboratore (Settings → Collaborators) e crea il proprio token.

## File locali (accanto all'exe, esclusi da git)

| File | Contenuto |
|---|---|
| `calendario_dati.json` | copia locale dei dati |
| `calendario_impostazioni.ini` | impostazioni GitHub e ultima campagna aperta (per PC) |
| `calendario_errore.txt` | creato solo in caso di errore |

## Problemi comuni

- **L'exe non parte o sparisce:** Windows Defender/SmartScreen lo ha bloccato. Togli il blocco da Proprietà, oppure compila con `compila.bat`.
- **Finestra di errore:** il testo viene salvato anche in `calendario_errore.txt`; aprire una issue allegandolo.
- **"GitHub ha risposto 401/403/404":** token scaduto, senza permesso *Contents: Read and write*, o repository/branch errato nelle impostazioni.

## Sicurezza: cosa sapere

- **Rendi il repository privato** (Settings → General → Danger zone → Change visibility). Se è pubblico, chiunque vede nomi, presenze e l'hash della password del Master.
- La protezione è "tra amici": chi ha un token con permesso di scrittura può modificare direttamente `dati/calendario.json` su GitHub (approvare utenti, cambiare la password del Master, cancellare dati). Dai il token solo a persone fidate; la cronologia dei commit mostra chi ha cambiato cosa.
- I giocatori non hanno password: chiunque usi l'app può scegliere qualsiasi nome approvato.
- **Consigliato:** tieni i dati in un repository separato (es. `CalendarioDnD-dati`, privato) e imposta quello nell'app. Così i token dei giocatori non possono modificare `Calendario.exe` o il codice in questo repository.
- Il token GitHub è cifrato con l'account Windows (DPAPI) e non viene mai salvato in chiaro. La password del Master è salvata come hash PBKDF2.

## Sviluppo

Tutto il programma è in `Calendario.cs` (C# 5, senza dipendenze esterne). Su Windows si compila con `compila.bat`. Compilando con Mono (`mcs`) bisogna usare gli assembly di riferimento di .NET Framework 4.8 (`-nostdlib` + [mono/reference-assemblies](https://github.com/mono/reference-assemblies) `v4.8`), altrimenti l'exe può usare metodi che su Windows non esistono.
