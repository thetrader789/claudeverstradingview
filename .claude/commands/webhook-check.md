---
description: Vérifie la configuration webhook FastAPI et la logique de rotation des comptes
allowed-tools: Read, Bash
---

1. Lis vps_dispatcher/app.py et vps_dispatcher/state_manager.py
2. Vérifie :
   - Endpoint /webhook reçoit et parse correctement les alertes TradingView
   - state_manager respecte la règle 1 trade/compte/jour
   - Aucune position corrélée simultanée sur comptes différents
   - Gestion des erreurs Tradovate API (timeouts, rejets)
   - Logs suffisants pour debug
3. Liste les risques identifiés
4. Propose les corrections si nécessaire
