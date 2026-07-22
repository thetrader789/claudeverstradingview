#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// ═══════════════════════════════════════════════════════════════════════════
// Rejection Swing Failure — Top Down (1H ref)
// Transcription NinjaTrader 8 du script Pine v6
// « Rejection_Swing_Failure_TopDown.pine » (RSF-TD 1H).
//
// Principe, en deux temps :
//  1. TOP DOWN (1H) — chaque swing 1H confirmé devient un niveau de liquidité
//     mémorisé tant que le prix ne l'a pas traversé (« extend until fill »).
//     Quand une bougie 1H balaie le niveau intact le plus proche puis REFERME
//     de l'autre côté (swing failure / SFP), un setup est armé.
//  2. TIMEFRAME DU CHART — la première FVG dans le sens du setup déclenche la
//     décision : entrée si le RR jusqu'à la liquidité visée atteint le minimum,
//     sinon le setup est abandonné. Dans les deux cas il est consommé.
//
// Fidélité à Pine :
//  - Toute la logique est évaluée sur la série primaire, comme en Pine : la
//    série 1H (ajoutée via AddDataSeries) n'est lue qu'à travers Highs[1] etc.
//    Une « nouvelle bougie 1H clôturée » est détectée par le changement de
//    Times[1][0], ce qui reproduit exactement request.security(lookahead_off).
//  - L'ordre des blocs suit celui du script Pine, qui compte : les setups sont
//    évalués AVANT la mitigation des niveaux (le niveau doit être encore en
//    mémoire), et l'armement se fait APRÈS.
//
// Écarts assumés :
//  - Le webhook JSON vers le dispatcher FastAPI n'est pas repris : NinjaTrader
//    passe les ordres directement au broker.
//  - Pine tourne ici en process_orders_on_close = true : l'ordre est rempli à
//    la CLÔTURE de la bougie FVG. NinjaTrader remplit à l'OUVERTURE de la
//    bougie suivante. C'est l'écart le plus visible sur les résultats — voir
//    la note détaillée au-dessus de la section ENTRÉES.
// ═══════════════════════════════════════════════════════════════════════════

namespace NinjaTrader.NinjaScript.Strategies
{
	public class RejectionSwingFailureTopDown : Strategy
	{
		#region Champs persistants

		private TimeZoneInfo tzParis;

		// Niveaux de liquidité 1H non mitigés
		private readonly List<double> liqHighs = new List<double>();
		private readonly List<double> liqLows  = new List<double>();

		// Setup armé : 1 = attente d'une FVG haussière, -1 = baissière
		private int    pendingDir    = 0;
		private double pendingTarget = double.NaN;

		// Détection d'une nouvelle bougie 1H clôturée (équivalent ta.change(time_close))
		private DateTime lastH1Time = DateTime.MinValue;

		// Horodatage de la bougie 1H qui a armé le setup : une entrée ne peut
		// être prise que sur une bougie primaire postérieure.
		private DateTime setupH1Time = DateTime.MinValue;

		// Note : activeTp du script Pine ne servait qu'à tracer le TP visé, tout
		// comme les plots de liquidité 1H. L'affichage n'est pas repris ici,
		// les niveaux étant portés par les ordres eux-mêmes.

		// Objectif d'évaluation atteint : latch définitif (flatten + blocage).
		private bool evalCapReached = false;

		#endregion

		#region Paramètres

		[NinjaScriptProperty]
		[Display(Name = "Stop basé sur l'ATR", Order = 0, GroupName = "1. Risque")]
		public bool UseATRStop { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Stop fixe au-delà de l'extrême FVG (points)", Order = 1, GroupName = "1. Risque")]
		public double StopOffset { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Longueur ATR", Order = 2, GroupName = "1. Risque")]
		public int AtrLen { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Multiple ATR pour le stop", Order = 3, GroupName = "1. Risque")]
		public double AtrMult { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Reward:Risk minimum", Order = 4, GroupName = "1. Risque")]
		public double MinRR { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Contrats par trade", Order = 5, GroupName = "1. Risque")]
		public int Qty { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "TP = liquidité 1H non mitigée la plus proche", Order = 10, GroupName = "2. Take Profit")]
		public bool UseSwingTP { get; set; }

		[NinjaScriptProperty]
		[Range(5, int.MaxValue)]
		[Display(Name = "Swings 1H mémorisés (par côté)", Order = 11, GroupName = "2. Take Profit")]
		public int MaxSwingKeep { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Barres à GAUCHE du swing (1H)", Order = 20, GroupName = "3. Swings 1H")]
		public int SwingSizeL { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Barres à DROITE du swing (1H)", Order = 21, GroupName = "3. Swings 1H")]
		public int SwingSizeR { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Heure de coupure (Paris)", Order = 30, GroupName = "4. Fin de journée")]
		public int EodCutoffHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Minute de coupure", Order = 31, GroupName = "4. Fin de journée")]
		public int EodCutoffMinute { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Réouverture Globex (Paris)", Order = 32, GroupName = "4. Fin de journée")]
		public int EodReopenHour { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Dessiner les signaux sur le graphique", Order = 40, GroupName = "5. Affichage")]
		public bool ShowSignals { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Compte d'évaluation (plafonner le gain cumulé)", Order = 50, GroupName = "6. Évaluation prop firm")]
		public bool EvalAccount { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Plafond de gain cumulé ($)", Order = 51, GroupName = "6. Évaluation prop firm")]
		public double MaxGainEval { get; set; }

		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "Rejection Swing Failure Top Down — transcription NinjaScript du script Pine v6 : rejet de liquidité 1H (SFP) puis entrée sur FVG du timeframe du chart.";
				Name						= "RSF-TD 1H";
				Calculate					= Calculate.OnBarClose;
				EntriesPerDirection			= 1;
				EntryHandling				= EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy= false;
				IsFillLimitOnTouch			= false;
				MaximumBarsLookBack			= MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution			= OrderFillResolution.Standard;
				StartBehavior				= StartBehavior.WaitUntilFlat;
				TimeInForce					= TimeInForce.Gtc;
				TraceOrders					= false;
				RealtimeErrorHandling		= RealtimeErrorHandling.StopCancelCloseIgnoreRejects;
				StopTargetHandling			= StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade			= 20;

				// Valeurs par défaut = celles du script Pine
				UseATRStop			= true;
				StopOffset			= 0.75;
				AtrLen				= 14;
				AtrMult				= 1.5;
				MinRR				= 2.0;
				Qty					= 1;
				UseSwingTP			= true;
				MaxSwingKeep		= 30;
				SwingSizeL			= 12;
				SwingSizeR			= 10;
				EodCutoffHour		= 21;
				EodCutoffMinute		= 30;
				EodReopenHour		= 23;
				ShowSignals			= true;
				EvalAccount			= false;
				MaxGainEval			= 1500;
			}
			else if (State == State.Configure)
			{
				// Série 1H de référence (index 1) : équivalent des appels
				// request.security(syminfo.tickerid, "60", …) du script Pine.
				AddDataSeries(BarsPeriodType.Minute, 60);

				try   { tzParis = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time"); }
				catch { tzParis = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }
			}
		}

		#region Utilitaires

		private DateTime ToParis(DateTime t)
		{
			return TimeZoneInfo.ConvertTime(t, NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo, tzParis);
		}

		// ta.pivothigh(high, left, right) sur la série 1H : la bougie située
		// « right » barres en arrière est un pivot si son extrême dépasse
		// strictement celui de ses voisines. NaN s'il n'y a pas de pivot.
		private double H1PivotHigh()
		{
			if (CurrentBars[1] < SwingSizeL + SwingSizeR)
				return double.NaN;

			double pivot = Highs[1][SwingSizeR];

			for (int i = 1; i <= SwingSizeL; i++)
				if (Highs[1][SwingSizeR + i] >= pivot)
					return double.NaN;

			for (int i = 1; i <= SwingSizeR; i++)
				if (Highs[1][SwingSizeR - i] >= pivot)
					return double.NaN;

			return pivot;
		}

		private double H1PivotLow()
		{
			if (CurrentBars[1] < SwingSizeL + SwingSizeR)
				return double.NaN;

			double pivot = Lows[1][SwingSizeR];

			for (int i = 1; i <= SwingSizeL; i++)
				if (Lows[1][SwingSizeR + i] <= pivot)
					return double.NaN;

			for (int i = 1; i <= SwingSizeR; i++)
				if (Lows[1][SwingSizeR - i] <= pivot)
					return double.NaN;

			return pivot;
		}

		// Niveau de liquidité non mitigé le plus proche au-dessus / en dessous
		private double NearestSwingAbove(double level)
		{
			double nearest = double.NaN;
			foreach (double p in liqHighs)
				if (p > level && (double.IsNaN(nearest) || p < nearest))
					nearest = p;
			return nearest;
		}

		private double NearestSwingBelow(double level)
		{
			double nearest = double.NaN;
			foreach (double p in liqLows)
				if (p < level && (double.IsNaN(nearest) || p > nearest))
					nearest = p;
			return nearest;
		}

		#endregion

		protected override void OnBarUpdate()
		{
			// Toute la logique vit sur la série primaire, comme en Pine.
			if (BarsInProgress != 0)
				return;

			if (CurrentBars[0] < Math.Max(BarsRequiredToTrade, 3) || CurrentBars[1] < SwingSizeL + SwingSizeR + 1)
				return;

			double positionSize = Position.MarketPosition == MarketPosition.Long  ?  Position.Quantity
								: Position.MarketPosition == MarketPosition.Short ? -Position.Quantity
								: 0;

			// ═══ Fin de journée (heure de Paris) ═════════════════════════════
			// Les builtins d'heure de Pine sont dans le fuseau de l'exchange
			// (America/Chicago pour le CME) : la conversion explicite vers Paris
			// est indispensable, un seuil « 21h30 » y tomberait sinon en pleine
			// nuit européenne.
			DateTime paris		= ToParis(Time[0]);
			int minutesParis	= paris.Hour * 60 + paris.Minute;
			bool eodWindow		= minutesParis >= EodCutoffHour * 60 + EodCutoffMinute
							   && minutesParis <  EodReopenHour * 60;

			// Filet de sécurité : une position encore ouverte au démarrage d'une
			// nouvelle journée de trading (jour férié, trou de données) est
			// soldée sans attendre.
			bool newTradingDay	= Bars.IsFirstBarOfSession;
			bool eodFlatten		= eodWindow || (newTradingDay && positionSize != 0);

			// ═══ Nouvelle bougie 1H clôturée ? ═══════════════════════════════
			bool h1NewBar = Times[1][0] != lastH1Time;

			if (h1NewBar)
			{
				lastH1Time = Times[1][0];

				double h1Open		= Opens[1][0];
				double h1High		= Highs[1][0];
				double h1Low		= Lows[1][0];
				double h1Close		= Closes[1][0];
				double h1ClosePrev	= Closes[1][1];
				bool   h1Red		= h1Close < h1Open;
				bool   h1Green		= h1Close > h1Open;

				// (a) Nouveau pivot 1H confirmé → empilé comme liquidité
				double ph = H1PivotHigh();
				double pl = H1PivotLow();

				if (!double.IsNaN(ph))
				{
					liqHighs.Add(ph);
					if (liqHighs.Count > MaxSwingKeep)
						liqHighs.RemoveAt(0);
				}
				if (!double.IsNaN(pl))
				{
					liqLows.Add(pl);
					if (liqLows.Count > MaxSwingKeep)
						liqLows.RemoveAt(0);
				}

				// (b) Référence du setup : liquidité la plus proche au-delà de la
				// clôture 1H PRÉCÉDENTE — le niveau était donc encore intact
				// avant cette bougie (le filtre « fresh breakout » est intégré).
				double nearestLiqHigh = NearestSwingAbove(h1ClosePrev);
				double nearestLiqLow  = NearestSwingBelow(h1ClosePrev);

				// ═══ Setup : balayage puis rejet du niveau (SFP) ═════════════
				bool bearishSetup = !double.IsNaN(nearestLiqHigh) && h1High > nearestLiqHigh
								 && h1Close < nearestLiqHigh && h1Red && !eodFlatten;
				bool bullishSetup = !double.IsNaN(nearestLiqLow) && h1Low < nearestLiqLow
								 && h1Close > nearestLiqLow && h1Green && !eodFlatten;

				// (c) Mitigation « extend until fill » : tout niveau traversé par
				// la bougie 1H est consommé — balayé-rejeté ou accepté, sa
				// liquidité est prise. Après la détection du setup, qui a besoin
				// du niveau encore présent.
				for (int i = liqHighs.Count - 1; i >= 0; i--)
					if (h1High >= liqHighs[i])
						liqHighs.RemoveAt(i);

				for (int i = liqLows.Count - 1; i >= 0; i--)
					if (h1Low <= liqLows[i])
						liqLows.RemoveAt(i);

				// ═══ Armement du setup ═══════════════════════════════════════
				if (bearishSetup)
				{
					pendingDir    = -1;
					pendingTarget = nearestLiqLow;
					setupH1Time   = Times[1][0];

					if (ShowSignals)
						Draw.TriangleDown(this, "setupS" + CurrentBar, true, 0, High[0] + 2 * TickSize, Brushes.Red);
				}

				if (bullishSetup)
				{
					pendingDir    = 1;
					pendingTarget = nearestLiqHigh;
					setupH1Time   = Times[1][0];

					if (ShowSignals)
						Draw.TriangleUp(this, "setupL" + CurrentBar, true, 0, Low[0] - 2 * TickSize, Brushes.Green);
				}
			}

			// ═══ FVG sur le timeframe du chart ═══════════════════════════════
			bool bullFVG = Low[0]  > High[2];
			bool bearFVG = High[0] < Low[2];

			double fvgHighestHigh	= Math.Max(High[0], Math.Max(High[1], High[2]));
			double fvgLowestLow		= Math.Min(Low[0],  Math.Min(Low[1],  Low[2]));
			double stopDistance		= UseATRStop ? ATR(AtrLen)[0] * AtrMult : StopOffset;

			// Une entrée ne peut se faire que sur une bougie postérieure à la
			// bougie 1H qui a armé le setup (le script Pine ne voit la bougie 1H
			// close qu'à partir de la bougie suivante du chart).
			bool apresSetup = Time[0] > setupH1Time;

			// ═══ Objectif d'évaluation atteint → flatten + blocage définitif ══
			// Plafond de gain CUMULÉ (réalisé + latent) pour un compte d'éval :
			// une fois franchi, on solde la position de CE compte et plus aucune
			// entrée n'est prise (verrouillage définitif). Les autres comptes
			// continuent de trader indépendamment.
			if (EvalAccount && !evalCapReached)
			{
				double cumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
				if (positionSize != 0)
					cumProfit += Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]);

				if (cumProfit >= MaxGainEval)
				{
					evalCapReached = true;
					if (positionSize > 0) ExitLong("Objectif éval", "Long");
					if (positionSize < 0) ExitShort("Objectif éval", "Short");
				}
			}

			// ═══ ENTRÉES ═════════════════════════════════════════════════════
			// Écart avec Pine : le script tourne en process_orders_on_close, donc
			// l'entrée est remplie AU CLOSE de la bougie FVG. NinjaTrader remplit
			// l'ordre marché à l'OUVERTURE de la bougie suivante. Le SL, le TP et
			// le RR restent calculés sur la clôture de la bougie FVG, exactement
			// comme en Pine : seul le prix d'entrée réel peut différer, ce qui
			// décale légèrement le RR effectif par rapport au backtest
			// TradingView.
			if (positionSize == 0 && pendingDir == 1 && bullFVG && apresSetup && !eodFlatten && !evalCapReached)
			{
				double entryPrice	= Close[0];
				double stopPrice	= fvgLowestLow - stopDistance;
				double target		= UseSwingTP ? NearestSwingAbove(entryPrice) : pendingTarget;
				double risk			= entryPrice - stopPrice;
				double reward		= target - entryPrice;
				double rr			= risk > 0 ? reward / risk : double.NaN;

				if (risk > 0 && !double.IsNaN(target) && rr >= MinRR)
				{
					SetStopLoss("Long", CalculationMode.Price, stopPrice, false);
					SetProfitTarget("Long", CalculationMode.Price, target);
					EnterLong(Qty, "Long");
				}

				// Le setup est consommé, que le trade ait été pris ou non.
				pendingDir = 0;
			}

			if (positionSize == 0 && pendingDir == -1 && bearFVG && apresSetup && !eodFlatten && !evalCapReached)
			{
				double entryPrice	= Close[0];
				double stopPrice	= fvgHighestHigh + stopDistance;
				double target		= UseSwingTP ? NearestSwingBelow(entryPrice) : pendingTarget;
				double risk			= stopPrice - entryPrice;
				double reward		= entryPrice - target;
				double rr			= risk > 0 ? reward / risk : double.NaN;

				if (risk > 0 && !double.IsNaN(target) && rr >= MinRR)
				{
					SetStopLoss("Short", CalculationMode.Price, stopPrice, false);
					SetProfitTarget("Short", CalculationMode.Price, target);
					EnterShort(Qty, "Short");
				}

				pendingDir = 0;
			}

			// ═══ Coupure fin de journée ══════════════════════════════════════
			// Flatten forcé, et aucun setup ne reste armé pour la fin de séance.
			if (eodFlatten)
			{
				if (positionSize > 0) ExitLong("EOD Flatten", "Long");
				if (positionSize < 0) ExitShort("EOD Flatten", "Short");

				pendingDir = 0;
			}
		}
	}
}
