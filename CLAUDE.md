# CLAUDE.md — thetrader789/claudeverstradingview

## Projet
Système de trading algorithmique automatisé sur NQ/MNQ (Nasdaq futures).
Stratégies Pine Script v6 sur TradingView → alertes webhook → Python/FastAPI → Tradovate REST API → comptes prop firms.

## Stack
- **Stratégies** : Pine Script v6 (TradingView)
- **Webhook dispatcher** : Python 3 + FastAPI (VPS français)
- **Broker API** : Tradovate REST
- **Prop firms** : Lucid Flex, Apex, FundedNext (rotation séquentielle)
- **OS VPS** : Linux (Ubuntu)
- **Repo** : github.com/thetrader789/claudeverstradingview

## Fichiers clés
- `15h30_corrige.pine` — Stratégie principale (session opens, BOS, ATR stops)
- `vps_dispatcher/app.py` — FastAPI webhook receiver
- `vps_dispatcher/state_manager.py` — Gestion état comptes / rotation

## Architecture webhook
TradingView alert → POST /webhook (FastAPI VPS) → state_manager (1 trade/compte/jour, pas de positions corrélées simultanées) → Tradovate REST

## Logique stratégie 15h30_corrige.pine
- **Sessions ciblées** (CET) : Globex, Japon, Londres, New York
- **Fenêtre continuation** : 15 premières minutes après l'open → trade dans sens du BOS
- **Fenêtre reverse** : après 15 min → retour vers fair price / FVG
- **Stop** : ATR-based (système 3 conditions sp/tp)
- **Break-even** : trailing activé à 1:1 R:R
- **Limite** : 1 trade par fenêtre de session
- **Filtres volume** : volFade, volMomS, volMomL
- **Globex gap** : détection intégrée

## Conventions
- Pine Script : version 6 obligatoire (`//@version=6`)
- Nommage variables : camelCase
- Commentaires en français
- Commits en français
- Ne jamais modifier `15h30_corrige.pine` sans backup versionné
- Toujours tester sur compte demo/papier avant prop firm

## Commandes essentielles VPS
```bash
# Démarrer le serveur webhook
uvicorn app:app --host 0.0.0.0 --port 8000

# Voir les logs en live
journalctl -u tradingbot -f

# Redémarrer le service
sudo systemctl restart tradingbot
```

## Règles prop firms (critiques)
- **Pas de positions corrélées simultanées** sur comptes différents (hedging flag)
- **1 trade max par compte par jour** (rotation séquentielle)
- **Pas de trading pendant les news** majeures (NFP, FOMC)
- Lucid Flex : bot trading autorisé explicitement
- Apex : bot trading autorisé

## Workflows
- Toujours commencer par `/review-strategy` avant de modifier une stratégie
- Utiliser `/backtest-check` pour valider la logique avant déploiement
- Utiliser `/deploy` uniquement après validation sur compte papier

## Backtests manuels
Structure dans `backtests_manuels/` :
- Screenshots H1 + M5 par setup
- Notes structurées (entrée, stop, target, résultat, observations)
