# OptLife — prenditi cura del tuo PC Windows

*[Read in English](README.md)*

OptLife confronta **come il PC dovrebbe essere** con **com'è**, e ti segnala la differenza — o la corregge quando premi il
pulsante. Lavora in silenzio vicino all'orologio, ha un pannello da guardare e una riga di comando per gli script.

È un programma normale, a regole fisse: **niente IA, niente account, niente telemetria, niente pubblicità**. Funziona anche senza rete.

## Cosa fa

| Area | Cosa controlla OptLife |
|---|---|
| Hardware e prestazioni | memoria, processore, scheda video, dischi e rete ogni 2 s; problemi sulle ultime 24 ore |
| Giochi | ogni partita secondo per secondo (FPS, scatti, ping, memoria riletta dal disco); F9 segna un lag e ne cerca la causa; un overlay leggero sopra il gioco |
| Impostazioni del PC | schermo alla frequenza massima, memoria alla velocità di targa (XMP/EXPO), piano energetico, Modalità gioco, BIOS e firmware (UEFI, Secure Boot, TPM, virtualizzazione, Resizable BAR, collegamento PCIe), DNS, dispositivi audio; si accorge quando un aggiornamento cambia qualcosa alle tue spalle. Su un portatile lascia fuori i consigli da PC fisso (Prestazioni elevate, limiti della scheda video) |
| Rete | la connessione in uso: col cavo la velocità (una scheda da 1 Gb ferma a 100 Mb), i pacchetti rovinati e i distacchi; col Wi-Fi banda, velocità e segnale; il ping verso il router |
| Avvio automatico | cosa parte con Windows, cosa deve e non deve partire, e i programmi che si rimettono da soli |
| Aggiornamenti | programmi (con winget), emulatori, driver video e chipset, BIOS (solo il link alla pagina del produttore), Windows Update |
| Sicurezza | Defender, firewall, account, funzioni di Windows rischiose, porte aperte |
| Dati e copie | che i backup esistano e siano recenti, repository Git con lavoro non salvato o non inviato |
| Ordine e spazio | come tenere il Desktop (solo segnalazioni — il predefinito —, Desktop pulito, Hub per categorie o regole tue), collegamenti rotti, Download pieni, cartelle pesanti ricostruibili, file temporanei, Cestino; ogni spostamento si vede prima e si annulla |
| Libreria | i giochi del PC e quelli emulati in un posto solo, con una vista a schermo intero per il controller |
| Strumenti | un pianificatore per i tuoi lavori, pulsanti per i tuoi script, "chiudi i programmi in background", riavvio nel BIOS, e pulsanti per i programmi noti trovati sul PC (DDU, Equalizer APO, MSI Afterburner, HWiNFO…) e per svuotare la cache degli shader NVIDIA |

Ogni modulo si può spegnere, e ogni azione ha un permesso: *da solo*, *chiedi* o *mai*.

## Cosa non fa

- Non cancella mai: quello che toglie va nel Cestino, e solo quando premi il pulsante.
- Non installa driver, non tocca il BIOS, non pulisce il registro.
- Non scarica BIOS, firmware, chiavi o giochi.
- Non manda niente di tuo da nessuna parte. In [Rete](#rete) trovi ogni collegamento che fa.

## Perché gira da amministratore

L'agente in background gira da amministratore (un'attività pianificata all'accesso) per poter, **solo quando lo chiedi tu**:
accendere e spegnere le voci di avvio registrate per tutti gli utenti, cambiare i server DNS, aggiornare i programmi senza
una richiesta di Windows per ognuno, leggere le funzioni e le impostazioni di sicurezza di Windows, riavviare nel BIOS.
I programmi che avvia per te (i tuoi lavori, gli strumenti senza il segno "amministratore", gli aggiornamenti) girano
**come te**, non da amministratore. Siccome le impostazioni decidono cosa esegue l'agente, le cambia solo un amministratore:
il pannello si apre con i permessi di amministratore (Windows può chiederli), e la riga di comando cambia le impostazioni solo da
un terminale amministratore (leggere si può ovunque). Un lavoro o uno strumento "da amministratore" parte solo da un file che chi
non è amministratore non può modificare. Dettagli in [SECURITY.md](SECURITY.md).

## Rete

OptLife si collega solo a:

- il router, un indirizzo pubblico e gli host che scrivi tu (NAS, server di backup), per il ping — gli indirizzi sono nelle impostazioni;
- durante una partita, il server del gioco o il suo datacenter, quando il gioco lo scrive nel suo registro (misura della latenza);
- `api.github.com` e `github.com` per le versioni nuove degli emulatori e di OptLife stesso (senza account né token);
- winget e Windows Update (Microsoft) quando si controllano gli aggiornamenti;
- per le copertine della Libreria: i server pubblici del negozio e delle immagini di Steam, GameTDB e gli archivi libretro
  di copertine e database su GitHub (si spegne tutto con l'impostazione "online" della Libreria).

Nient'altro, e mai con dati tuoi. I pulsanti che aprono un sito (la pagina di un driver, di un'app) lo aprono nel tuo browser.

## Requisiti

- Windows 10 (22H2) o Windows 11, 64 bit.
- Nient'altro da installare: .NET è compreso.
- Facoltativi: una scheda NVIDIA (le letture della scheda video arrivano dal suo driver), MSI Afterburner (temperatura del
  processore, anche sui Ryzen; letture delle schede AMD) e RTSS (frame nei giochi). Senza, quei valori compaiono come "non
  disponibile", con cosa serve per averli; OptLife non li scarica mai, e trova Afterburner da solo appena è aperto.

## Installazione

1. Scarica `OptLife-Setup-<versione>.exe` e `SHA256SUMS.txt` dalla pagina [Releases](../../releases).
2. Controlla che il file sia quello pubblicato (in PowerShell, nella cartella dei download):
   ```powershell
   Get-FileHash .\OptLife-Setup-*.exe -Algorithm SHA256
   ```
   Il valore deve essere uguale alla riga di `SHA256SUMS.txt`.
3. Avvia l'installatore. **Windows SmartScreen** può dire *"PC protetto da Windows"*: le prime versioni non sono ancora
   firmate con un certificato. Premi **Ulteriori informazioni → Esegui comunque** solo se lo SHA-256 corrisponde.
4. L'installatore mette OptLife in `Programmi`, lo aggiunge al menu Start e fa partire l'agente a ogni accesso.
   Alla prima apertura del pannello una breve visita guidata ti fa scegliere la lingua e cosa ti interessa.

Storico e impostazioni stanno in `%ProgramData%\OptLife`. Un aggiornamento si installa sopra e li conserva.

## Aggiornamento

OptLife controlla una volta al giorno se c'è una sua versione nuova (Impostazioni → Aggiornamenti di OptLife: *avvisa* o *da solo*).
Scarica l'installatore dalle release di questo repository, lo confronta con lo SHA-256 pubblicato e non aggiorna mai durante un gioco.

## Disinstallazione

Impostazioni → App → OptLife → Disinstalla (oppure *Disinstalla OptLife* nel menu Start). Ti chiede se tenere storico e impostazioni.

## Riga di comando

```powershell
optlife status --json        # cosa non va adesso
optlife findings             # i problemi aperti
optlife events --since 7d    # cosa è successo
optlife report --days 7      # la settimana a parole
```

Ogni comando di lettura accetta `--json`.

## Compilare dal codice

Vedi [docs/DEVELOPING.md](docs/DEVELOPING.md) (in inglese).

## Licenza

[MIT](LICENSE). I contributi sono benvenuti: vedi [CONTRIBUTING.md](CONTRIBUTING.md). Problemi di sicurezza: [SECURITY.md](SECURITY.md).
