---
description: Analyse une stratégie Pine Script v6 et identifie les problèmes potentiels
allowed-tools: Read, Bash
---

1. Lis le fichier Pine Script indiqué (ou 15h30_corrige.pine par défaut)
2. Vérifie :
   - Syntaxe Pine Script v6 correcte (`//@version=6`)
   - Logique BOS / CHoCH cohérente
   - Conditions de stop ATR (système sp/tp 3 conditions)
   - Break-even à 1:1 correctement implémenté
   - Limite 1 trade par fenêtre de session respectée
   - Filtres volume (volFade, volMomS, volMomL) actifs
3. Liste les bugs ou incohérences trouvés
4. Propose les corrections avec le code Pine Script corrigé
5. NE MODIFIE PAS le fichier original sans confirmation explicite
