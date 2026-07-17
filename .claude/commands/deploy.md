---
description: Checklist de déploiement avant mise en production sur le VPS
allowed-tools: Read, Bash
---

1. Vérifie que les tests ont été faits sur compte papier/demo
2. Checklist prop firms :
   - [ ] Pas de positions ouvertes en cours sur les comptes cibles
   - [ ] Pas de news majeures dans les 2 prochaines heures (NFP, FOMC, CPI)
   - [ ] Limites journalières prop firm vérifiées
3. Rappelle les commandes de déploiement VPS :
   - `git pull origin main`
   - `sudo systemctl restart tradingbot`
   - `journalctl -u tradingbot -f` (vérifier démarrage propre)
4. Confirme que le webhook TradingView pointe vers la bonne URL
5. STOP — demande confirmation explicite avant toute action sur le VPS
