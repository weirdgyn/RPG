# RPG

Gioco di ruolo multiplayer ispirato a Cyberpunk 2020: un server TCP e un client a riga di comando (.NET 8).

## Configurazione del client

Senza altre indicazioni il client si connette a `127.0.0.1:5000`. Per usare un altro server senza
scriverne l'indirizzo nel repository:

1. copia `RPG.Client/client.settings.example.json` in `client.settings.json` (il file è ignorato da Git);
2. imposta `Host` e `Port` (entrambi facoltativi).

Il file viene cercato prima nella cartella da cui lanci il client, poi accanto all'eseguibile.
Gli argomenti `--host` e `--port` hanno la precedenza sul file.
