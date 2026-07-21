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
// Opening Range Break Strategy — Asia London
// Transcription NinjaTrader 8 du script Pine v6 « ORB_Asia_London_Strategy.pine ».
//
// Principe : chaque session (heure de Paris) forme un opening range court.
// Après sa clôture, la première bougie qui clôture hors du range déclenche un
// breakout, filtré contre les faux signaux : le niveau ne doit pas être collé
// au haut/bas de la veille sans liquidité (swing) au-delà, et le R:R jusqu'au
// swing cible doit atteindre le minimum. Stop au milieu du range, take-profit
// sur le swing le plus proche au-delà — take-profit qui peut se rapprocher en
// cours de trade si un swing plus proche se confirme.
//
// Fenêtres d'opening range (heure de Paris) :
//   Asie 02:00-02:25 · Londres 09:00-09:30 · New York 15:30-15:45
// L'OR Asie se ferme à 02:25 pour que la première bougie de cassure éligible
// clôture à 02:30 pile.
//
// Fidélité à Pine :
//  - L'ordre des sections suit exactement celui du script Pine, qui compte :
//    les compteurs journaliers sont mis à jour AVANT la détection du breakout,
//    et le TP dynamique s'applique APRÈS l'entrée.
//  - Deux séries de pivots distinctes, comme en Pine : pivotLen (lent) pour le
//    filtre d'entrée et le TP initial, tpPivotLen (rapide) pour le suivi du TP.
//  - Les fenêtres de session sont en Europe/Paris (DST géré), tandis que le
//    flatten de fin de séance est dans le fuseau de l'EXCHANGE — cette
//    différence est voulue et reprise telle quelle.
//
// Écarts assumés :
//  - Pine tourne en process_orders_on_close : l'entrée est remplie à la clôture
//    de la bougie de cassure, NinjaTrader à l'ouverture de la suivante.
//  - Le « fix mèche » de Pine (réémission d'une limite au prix de clôture quand
//    la mèche franchit le TP sans que la clôture y soit) devient ici une sortie
//    au marché, son équivalent le plus proche.
// ═══════════════════════════════════════════════════════════════════════════

namespace NinjaTrader.NinjaScript.Strategies
{
	public enum OrbSessionChoice
	{
		[Description("Asie + Londres")]					AsieLondres,
		[Description("Londres + New York")]				LondresNY,
		[Description("Asie + Londres + New York")]		AsieLondresNY,
		[Description("Londres seule (09:00-09:30)")]	LondresSeule,
		[Description("Asie seule (02:00-02:25)")]		AsieSeule
	}

	public class ORBAsiaLondon : Strategy
	{
		#region Champs persistants

		private TimeZoneInfo tzParis;

		// Opening range de la session en cours
		private double orHigh			= double.NaN;
		private double orLow			= double.NaN;
		private double orMid			= double.NaN;
		private int    orBarCount		= 0;
		private bool   orLocked			= false;
		private DateTime sessionEndTime	= DateTime.MinValue;

		private bool   breakoutDoneToday	= false;
		private int    sessionTradeCount	= 0;
		private bool   sessionWinLock		= false;
		private bool   orLongsAllowed		= true;

		// Trade en cours
		private double activeTpPrice	= double.NaN;
		private double activeSlPrice	= double.NaN;
		private int    entryBarIdx		= -1;
		private double entryPxSaved		= double.NaN;

		// Ordre retest en attente
		private int    pendingRetestDir		= 0;	// 0 = aucun, 1 = long, -1 = short
		private double retestLvl			= double.NaN;
		private double retestSl				= double.NaN;
		private double retestTp				= double.NaN;
		private bool   longRetestActive		= false;
		private bool   shortRetestActive	= false;

		// Pivots lents (filtre d'entrée + TP initial)
		private readonly List<double> pivotHighs	= new List<double>();
		private readonly List<int>    pivotHighBars	= new List<int>();
		private readonly List<double> pivotLows		= new List<double>();
		private readonly List<int>    pivotLowBars	= new List<int>();

		// Pivots rapides (suivi du TP dynamique)
		private readonly List<double> tpPivotHighs	= new List<double>();
		private readonly List<int>    tpPivotHBars	= new List<int>();
		private readonly List<double> tpPivotLows	= new List<double>();
		private readonly List<int>    tpPivotLBars	= new List<int>();

		// Compteurs journaliers
		private int dayLosses		= 0;
		private int dayWins			= 0;
		private int lastClosedCount	= 0;

		private bool inSessionPrev	= false;

		#endregion

		#region Paramètres

		[NinjaScriptProperty]
		[Display(Name = "Session", Order = 0, GroupName = "1. Session & Opening Range")]
		public OrbSessionChoice SessionChoice { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Contrats par trade", Order = 1, GroupName = "1. Session & Opening Range")]
		public int Qty { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Vérifier proximité H/L session précédente", Order = 10, GroupName = "2. Filtre anti fake-breakout")]
		public bool UsePrevSessionFilter { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Exiger de la liquidité (swing) au-delà du niveau", Order = 11, GroupName = "2. Filtre anti fake-breakout")]
		public bool UseSwingFilter { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Période ATR (mesure de proximité)", Order = 12, GroupName = "2. Filtre anti fake-breakout")]
		public int AtrLen { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Distance min. requise (x ATR)", Order = 13, GroupName = "2. Filtre anti fake-breakout")]
		public double ProximityMult { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Longueur pivot (barres de chaque côté)", Order = 14, GroupName = "2. Filtre anti fake-breakout")]
		public int PivotLen { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Barres de recherche des pivots", Order = 15, GroupName = "2. Filtre anti fake-breakout")]
		public int PivotLookback { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "R:R minimum pour entrée marché", Order = 20, GroupName = "3. Gestion du trade")]
		public double MinRR { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Pertes max par jour", Order = 21, GroupName = "3. Gestion du trade")]
		public int MaxLossesDay { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Stop trading après 1 gain dans la journée", Order = 22, GroupName = "3. Gestion du trade")]
		public bool StopAfterWin { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Durée max de recherche de breakout (heures)", Order = 23, GroupName = "3. Gestion du trade")]
		public double MaxSessionHours { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Pas de longs sur Asie", Order = 24, GroupName = "3. Gestion du trade")]
		public bool DisableLongsAsia { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Tolérance TP (ticks) — clôture proche du swing = sortie", Order = 25, GroupName = "3. Gestion du trade")]
		public int TpProximityTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "TP dynamique (suit les nouveaux swings)", Order = 26, GroupName = "3. Gestion du trade")]
		public bool DynamicTpFollow { get; set; }

		[NinjaScriptProperty]
		[Range(1, 20)]
		[Display(Name = "Longueur pivot pour TP dynamique", Order = 27, GroupName = "3. Gestion du trade")]
		public int TpPivotLen { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Entrée retest (cassure éloignée → ordre limite)", Order = 28, GroupName = "3. Gestion du trade")]
		public bool UseRetestEntry { get; set; }

		[NinjaScriptProperty]
		[Range(0.5, double.MaxValue)]
		[Display(Name = "R:R cible du retest", Order = 29, GroupName = "3. Gestion du trade")]
		public double RetestRR { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Flatten de sécurité en fin de séance", Order = 40, GroupName = "4. Sécurité")]
		public bool UseEodFlatten { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Heure de flatten (fuseau de l'EXCHANGE)", Order = 41, GroupName = "4. Sécurité")]
		public int FlattenHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Minute de flatten", Order = 42, GroupName = "4. Sécurité")]
		public int FlattenMinute { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Dessiner l'opening range", Order = 50, GroupName = "5. Affichage")]
		public bool ShowVisuals { get; set; }

		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "Opening Range Break Asia/London — transcription NinjaScript du script Pine v6 : opening range multi-sessions (heure de Paris), filtre anti fake-breakout, TP sur swing avec suivi dynamique.";
				Name						= "ORB Asia London";
				Calculate					= Calculate.OnBarClose;
				EntriesPerDirection			= 1;
				EntryHandling				= EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy= false;
				IsFillLimitOnTouch			= false;
				MaximumBarsLookBack			= MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution			= OrderFillResolution.Standard;
				Slippage					= 3;
				StartBehavior				= StartBehavior.WaitUntilFlat;
				TimeInForce					= TimeInForce.Gtc;
				TraceOrders					= false;
				RealtimeErrorHandling		= RealtimeErrorHandling.StopCancelCloseIgnoreRejects;
				StopTargetHandling			= StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade			= 20;

				// Valeurs par défaut = celles du script Pine
				SessionChoice			= OrbSessionChoice.AsieLondres;
				Qty						= 1;
				UsePrevSessionFilter	= true;
				UseSwingFilter			= true;
				AtrLen					= 14;
				ProximityMult			= 0.5;
				PivotLen				= 4;
				PivotLookback			= 300;
				MinRR					= 1.0;
				MaxLossesDay			= 2;
				StopAfterWin			= true;
				MaxSessionHours			= 3.0;
				// Activé par défaut (choix utilisateur du 21/07/2026) : les longs
				// pris sur l'opening range Asie ne sont pas retenus, la
				// performance vient des shorts. Le script Pine d'origine laisse
				// cette option à false — c'est un écart volontaire.
				DisableLongsAsia		= true;
				TpProximityTicks		= 10;
				DynamicTpFollow			= true;
				TpPivotLen				= 2;
				UseRetestEntry			= false;
				RetestRR				= 1.0;
				UseEodFlatten			= true;
				FlattenHour				= 17;
				FlattenMinute			= 45;
				ShowVisuals				= true;
			}
			else if (State == State.Configure)
			{
				// Série journalière : équivalent de request.security("1D", high[1])
				AddDataSeries(BarsPeriodType.Day, 1);

				try   { tzParis = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time"); }
				catch { tzParis = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }
			}
		}

		#region Utilitaires

		// Heure d'ouverture de la bougie courante, dans le fuseau demandé. Pine
		// indexe les bougies sur leur ouverture, NinjaTrader sur leur clôture.
		private DateTime BarOpenIn(TimeZoneInfo tz)
		{
			DateTime close = TimeZoneInfo.ConvertTime(Time[0], NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo, tz);
			return close.AddMinutes(-BarsPeriod.Value);
		}

		// Une bougie appartient à la fenêtre si son ouverture est dans [début, fin[
		private static bool InWindow(int minutes, int startH, int startM, int endH, int endM)
		{
			int start = startH * 60 + startM;
			int end   = endH * 60 + endM;
			return minutes >= start && minutes < end;
		}

		private double PivotHighAt(int left, int right)
		{
			if (CurrentBar < left + right)
				return double.NaN;

			double pivot = High[right];

			for (int i = 1; i <= left; i++)
				if (High[right + i] >= pivot)
					return double.NaN;

			for (int i = 1; i <= right; i++)
				if (High[right - i] >= pivot)
					return double.NaN;

			return pivot;
		}

		private double PivotLowAt(int left, int right)
		{
			if (CurrentBar < left + right)
				return double.NaN;

			double pivot = Low[right];

			for (int i = 1; i <= left; i++)
				if (Low[right + i] <= pivot)
					return double.NaN;

			for (int i = 1; i <= right; i++)
				if (Low[right - i] <= pivot)
					return double.NaN;

			return pivot;
		}

		private double NearestSwingAbove(double level)
		{
			double nearest = double.NaN;
			foreach (double p in pivotHighs)
				if (p > level && (double.IsNaN(nearest) || p < nearest))
					nearest = p;
			return nearest;
		}

		private double NearestSwingBelow(double level)
		{
			double nearest = double.NaN;
			foreach (double p in pivotLows)
				if (p < level && (double.IsNaN(nearest) || p > nearest))
					nearest = p;
			return nearest;
		}

		// Versions rapides : seuls les pivots dont la bougie de FORMATION est
		// strictement postérieure à minFormBar (la barre d'entrée) sont des
		// cibles valables — un swing antérieur à l'entrée est du bruit du
		// mouvement en cours, pas de la liquidité.
		private double TpNearestHighAbove(double level, int minFormBar)
		{
			double nearest = double.NaN;
			for (int i = 0; i < tpPivotHighs.Count; i++)
			{
				double p = tpPivotHighs[i];
				int    fb = tpPivotHBars[i];
				if (p > level && (minFormBar < 0 || fb > minFormBar) && (double.IsNaN(nearest) || p < nearest))
					nearest = p;
			}
			return nearest;
		}

		private double TpNearestLowBelow(double level, int minFormBar)
		{
			double nearest = double.NaN;
			for (int i = 0; i < tpPivotLows.Count; i++)
			{
				double p = tpPivotLows[i];
				int    fb = tpPivotLBars[i];
				if (p < level && (minFormBar < 0 || fb > minFormBar) && (double.IsNaN(nearest) || p > nearest))
					nearest = p;
			}
			return nearest;
		}

		private void ResetTradeState()
		{
			activeTpPrice	= double.NaN;
			activeSlPrice	= double.NaN;
			entryBarIdx		= -1;
			entryPxSaved	= double.NaN;
		}

		#endregion

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0)
				return;

			if (CurrentBars[0] < Math.Max(BarsRequiredToTrade, PivotLen * 2 + 1) || CurrentBars[1] < 1)
				return;

			double positionSize = Position.MarketPosition == MarketPosition.Long  ?  Position.Quantity
								: Position.MarketPosition == MarketPosition.Short ? -Position.Quantity
								: 0;

			// ═══ 1) SESSION & OPENING RANGE ══════════════════════════════════
			DateTime paris	= BarOpenIn(tzParis);
			int parisMin	= paris.Hour * 60 + paris.Minute;

			bool useAsiaOR	= SessionChoice != OrbSessionChoice.LondresNY;
			bool useNYOR	= SessionChoice == OrbSessionChoice.LondresNY
						   || SessionChoice == OrbSessionChoice.AsieLondresNY;

			bool inAsiaOR	= InWindow(parisMin,  2,  0,  2, 25);
			bool inLondonOR	= InWindow(parisMin,  9,  0,  9, 30);
			bool inNYOR		= InWindow(parisMin, 15, 30, 15, 45);

			bool inSession;
			switch (SessionChoice)
			{
				case OrbSessionChoice.LondresSeule:	inSession = inLondonOR;	break;
				case OrbSessionChoice.AsieSeule:	inSession = inAsiaOR;	break;
				default:
					inSession = (useAsiaOR && inAsiaOR) || inLondonOR || (useNYOR && inNYOR);
					break;
			}

			bool isNewSessionStart = inSession && (!inSessionPrev || double.IsNaN(orHigh));

			if (inSession)
			{
				if (isNewSessionStart)
				{
					orHigh				= High[0];
					orLow				= Low[0];
					orBarCount			= 1;
					breakoutDoneToday	= false;
					sessionTradeCount	= 0;
					sessionWinLock		= false;
					orLongsAllowed		= !(DisableLongsAsia && inAsiaOR);
				}
				else
				{
					orHigh = Math.Max(orHigh, High[0]);
					orLow  = Math.Min(orLow,  Low[0]);
					orBarCount++;
				}
			}

			bool sessionJustEnded = !inSession && inSessionPrev;

			if (sessionJustEnded)
			{
				orMid			= (orHigh + orLow) / 2;
				orLocked		= true;
				sessionEndTime	= Time[0];

				if (ShowVisuals && !double.IsNaN(orHigh))
					Draw.Rectangle(this, "OR" + CurrentBar, false, orBarCount, orHigh, 0, orLow,
						Brushes.Transparent, Brushes.SteelBlue, 20);
			}

			inSessionPrev = inSession;

			bool withinTradingWindow = sessionEndTime != DateTime.MinValue
									&& (Time[0] - sessionEndTime).TotalHours <= MaxSessionHours;

			// ═══ 2) H/L DE LA SESSION PRÉCÉDENTE ═════════════════════════════
			// Index 1 de la série journalière = la veille entièrement clôturée
			// (l'index 0 est la journée en cours, encore en formation).
			double prevDayHigh = Highs[1][1];
			double prevDayLow  = Lows[1][1];

			// ═══ 3) PIVOTS — deux séries ═════════════════════════════════════
			double pivotHighVal		= PivotHighAt(PivotLen, PivotLen);
			double pivotLowVal		= PivotLowAt(PivotLen, PivotLen);
			double tpPivotHighVal	= PivotHighAt(TpPivotLen, TpPivotLen);
			double tpPivotLowVal	= PivotLowAt(TpPivotLen, TpPivotLen);

			if (!double.IsNaN(pivotHighVal))
			{
				pivotHighs.Add(pivotHighVal);
				pivotHighBars.Add(CurrentBar - PivotLen);
			}
			if (!double.IsNaN(pivotLowVal))
			{
				pivotLows.Add(pivotLowVal);
				pivotLowBars.Add(CurrentBar - PivotLen);
			}
			if (!double.IsNaN(tpPivotHighVal))
			{
				tpPivotHighs.Add(tpPivotHighVal);
				tpPivotHBars.Add(CurrentBar - TpPivotLen);
			}
			if (!double.IsNaN(tpPivotLowVal))
			{
				tpPivotLows.Add(tpPivotLowVal);
				tpPivotLBars.Add(CurrentBar - TpPivotLen);
			}

			// Purge au-delà de la fenêtre de recherche
			while (pivotHighBars.Count > 0 && CurrentBar - pivotHighBars[0] > PivotLookback)
			{ pivotHighs.RemoveAt(0); pivotHighBars.RemoveAt(0); }
			while (pivotLowBars.Count > 0 && CurrentBar - pivotLowBars[0] > PivotLookback)
			{ pivotLows.RemoveAt(0); pivotLowBars.RemoveAt(0); }
			while (tpPivotHBars.Count > 0 && CurrentBar - tpPivotHBars[0] > PivotLookback)
			{ tpPivotHighs.RemoveAt(0); tpPivotHBars.RemoveAt(0); }
			while (tpPivotLBars.Count > 0 && CurrentBar - tpPivotLBars[0] > PivotLookback)
			{ tpPivotLows.RemoveAt(0); tpPivotLBars.RemoveAt(0); }

			// ═══ 4) COMPTEURS JOURNALIERS ════════════════════════════════════
			if (Bars.IsFirstBarOfSession)
			{
				dayLosses			= 0;
				dayWins				= 0;
				breakoutDoneToday	= false;
			}

			if (SystemPerformance.AllTrades.Count > lastClosedCount)
			{
				double tradeProfit = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1].ProfitCurrency;

				if (tradeProfit > 0)
				{
					dayWins++;
					sessionWinLock = true;
				}
				else
				{
					dayLosses++;
					// Deuxième chance après une perte, si la session n'a pas
					// déjà consommé ses deux tentatives.
					if (sessionTradeCount < 2)
						breakoutDoneToday = false;
				}

				lastClosedCount = SystemPerformance.AllTrades.Count;
			}

			bool canTradeToday = dayLosses < MaxLossesDay && (!StopAfterWin || dayWins < 1);

			// ═══ 5) FLATTEN DE FIN DE JOURNÉE ════════════════════════════════
			// Volontairement dans le fuseau de l'EXCHANGE, pas en heure de
			// Paris : le script Pine utilise 'hour'/'minute' nus ici, et le
			// réglage se fait selon le marché tradé (Eurex/DAX ferme à 17h30).
			DateTime exch	= BarOpenIn(Bars.TradingHours.TimeZoneInfo);
			bool eodFlatten	= UseEodFlatten && exch.Hour == FlattenHour && exch.Minute >= FlattenMinute;

			if (eodFlatten)
			{
				CancelPendingRetest();

				// close_all() de Pine : toute la position, quelle que soit
				// l'entrée qui l'a ouverte (marché ou retest).
				if (positionSize > 0) ExitLong();
				if (positionSize < 0) ExitShort();
			}

			// ═══ 6) DÉTECTION DU BREAKOUT ════════════════════════════════════
			bool firstBreakoutCheck = !inSession && orLocked && !breakoutDoneToday && !sessionWinLock
								   && canTradeToday && !eodFlatten && withinTradingWindow;

			bool crossedAboveOR = Close[0] > orHigh && Close[1] <= orHigh;
			bool crossedBelowOR = Close[0] < orLow  && Close[1] >= orLow;

			bool longBreakout  = firstBreakoutCheck && crossedAboveOR && orLongsAllowed;
			bool shortBreakout = firstBreakoutCheck && crossedBelowOR;

			double atrVal	= ATR(AtrLen)[0];
			double minDist	= ProximityMult * atrVal;

			double nearestHighAbove = NearestSwingAbove(orHigh);
			double nearestLowBelow  = NearestSwingBelow(orLow);

			bool tooCloseToPrevHigh = UsePrevSessionFilter && !double.IsNaN(prevDayHigh)
								   && Math.Abs(orHigh - prevDayHigh) <= minDist;
			bool tooCloseToPrevLow  = UsePrevSessionFilter && !double.IsNaN(prevDayLow)
								   && Math.Abs(orLow - prevDayLow) <= minDist;

			bool hasLiquidityAbove = UseSwingFilter
								   ? (!double.IsNaN(nearestHighAbove) && (nearestHighAbove - orHigh) > minDist)
								   : true;
			bool hasLiquidityBelow = UseSwingFilter
								   ? (!double.IsNaN(nearestLowBelow) && (orLow - nearestLowBelow) > minDist)
								   : true;

			bool longInvalid  = tooCloseToPrevHigh && !hasLiquidityAbove;
			bool shortInvalid = tooCloseToPrevLow  && !hasLiquidityBelow;

			// ═══ 7) ENTRÉE MARCHÉ ════════════════════════════════════════════
			if (longBreakout && !longInvalid && !double.IsNaN(nearestHighAbove))
			{
				double entryPrice	= Close[0];
				double slPrice		= orMid;
				double tpPrice		= nearestHighAbove;
				double riskPts		= entryPrice - slPrice;
				double rewardPts	= tpPrice - entryPrice;
				double rr			= riskPts > 0 ? rewardPts / riskPts : double.NaN;

				if (riskPts > 0 && rr >= MinRR)
				{
					SetStopLoss("Long Marché", CalculationMode.Price, slPrice, false);
					SetProfitTarget("Long Marché", CalculationMode.Price, tpPrice);
					EnterLong(Qty, "Long Marché");

					breakoutDoneToday	= true;
					sessionTradeCount++;
					activeTpPrice		= tpPrice;
					activeSlPrice		= slPrice;
					entryBarIdx			= CurrentBar;
					entryPxSaved		= entryPrice;
				}
			}

			if (shortBreakout && !shortInvalid && !double.IsNaN(nearestLowBelow))
			{
				double entryPrice	= Close[0];
				double slPrice		= orMid;
				double tpPrice		= nearestLowBelow;
				double riskPts		= slPrice - entryPrice;
				double rewardPts	= entryPrice - tpPrice;
				double rr			= riskPts > 0 ? rewardPts / riskPts : double.NaN;

				if (riskPts > 0 && rr >= MinRR)
				{
					SetStopLoss("Short Marché", CalculationMode.Price, slPrice, false);
					SetProfitTarget("Short Marché", CalculationMode.Price, tpPrice);
					EnterShort(Qty, "Short Marché");

					breakoutDoneToday	= true;
					sessionTradeCount++;
					activeTpPrice		= tpPrice;
					activeSlPrice		= slPrice;
					entryBarIdx			= CurrentBar;
					entryPxSaved		= entryPrice;
				}
			}

			// ═══ 7bis) TP DYNAMIQUE ══════════════════════════════════════════
			// Le TP ne peut que se RAPPROCHER, jamais s'éloigner, et ne suit que
			// des swings formés après la bougie d'entrée, au-delà du prix
			// d'entrée d'au moins minDist. Le stop n'est jamais modifié.
			if (DynamicTpFollow && positionSize != 0 && !double.IsNaN(activeTpPrice) && entryBarIdx >= 0)
			{
				if (positionSize < 0)
				{
					double candidateTp = TpNearestLowBelow(Math.Min(entryPxSaved - minDist, orLow), entryBarIdx);

					if (!double.IsNaN(candidateTp) && candidateTp > activeTpPrice)
					{
						activeTpPrice = candidateTp;
						SetProfitTarget("Short Marché", CalculationMode.Price, activeTpPrice);
					}
				}
				else
				{
					double candidateTp = TpNearestHighAbove(Math.Max(entryPxSaved + minDist, orHigh), entryBarIdx);

					if (!double.IsNaN(candidateTp) && candidateTp < activeTpPrice)
					{
						activeTpPrice = candidateTp;
						SetProfitTarget("Long Marché", CalculationMode.Price, activeTpPrice);
					}
				}
			}

			// ═══ 7ter) ENTRÉE RETEST ═════════════════════════════════════════
			// Remplissage détecté à la clôture de la barre
			if (pendingRetestDir == 1 && positionSize > 0)
			{
				longRetestActive = true;
				pendingRetestDir = 0;
			}
			if (pendingRetestDir == -1 && positionSize < 0)
			{
				shortRetestActive = true;
				pendingRetestDir = 0;
			}

			// Annulation d'un ordre devenu caduc
			if (pendingRetestDir != 0 && (isNewSessionStart || !withinTradingWindow || eodFlatten
				|| (pendingRetestDir == 1 && crossedBelowOR) || (pendingRetestDir == -1 && crossedAboveOR)))
			{
				CancelPendingRetest();
			}

			// Pose de l'ordre : signal valide mais entrée marché refusée par le
			// filtre R:R. breakoutDoneToday est consommé dès la pose, que
			// l'ordre soit rempli ou non.
			if (UseRetestEntry && pendingRetestDir == 0 && positionSize == 0)
			{
				if (longBreakout && !longInvalid)
				{
					double mRisk		= Close[0] - orMid;
					double mRr			= (!double.IsNaN(nearestHighAbove) && mRisk > 0)
										? (nearestHighAbove - Close[0]) / mRisk : double.NaN;
					bool   marketTaken	= !double.IsNaN(mRr) && mRr >= MinRR;
					double lineRisk		= orHigh - orMid;

					if (!marketTaken && lineRisk > 0)
					{
						retestLvl	= orHigh;
						retestSl	= orMid;
						retestTp	= orHigh + RetestRR * lineRisk;

						SetStopLoss("Long Retest", CalculationMode.Price, retestSl, false);
						SetProfitTarget("Long Retest", CalculationMode.Price, retestTp);
						EnterLongLimit(0, true, Qty, retestLvl, "Long Retest");

						pendingRetestDir	= 1;
						breakoutDoneToday	= true;
						sessionTradeCount++;
					}
				}

				if (shortBreakout && !shortInvalid)
				{
					double mRisk		= orMid - Close[0];
					double mRr			= (!double.IsNaN(nearestLowBelow) && mRisk > 0)
										? (Close[0] - nearestLowBelow) / mRisk : double.NaN;
					bool   marketTaken	= !double.IsNaN(mRr) && mRr >= MinRR;
					double lineRisk		= orMid - orLow;

					if (!marketTaken && lineRisk > 0)
					{
						retestLvl	= orLow;
						retestSl	= orMid;
						retestTp	= orLow - RetestRR * lineRisk;

						SetStopLoss("Short Retest", CalculationMode.Price, retestSl, false);
						SetProfitTarget("Short Retest", CalculationMode.Price, retestTp);
						EnterShortLimit(0, true, Qty, retestLvl, "Short Retest");

						pendingRetestDir	= -1;
						breakoutDoneToday	= true;
						sessionTradeCount++;
					}
				}
			}

			// ═══ 8) SORTIES ══════════════════════════════════════════════════
			double tpTolerance = TpProximityTicks * TickSize;

			bool longNearTp  = TpProximityTicks > 0 && positionSize > 0 && !double.IsNaN(activeTpPrice)
							&& Close[0] >= activeTpPrice - tpTolerance;
			bool shortNearTp = TpProximityTicks > 0 && positionSize < 0 && !double.IsNaN(activeTpPrice)
							&& Close[0] <= activeTpPrice + tpTolerance;

			if (longNearTp || shortNearTp)
			{
				CancelPendingRetest();

				if (positionSize > 0) ExitLong();
				if (positionSize < 0) ExitShort();

				ResetTradeState();
			}

			// Fix mèche : en Pine, process_orders_on_close n'évalue les limites
			// que sur la clôture — une mèche qui franchit le TP sans clôture
			// au-delà ne déclenche rien. Pine réémet alors une limite au prix de
			// clôture ; ici, sortie au marché, son équivalent le plus proche.
			bool longWickTp  = !longNearTp  && positionSize > 0 && !double.IsNaN(activeTpPrice)
							&& High[0] >= activeTpPrice;
			bool shortWickTp = !shortNearTp && positionSize < 0 && !double.IsNaN(activeTpPrice)
							&& Low[0] <= activeTpPrice;

			if (longWickTp)
			{
				ExitLong("Sortie Long", "Long Marché");
				activeTpPrice = double.NaN;
				activeSlPrice = double.NaN;
			}
			if (shortWickTp)
			{
				ExitShort("Sortie Short", "Short Marché");
				activeTpPrice = double.NaN;
				activeSlPrice = double.NaN;
			}

			if (positionSize == 0 && !(longBreakout || shortBreakout))
				ResetTradeState();

			// Position retest : pas de sortie anticipée par proximité — elle sort
			// au stop ou au target, rien d'autre. Seul le fix mèche s'applique.
			if (longRetestActive && positionSize > 0 && High[0] >= retestTp)
				ExitLong("Sortie Long Retest", "Long Retest");

			if (shortRetestActive && positionSize < 0 && Low[0] <= retestTp)
				ExitShort("Sortie Short Retest", "Short Retest");

			if (positionSize == 0)
			{
				longRetestActive  = false;
				shortRetestActive = false;
			}
		}

		// strategy.cancel() de Pine : annule les ordres d'entrée retest encore
		// en attente et remet l'état à zéro.
		private void CancelPendingRetest()
		{
			foreach (Order o in Orders)
			{
				if ((o.Name == "Long Retest" || o.Name == "Short Retest")
					&& (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted
						|| o.OrderState == OrderState.Submitted))
					CancelOrder(o);
			}

			pendingRetestDir	= 0;
			retestLvl			= double.NaN;
			retestSl			= double.NaN;
			retestTp			= double.NaN;
		}
	}
}
