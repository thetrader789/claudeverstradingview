---
description: Vérifie qu'une stratégie est prête pour le backtest et identifie les limites
allowed-tools: Read, Bash
---

1. Lis la stratégie Pine Script cible
2. Rappelle la limite TradingView : ~10 000 bougies sur M1 ≈ 10 jours seulement
3. Vérifie que les paramètres de backtest sont configurés (commission, slippage NQ)
4. Identifie les métriques clés à surveiller : Win rate, Profit factor, Max drawdown, Sharpe
5. Suggère si TradeStation ou QuantConnect serait nécessaire pour plus de données historiques
6. Résume ce qui peut et ne peut PAS être validé avec TradingView seul
