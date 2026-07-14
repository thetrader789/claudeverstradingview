# Dispatcher webhook multi-comptes (TradingView → Tradovate)

Reçoit les alertes JSON du script Pine **FP Multi-Marchés 1mn v7** (groupe
d'inputs « Webhook ») et exécute sur des comptes prop firms via Tradovate REST,
avec rotation **1 trade par compte par jour** et interdiction des positions
corrélées simultanées.

## Fichiers

| Fichier | Rôle |
|---|---|
| `app.py` | FastAPI : `/webhook`, `/status`, `/health` |
| `state_manager.py` | Registre de comptes, rotation, limites journalières, corrélation, kill switch |
| `tradovate.py` | Sessions Tradovate par firme, contrats front month, ordres OSO/modify/flatten |
| `accounts.json.example` | Modèle de configuration — copier vers `accounts.json` |

## Installation sur le VPS

```bash
cd /opt/tradingbot           # ou l'emplacement actuel du service
python3 -m venv venv && source venv/bin/activate
pip install -r requirements.txt
cp accounts.json.example accounts.json
nano accounts.json           # comptes réels : account_spec + account_id + qty
```

Secrets dans l'environnement du service (jamais dans accounts.json) —
`/etc/systemd/system/tradingbot.service` :

```ini
[Service]
Environment=WEBHOOK_SECRET=un-secret-long-et-aleatoire
Environment=TRADO_APEX_USER=... TRADO_APEX_PASS=... TRADO_APEX_CID=... TRADO_APEX_SEC=...
Environment=TRADO_LUCID_USER=... TRADO_LUCID_PASS=... TRADO_LUCID_CID=... TRADO_LUCID_SEC=...
ExecStart=/opt/tradingbot/venv/bin/uvicorn app:app --host 0.0.0.0 --port 8000
WorkingDirectory=/opt/tradingbot
Restart=always
```

`cid`/`sec` = clé API Tradovate (Settings → API Access sur le compte de la
firme). `account_id` : `GET /v1/account/list` une fois authentifié, ou le
laisser à 0 et le lire dans les logs de la première erreur.

## Trouver les account_id

```bash
curl -s -X POST https://demo.tradovateapi.com/v1/auth/accesstokenrequest \
  -H 'Content-Type: application/json' \
  -d '{"name":"USER","password":"PASS","appId":"FPDispatcher","appVersion":"1.0","deviceId":"setup","cid":"CID","sec":"SEC"}'
# puis avec le token :
curl -s https://demo.tradovateapi.com/v1/account/list -H 'Authorization: Bearer TOKEN'
```

## Côté TradingView

1. Sur le chart, ouvrir les réglages de « FP Multi-Marchés 1mn » → groupe
   **Webhook** : cocher « Alertes webhook JSON », renseigner le **même secret**
   que `WEBHOOK_SECRET`.
2. Créer UNE alerte sur la stratégie, condition « **Toute fonction alert()** »,
   notification Webhook → `https://<vps>:8000/webhook` (mettre un reverse
   proxy TLS devant, TradingView exige HTTPS sur le port 443/80/8443).
3. Le champ `qty` du payload est indicatif : la taille réelle vient de
   `accounts.json` (`qty.default` ou par racine de symbole).

## Règles appliquées côté serveur

- **1 trade/compte/jour** (jour Europe/Paris, reset automatique à minuit)
- **Rotation séquentielle** : le signal part sur le compte éligible suivant
- **Corrélation** : jamais deux comptes en position sur le même groupe
  (`groupes_correlation` dans la config)
- **Blackout news** : fenêtres dans `blackouts_news` → entrées refusées
- **Kill switch** : `touch PAUSE` dans le dossier → plus aucune entrée ;
  `rm PAUSE` pour reprendre (les positions ouvertes continuent d'être gérées)
- **breakeven** : le stop remonte au prix de fill réel du compte (pas celui
  du payload)
- **flatten** : annule les brackets AVANT de liquider

## Suivi

```bash
curl -s localhost:8000/status | python3 -m json.tool   # état comptes/rotation
journalctl -u tradingbot -f                             # logs live
```

## Checklist avant argent réel

1. `environnement: "demo"` partout, secret factice → tester les 5 événements
   (buy, sell, breakeven, modify_stop, flatten) avec `curl` manuellement.
2. Alerte TradingView branchée sur le demo pendant plusieurs sessions ;
   comparer chaque exécution avec le backtest du chart.
3. Vérifier par écrit les règles d'automatisation de CHAQUE firme avant de
   basculer `environnement: "live"` (supervision exigée chez certaines).
