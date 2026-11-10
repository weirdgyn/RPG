# Manuale del client — RPG Cyberpunk 2020

Guida all'uso del client per **giocatori** e **master**. Il gioco è una versione testuale e multigiocatore di Cyberpunk 2020: ci si collega a un server, si sceglie un personaggio, si entra in una partita (*game*) e si gioca con comandi che iniziano per `/`.

> Il manuale descrive lo stato attuale del gioco. Alcune regole sono ancora semplificate (vedi [Cosa non c'è ancora](#cosa-non-cè-ancora)).

---

## 1. Primi passi

### 1.1 Avvio e connessione

Il client si collega a un server con questi parametri, in ordine di priorità:

1. riga di comando: `-h <host>` e `-p <porta>`;
2. file `client.settings.json` accanto al client (vedi `client.settings.example.json`):
   ```json
   { "Host": "127.0.0.1", "Port": 5000 }
   ```
3. valori predefiniti: `127.0.0.1` porta `5000`.

Altre opzioni: `-i` (mostra la sequenza intro), `-f <font>` (font del banner).

### 1.2 Login

All'avvio il client chiede **username** e **password** (la digitazione è nascosta). Lo username è unico: se è già in uso sul server il login viene rifiutato e puoi riprovare. Se non hai un account, chiedilo a chi gestisce il server.

> **Connessione:** se non completi il login entro **30 secondi** dalla connessione il server la chiude; un client che per più di **10 secondi** non legge i messaggi del server viene disconnesso. Una volta fatto il login non c'è un timeout di inattività.

> **Nota:** la connessione è cifrata con TLS e il login richiede username e password: gli account li crea solo l'amministratore dal server (console del server: `adduser <nome>`, `passwd <nome>`, `deluser <nome>`, `users`). Dopo 5 tentativi falliti l'account è bloccato per 5 minuti. Il server usa un certificato autofirmato (`server.pfx`, creato al primo avvio): alla prima connessione il client ne memorizza l'impronta SHA-256 in `known_servers.json` (cartella ApplicationData, sottocartella `RPG`) e la confronta a ogni collegamento successivo; se cambia, rifiuta la connessione. Per prove in rete locale server e client possono essere avviati entrambi con `--no-tls` (traffico in chiaro).

### 1.3 Il percorso tipico

```
login all'avvio  →  /create (oppure /link)  →  /join  →  gioca
```

1. **Crea** un personaggio da un archetipo: `/create Solo Razor`.
2. **Collegati** a un personaggio esistente: `/link Razor`.
3. **Entra** in una partita: `/join <game> [master|player]`.
4. Gioca con i comandi delle sezioni seguenti.

Con `/help` si vede in ogni momento l'elenco dei comandi.

---

## 2. Concetti base

| Termine | Significato |
|---|---|
| **Personaggio** (*character*) | La scheda con statistiche, abilità, armi, armatura e salute. Si salva sul server. |
| **Giocatore** (*player*) | Chi controlla un personaggio. |
| **Master** | Chi conduce la partita: crea il game, gestisce gli NPC e le prove collettive, avvia il combattimento. |
| **Game** | Una partita. Ogni utente può stare in un solo game alla volta. |
| **NPC** | Un personaggio controllato dal master (una scheda come le altre, usata dal master). |
| **TN** | *Target Number*: il numero da raggiungere o superare con il tiro. |

### Statistiche, abilità, tiri

- Le statistiche sono **INT, REF, TECH, COOL, ATTR, MA, BODY, EMP**.
- Ogni abilità è legata a una statistica (per esempio *Pistola* → REF). Con `/skills` vedi l'elenco delle abilità e la statistica associata.
- Il tiro base è **statistica + livello dell'abilità + penalità ferite + d10**.
- Il d10 è **esplosivo**: se esce 10 si ritira e si somma, finché non esce altro.
- Un **1 naturale** sul primo dado è un **fallimento critico**: fallisce sempre, anche se il totale supererebbe il TN.

### Un personaggio, un utente

Una scheda può essere collegata a un solo giocatore alla volta, oppure essere un NPC di un solo game. Non è possibile usare la stessa scheda in due modi insieme. Il collegamento (`/link`) resta valido anche se esci dal game; si scioglie con `/unlink`, da usare solo fuori dal game.

### Il master

Il ruolo di master si prende entrando in un game con `/join <game> master`: se il game non esiste viene creato, se esiste e non ha un master lo diventi. Un game ha un solo master. Il master **non può** collegarsi a un personaggio con `/link` (usa gli NPC) e non può assumere il ruolo di master se ha già una scheda collegata: se non sei in un game usa `/unlink`; se sei in un game esci prima con `/leave`, poi usa `/unlink`.

---

## 3. Manuale del giocatore

### 3.1 Comandi generali

| Comando | Cosa fa |
|---|---|
| `/help` | Mostra l'elenco dei comandi. |
| `/help [comando]` | Mostra informazioni su un comando specifico. |
| `/ping` | Misura la latenza con il server. |
| `/exit` | Si disconnette ed esce. |
| `/connect [host] [porta]` | Ritenta la connessione dopo che il server è caduto: senza parametri riusa l'ultimo server, altrimenti host e/o porta indicati. Rifiutato se sei già connesso. Non fa login, `/link` o `/join` automatici: dopo il collegamento si riparte dal login. Senza connessione sono accettati solo `/connect`, `/help` e `/exit`. |
| `/whoami` | Mostra utente, ruolo, game, personaggio collegato (HP, posizione, MA, arma e colpi). |
| `/list [game]` | Elenca i giocatori online, o quelli di un game. |
| `/games` | Elenca i game attivi. |
| `/characters` | Elenca le schede salvate sul server con il loro stato (libera, in uso, NPC di un game). |
| `/archetypes` | Elenca gli archetipi disponibili sul server (da usare con `/create`). |
| `/chat "messaggio" [utente]` | Messaggio a tutti, o privato se indichi un utente. Il messaggio va tra virgolette. |

### 3.2 Personaggio

| Comando | Cosa fa |
|---|---|
| `/create <archetipo> <handle>` | Crea un personaggio da un archetipo, con statistiche leggermente variate a caso. Archetipi: **Solo, Netrunner, Nomad, Rocker, Fixer, Corporate, Techie, TechMed, Cop, Media**. |
| `/link <personaggio>` | Ti collega a una scheda esistente. Se sei già in un game, esci prima con `/leave`: cambiare scheda durante una partita non è supportato. |
| `/unlink` | Scioglie il collegamento con il personaggio corrente. È inibito dentro un game: esci prima con `/leave`. |
| `/status` | Mostra la scheda del personaggio collegato: statistiche, salute, armi e munizioni, armatura, cyberware. |
| `/skills` | Elenco abilità con statistica associata e il tuo pool di tiro. |

Un archetipo definisce abilità, armi, munizioni, armatura e cyberware iniziali. Alcune armi ad area (granate) sono presenti solo in certi archetipi (per esempio Solo e Nomad): se la tua scheda non ne ha, `/throw` risponde che non hai armi ad area.

### 3.3 Partecipare a un game

| Comando | Cosa fa |
|---|---|
| `/join <game> [player]` | Entra in un game esistente. **Devi aver già collegato un personaggio** (`/link`, oppure `/create` e poi `/link`): dentro un game `/link` e `/unlink` sono vietati. Se sei già in un game, esci prima con `/leave`. |
| `/leave` | Esce dal game corrente. Il personaggio resta collegato. |
| `/game` (alias `/gamestatus`) | Stato del game: master, partecipanti con salute, posizione e stordimento, NPC, combattimento, check aperto, azioni libere. |

Gli NPC compaiono nell'elenco con il loro stato qualitativo (per esempio "Seria"), ma senza punti ferita numerici.

`/game` mostra anche il **contatore delle azioni libere**: quante azioni tra `/skill`, `/roll`, `/attack` e `/move` sono state compiute da PG e NPC **fuori dal combattimento** dall'ultimo avvio del combattimento (si azzera a ogni `/combat start`). Accanto c'è la **stima delle round di gruppo**: `azioni libere / (2 × numero di personaggi)`, cioè quanti round di combattimento sarebbero serviti al gruppo per compiere le stesse azioni (2 = un movimento e un'azione per personaggio a round). Contano le azioni eseguite: un movimento bloccato o un attacco rifiutato dalle regole non incrementano il contatore. I `/roll` del master non contano (non sono l'azione di un PG).

### 3.4 Prove e tiri

| Comando | Cosa fa |
|---|---|
| `/skill <abilità> [TN]` | Prova di abilità. Il TN predefinito è 15. L'abilità deve esistere (vedi `/skills`). |
| `/roll <espressione> [motivo]` | Tiro di dadi libero, visibile a tutto il game. Esempi: `/roll 2d6+3 Danno`, `/roll 3d6!` (con `!` i dadi esplodono). |
| `/init` | Tiro di iniziativa: REF + CombatSense (solo per i Solo) + bonus cyberware + d10. |
| `/activate [oggetto]` | Attiva un oggetto con scheda di attivazione (oggi il cyberware): vedi sotto. Senza nome va bene solo se ne hai uno. |

**Oggetti ad attivazione.** Un cyberware può essere *Passive* (sempre attivo, nessun comando) oppure *Active*: i suoi effetti partono solo con `/activate`. Per ogni oggetto la scheda dice:
- **costo**: 0 (attivazione gratuita) o 1 (consuma l'azione del turno). In combattimento si attiva solo **nel proprio turno** (anche se gratuita); fuori dal combattimento sempre;
- **prova** (facoltativa): un'abilità e una difficoltà (15 se non indicata), indipendenti dal costo. Se la prova fallisce gli effetti non partono, ma l'azione, se richiesta, è comunque spesa;
- **durata**: come per `/bonus` (`manual`, `combat`, `r<N>`, `u<N>`, `d<N>`), anche con i dadi (es. `r1d6`: i round si tirano all'attivazione);
- **effetti**: elenco di chiavi e valori, come `/bonus` (es. `INIT +3`).

L'attivazione è un annuncio per tutto il game con il pannello di calcolo (prova, durata tirata, effetti). Riattivare un oggetto già attivo ne **rinnova** gli effetti. Gli effetti sono dello stato della partita (vedi `/game`) e servono quindi un game aperto. I limiti d'uso (es. una volta al giorno) arriveranno con il tempo di gioco (`/day`).

**Quando il master ha aperto un check** (vedi 4.3) il tuo `/skill` o `/init` corrispondente viene registrato per lo spoglio: ricevi solo la conferma *"tiro registrato"* e i risultati escono tutti insieme quando hanno tirato tutti. Vale il **primo** tiro. Il TN non ti viene rivelato.

### 3.5 Movimento

`/move <X> <Y> [Z]` sposta il personaggio sulla mappa 3D.

- Ogni spostamento può essere lungo al massimo **MA metri** (la tua statistica Movimento), in distanza 3D dalla posizione attuale.
- La destinazione deve stare dentro la mappa e non può essere un ostacolo.
- Durante un combattimento MA è il totale del turno: più `/move` si sommano (vedi 5.1). Gli effetti attivi con chiave `MOVE` o `STAT:MA` (e i bonus cyberware con la stessa chiave) cambiano l'MA disponibile, mai sotto zero; il valore con gli effetti compare nei messaggi di movimento, nel turno, in `/game` e in `/whoami`.
- Se ometti la `Z`, l'azione resta sulla **quota corrente** del personaggio (non va a 0): da `(0,0,2)` un `/move 2 1` porta a `(2,1,2)`. Vale anche per `/throw` e per `/as <npc> move` / `/as <npc> throw` (che usano la quota corrente dell'NPC).


### 3.6 Combattimento

| Comando | Cosa fa |
|---|---|
| `/attack <bersaglio> [arma\|abilità]` | Attacca un personaggio dello stesso game. Senza specificare usa l'arma in pugno; puoi indicare un'arma per nome, Id o abilità. |
| `/reload [munizioni] [arma]` | Ricarica l'arma in pugno (o quella indicata). Opzionalmente scegli il tipo di munizioni, anche con una parte del nome: `/reload ap`. |
| `/throw <X> <Y> [Z] [arma]` | Lancia un'arma ad area (granata) verso un punto. |
| `/heal <personaggio>` | Pronto soccorso: stabilizza un ferito **in Mortale** (Pronto Soccorso/Tecnomedicina, TN = danno subito dal ferito). Devi essere a **2,5 m o meno**. Consuma l'azione del turno, anche se il tentativo fallisce. |
| `/next` (alias `/endturn`) | Termina il tuo turno. |
| `/combat status` | Mostra round, turno, movimento e azione rimasti, ordine di gioco. |

Le regole del combattimento sono nella sezione 5.

---

## 4. Manuale del master

### 4.1 Aprire e condurre un game

1. `/join <game> master` crea il game (o ne prende la guida se è libero).
2. I giocatori entrano con `/join <game>`, dopo aver collegato il loro personaggio (senza personaggio il `/join` viene rifiutato; il master non ne ha uno).
3. Con `/game` vedi in qualsiasi momento tutti i partecipanti, gli NPC (con i punti ferita numerici), il combattimento e i check aperti.
4. Con `/heal <personaggio>` (giocatori o NPC) curi **completamente** il bersaglio e ne azzeri morte, stordimento e stabilizzazione. Con `/heal <personaggio> <caselle>` restituisci solo quel numero di caselle (da 1 a 1000): un morto riparte dal limite della pista (0 caselle, Mortale 6), e stordimento e stabilizzazione restano come sono. È gratuito, senza tiro e non conta come azione. Per far curare un NPC in gioco usa `/as <npc> heal <personaggio>` (stesse regole dei giocatori: tiro, 2,5 m, azione del turno).
5. Con `/teleport <X> <Y> <Z> <personaggio> [altri...]` sposti istantaneamente personaggi o NPC del game nel punto indicato della mappa (destinazione valida e priva di ostacoli). Non conta come azione e non consuma movimento del turno.
6. Con `/bonus` applichi **bonus e malus di circostanza** (effetti) ai personaggi del game:
   - `/bonus <chiave> <±N> [durata] <bersagli>`: es. `/bonus REF +2 r3 morgan molly`, `/bonus INIT -2 combat party`, `/bonus Pronto Soccorso +3 u1 morgan` (anche le abilità con lo spazio si scrivono normalmente).
   - **Chiave**: `ALL` (prove di abilità, colpire, iniziativa), `INIT`, `HIT`, `DAMAGE`, `MOVE` (movimento), `STAT:<stat>` (o solo `REF`, `TECH`...) oppure `SKILL:<abilità>` (o solo il nome dell'abilità). Valore intero da -10 a +10, diverso da 0.
   - **Durata** (facoltativa, di default fino a rimozione): `manual`, `combat` (fino a fine combattimento), `r<N>` (N round: un effetto a 3 round applicato nel round 2 vale per i round 2, 3 e 4), `u<N>` (N usi: si consuma nei tiri in cui interviene), `d<N>` (giorni, per il futuro `/day`).
   - **Bersagli**: handle di PG o NPC separati da spazi o virgole, oppure `party` (tutti i PG), `npc`, `all`. Tutto o niente: se un bersaglio non esiste non cambia nulla.
   - Lo stesso effetto del master sullo stesso personaggio e sulla stessa chiave si **sostituisce**; effetti di origini diverse si sommano. Massimo 20 effetti per personaggio.
   - `/bonus clear <bersagli>` toglie i bonus del master ai bersagli; `/bonus list [bersagli]` elenca gli effetti attivi (senza bersagli: tutti).
   - Ogni termine compare nei pannelli di calcolo (es. "+2 master"). Gli effetti sono **stato della partita**: non finiscono nella scheda e si perdono quando il game si chiude. Gli effetti a round scadono all'inizio del round successivo all'ultimo e tutti quelli a round o `combat` cadono con la fine del combattimento (l'annuncio li elenca). Gli effetti attivi si vedono in `/game` (gli NPC solo al master).

Se il master esce o si disconnette, il ruolo torna libero e i giocatori vengono avvisati. Un game senza partecipanti viene chiuso.

### 4.2 Gli NPC

Un NPC è una normale scheda personaggio che il master usa per un game. Si crea come qualsiasi personaggio (`/create`), poi si aggiunge al game.

| Comando | Cosa fa |
|---|---|
| `/npc add <handle>` | Usa la scheda esistente come NPC del game. La scheda non deve essere collegata a un giocatore né essere NPC altrove. |
| `/npc list` | Elenca gli NPC del game con salute e posizione. |
| `/npc status <handle>` | Scheda dell'NPC. |
| `/npc remove <handle>` | Toglie l'NPC dal game e dagli eventuali check aperti (la scheda torna libera). |
| `/as <npc> <comando> ...` | Esegue un comando **per conto** di un NPC. |

Comandi consentiti con `/as`:

```
/as <npc> attack <bersaglio> [arma|abilità]
/as <npc> skill <abilità> [TN]
/as <npc> move <X> <Y> [Z]
/as <npc> init
/as <npc> reload [munizioni] [arma]
/as <npc> throw <X> <Y> [Z] [arma]
/as <npc> heal <personaggio>
/as <npc> activate [oggetto]
```

> Per `/as move` e `/as throw` la `Z` è la **quota corrente dell'NPC** se la ometti: indicala solo quando vuoi cambiare piano.

Gli NPC seguono le stesse regole dei giocatori: gittata, MA, munizioni, turno di combattimento. Durante un combattimento il master deve prima passare il turno all'NPC con `/next`.

### 4.3 Prove collettive: `/check`

Il master apre una prova per più personaggi insieme e il server ne fa lo spoglio quando tutti hanno tirato.

| Comando | Cosa fa |
|---|---|
| `/check skill <abilità> <TN> [personaggi…]` | Prova di abilità contro un TN. |
| `/check init [personaggi…]` | Tiro di iniziativa. `/init` del master equivale a `/check init` per tutti. |
| `/check status` | Chi ha tirato e chi manca. |
| `/check close` | Chiude subito lo spoglio con i tiri presenti. |
| `/check cancel` | Annulla il check. |

Come funziona:

- **Iscritti**: se non indichi personaggi, entrano tutti quelli del game (giocatori e NPC). I personaggi **morti** vengono esclusi. I nomi si separano con spazi o virgole.
- **NPC**: tira il server all'apertura, non serve fare nulla.
- **Giocatori**: ricevono un avviso (senza il TN) e tirano con `/skill <abilità>` oppure `/init`. Vale il primo tiro; per loro compare solo la conferma.
- **Spoglio** (a tutto il game, quando la lista è piena o con `/check close`):
  - *abilità*: tabella con tiro e successo, fallimento o fallimento critico contro il TN;
  - *iniziativa*: lista ordinata. A parità vince il REF più alto, poi chi ha tirato prima. Questo ordine è quello usato dal combattimento a turni.
- Un solo check alla volta. Chi esce dal game o si disconnette viene tolto dal check: se mancava solo lui, lo spoglio parte con gli altri.
- Se rimuovi un NPC durante un check aperto, la sua iscrizione viene tolta: se era l'ultimo mancante, lo spoglio parte con gli altri.
- Un `/skill` su un'abilità diversa da quella del check resta una prova libera.
- Non si può aprire un nuovo `/check init` mentre un combattimento è in corso.

### 4.4 Combattimento a turni

| Comando | Cosa fa |
|---|---|
| `/check init` | 1. Calcola l'ordine di iniziativa. |
| `/combat start` | 2. Avvia il combattimento dal primo dell'ordine (round 1) e azzera il contatore delle azioni libere. |
| `/next` | Passa al turno successivo (il master può farlo sempre). |
| `/combat status` | Round, turno e budget rimasto. |
| `/combat end` | Termina il combattimento: movimenti e azioni tornano liberi. |

Regole dei turni:

- Agisce **solo chi è di turno** (anche gli NPC: il master usa `/as <npc> …` quando tocca a loro).
- Ogni turno si ha **movimento fino a MA metri in totale** (più `/move` si sommano) e **una sola azione**: `/attack`, `/throw` o `/reload`.
- Restano sempre liberi: `/skill`, `/init`, `/roll`, la chat, le risposte ai `/check`, `/status`, `/game`.
- Un'azione rifiutata dalle regole (fuori portata, caricatore vuoto) **non** consuma l'azione.
- Il round aumenta a ogni giro completo. I personaggi morti o usciti dal game vengono saltati. Chi è stordito tenta di riprendersi a inizio turno; se fallisce, il turno passa al successivo. Se esce chi era di turno, il turno passa al successivo e il game riceve l'annuncio.
- Fuori dal combattimento non ci sono limiti di turno.

---

## 5. Regole del combattimento

### 5.1 Movimento e turni

Vedi 3.5 e 4.4. In combattimento, per esempio, con MA 7 un giocatore può fare un `/move` da 4 m e poi uno da 3 m; un terzo spostamento nello stesso turno è rifiutato.

### 5.2 Attacco a distanza

Tiro per colpire: **statistica + livello abilità + penalità ferite + modificatore arma + bonus cyberware (HIT) + d10 esplosivo**.

- **Modificatore arma** (*AccuracyBonus*): difettosa −1, standard 0, ottima +1.
- **TN** in base alla distanza, scalato sulla **gittata** *R* dell'arma:

| Distanza | TN |
|---|---|
| fino a 1 m (a bruciapelo) | 10 |
| fino a R/4 | 15 |
| fino a R/2 | 20 |
| fino a R | 25 |
| fino a 2R | 30 |
| oltre 2R | fuori portata |

Per una pistola con gittata 50 m: 1 m, 12,5 m, 25 m, 50 m, 100 m.

- Un **1 naturale** è sempre un mancato.
- Ogni colpo sparato consuma un proiettile dal caricatore, anche se manca. Con il caricatore vuoto serve `/reload`.

### 5.3 Attacco in mischia

Il bersaglio deve essere a **2,5 m o meno**. Il TN è **10 + REF del bersaglio + Schivare + penalità ferite del bersaglio**.

### 5.4 Zona colpita, armatura, corporatura

Se l'attacco è a segno si tira la **locazione** (d10):

| d10 | Locazione | Danno |
|---|---|---|
| 1 | Testa | ×2 (dopo l'armatura) |
| 2–4 | Torso | ×1 |
| 5 | Braccio destro | ×1 |
| 6 | Braccio sinistro | ×1 |
| 7–8 | Gamba destra | ×1 |
| 9–10 | Gamba sinistra | ×1 |

Il danno si calcola in quest'ordine:

1. **Armatura**: si sottrae l'**SP** del pezzo che protegge la zona (casco, giubbotto, braccia, gambe). Se l'armatura viene penetrata, il suo SP cala di 1 (ablazione). Se assorbe tutto, nessun effetto.
2. **Moltiplicatore** della zona (Testa ×2).
3. **BTM** (*Body Type Modifier*): riduzione data dalla corporatura (BODY) del bersaglio, solo se il colpo è passato.

| BODY | 2 o meno | 3–4 | 5–7 | 8–9 | 10 | 11 o più |
|---|---|---|---|---|---|---|
| Riduzione | 0 | 1 | 2 | 3 | 4 | 5 |

Un colpo che penetra l'armatura fa **almeno 1** danno.

### 5.5 Munizioni e ricarica

- Il caricatore ha una capacità (per esempio 12 colpi); le munizioni di riserva sono nell'inventario, divise per calibro e tipo.
- `/reload` ricarica fino a capienza. I colpi rimasti nel caricatore tornano nella loro pila prima di caricare il nuovo tipo, quindi cambiare munizioni non fa perdere colpi.
- `/reload ap` carica munizioni **perforanti**: dimezzano l'SP dell'armatura (arrotondato per eccesso). Altri tipi possono avere un modificatore al danno.
- La ricarica è un'azione del turno.

### 5.6 Armi ad area: `/throw`

- `/throw <X> <Y> [Z] [arma]` lancia verso un punto della mappa. Se ometti la Z usa quella del tuo personaggio; con `/as <npc> throw` indica invece sempre la Z dell'NPC.
- **Tiro di lancio**: statistica + livello **Atletica** + penalità ferite + d10 esplosivo, contro lo stesso TN a fasce degli attacchi a distanza, scalato sulla **gittata di lancio** dell'arma. Oltre la gittata il lancio è rifiutato.
- **Se fallisce**, la granata **devia** di 1d10 metri in direzione casuale (il doppio con un fallimento critico) ed esplode lì.
- **Danno**: colpisce tutti i personaggi del game (giocatori e NPC, compreso chi lancia) entro il **raggio**. Per le granate a fasce il danno dipende dalla distanza dal centro: per esempio fino a 2 m 5d6, 2–5 m 3d6, 5–10 m 2d6. Si tira una volta per fascia e tutti nella stessa fascia subiscono lo stesso danno, ridotto da armatura del torso e BTM.
- Ogni lancio consuma un pezzo (`CurrentAmmo` conta i pezzi rimasti).
- Per non essere colpiti conviene stare **fuori dal raggio** dal punto d'impatto e tenere conto della possibile deviazione.

### 5.7 Salute e ferite

Tutti i personaggi hanno una scheda ferite di **40 caselle**; ogni danno ne toglie. Lo stato dipende dal **danno subito**:

| Danno subito | Stato | Penalità ai tiri |
|---|---|---|
| 0 | Illeso | 0 |
| 1–4 | Leggera | 0 |
| 5–8 | Seria | −2 |
| 9–12 | Critica | −4 |
| 13–16 | Mortale 0 | −6 |
| 17–20 | Mortale 1 | −6 |
| 21–24 | Mortale 2 | −6 |
| 25–28 | Mortale 3 | −6 |
| 29–32 | Mortale 4 | −6 |
| 33–36 | Mortale 5 | −6 |
| 37–40 | Mortale 6 | −6 |
| 41+ | Morto | — |

Il BODY non cambia le caselle: agisce sul BTM. La penalità si applica ai tiri di attacco, di abilità e di lancio, e alla difesa in mischia; il tiro di iniziativa non la applica.

**Salvezza contro la morte.** Chi entra in **Mortale** (13+ danni) tira un **d10 inferiore a BODY − livello Mortale** (Mortale 0 = BODY, Mortale 1 = BODY − 1, … fino a Mortale 6; la soglia non scende sotto 0) all'inizio di ogni suo turno: se fallisce, **muore** (41+ danni, fuori gioco). Un **pronto soccorso** riuscito (`/heal`) **stabilizza** il ferito e sospende il tiro salvezza finché non subisce altri danni, che lo fanno ripartire.

**Stordimento (solo in combattimento).** Dopo un colpo che infligge almeno 1 danno, il bersaglio tira un d10: se il risultato è **inferiore** a `BODY − modificatore di stato`, resiste; altrimenti è stordito. Il modificatore è 0 per le ferite Leggere, 1 per le Serie, 2 per le Critiche, 3 per Mortale 0, 4 per Mortale 1, e così via. Un personaggio stordito non può agire né muoversi; all'inizio del suo turno ritenta il tiro con la stessa soglia e, se fallisce, viene saltato. Se nessuno può agire ma c'è almeno uno stordito, il turno resta a uno di loro, che può solo passare con `/next`; se sono tutti morti, il combattimento termina.

Il danno si accumula fino a riempire le 40 caselle: a **40 danni** il personaggio è all'ultima casella (**Mortale 6**, −6 ai tiri); con **41 danni o più** è **morto** e non può più agire (attacco, lancio, ricarica, movimento e prove sono tutti bloccati). Un morto non può più essere bersaglio di attacchi né subire i danni di un'esplosione; solo il master lo riporta in vita con `/heal`. Lo stato di **stordimento**, invece, è di sessione: non viene salvato nella scheda, non ha effetto fuori dal combattimento e viene azzerato all'avvio e alla fine esplicita del combattimento.

---

## 6. Riferimento rapido dei comandi

| Comando | Chi | Sintesi |
|---|---|---|
| `/help [comando]` | tutti | elenco comandi o guida del singolo comando |
| `/ping` | tutti | latenza |
| `/exit` | tutti | esci |
| `/connect [host] [porta]` | tutti | riconnessione dopo una caduta |
| `/whoami` | tutti | chi sono |
| `/list [game]` | tutti | giocatori online |
| `/games` | tutti | game attivi |
| `/characters` | tutti | schede sul server |
| `/archetypes` | tutti | archetipi per `/create` |
| `/chat "msg" [utente]` | tutti | chat pubblica o privata |
| `/create <archetipo> <handle>` | tutti | nuovo personaggio |
| `/link <personaggio>` | giocatore | collega la scheda |
| `/unlink` | giocatore | scollega la scheda (solo fuori dal game) |
| `/status` | giocatore | scheda del personaggio |
| `/skills` | tutti | abilità e statistiche |
| `/join <game> [master\|player]` | tutti | entra in un game |
| `/leave` | tutti | esci dal game |
| `/game` (`/gamestatus`) | tutti | stato del game |
| `/skill <abilità> [TN]` | giocatore | prova di abilità |
| `/roll <espr> [motivo]` | tutti | tiro di dadi libero |
| `/init` | giocatore | iniziativa (master: come `/check init`) |
| `/activate [oggetto]` | giocatore | attiva un oggetto (cyberware Active) |
| `/move <X> <Y> [Z]` | giocatore | muoviti (max MA) |
| `/teleport <X> <Y> <Z> <pg> [altri]` | master | sposta personaggi/NPC, senza limite di movimento |
| `/bonus <chiave> <±N> [durata] <bersagli>` | master | applica un bonus/malus; `/bonus clear <bersagli>` lo toglie, `/bonus list [bersagli]` li elenca |
| `/attack <bersaglio> [arma]` | giocatore | attacca |
| `/reload [munizioni] [arma]` | giocatore | ricarica |
| `/throw <X> <Y> [Z] [arma]` | giocatore | lancia granata |
| `/heal <personaggio> [caselle]` | giocatore/master | stabilizza (giocatore); cura completa o di N caselle (master) |
| `/next` (`/endturn`) | turno/master | termina il turno |
| `/combat start\|end\|status` | master (status: tutti) | combattimento a turni |
| `/check skill\|init\|status\|close\|cancel` | master | prove collettive |
| `/npc add\|list\|remove\|status` | master | gestione NPC |
| `/as <npc> …` | master | comando per conto di un NPC |

---

## 7. Consigli e problemi comuni

- **"Devi collegarti a un personaggio"**: usa prima `/create` o `/link`.
- **"Devi essere in un game"**: entra con `/join <game>`.
- **"Abilità sconosciuta"**: controlla il nome con `/skills` (maiuscole indifferenti, senza spazi).
- **"Caricatore vuoto"**: `/reload`.
- **"Non è il turno di …"**: durante il combattimento aspetta il tuo turno; `/combat status` dice a chi tocca.
- **"Troppo lontano" su `/move`**: lo spostamento supera MA (o il movimento rimasto nel turno).
- **"Fuori portata"**: l'arma non arriva a quella distanza (oltre il doppio della gittata).
- I messaggi dei tiri dei **check** compaiono solo allo spoglio: è normale non vedere il risultato subito.

---

## Cosa non c'è ancora

Regole previste ma non ancora implementate:

- **passaggio del tempo di gioco e recupero delle ferite nel tempo** (previsti 0,5 caselle/giorno con Pronto Soccorso e 1/giorno con Tecnomedicina, da progettare insieme al comando del master per far scorrere i giorni): oggi `/heal` stabilizza (giocatori e NPC) e il master cura gratuitamente, per intero o di N caselle;
- **mischia come tiro contrapposto**;
- timeout delle connessioni **inattive dopo il login** (il timeout di login e la disconnessione dei client che non leggono sono già attivi).
- **inventario, negozi e oggetti** (`/inventory`, `/buy`, `/pick`);
- **caricamento e valutazione di schede esterne** (`/upload`, `/eval`), con un validatore delle schede;
- **addestramento e ricompense** (`/train`, `/reward`), **bonus** ad attivazione da oggetti (`/activate`), **veicoli** (`/drive`) e cyberpsicosi.
