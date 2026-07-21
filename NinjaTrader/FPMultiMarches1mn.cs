#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

// ═══════════════════════════════════════════════════════════════════════════
// FP Multi-Marchés 1mn — transcription NinjaTrader 8 du script Pine v6
// « yannis_FP_1MN_multi_marches.pine » (FP Multi-Marchés 1mn).
//
// Principe : à l'ouverture de chaque session (heure de Paris) une bougie de
// référence fige une zone « fair price » (open/close de la bougie) et un biais
// (refBull / refBear). Dans la fenêtre qui suit, la stratégie prend soit une
// CONTINUATION dans le sens du biais, soit une RÉVERSION vers la zone FP,
// après armement sur retest de pivot ou cassure BOS des extrêmes de session.
//
// Fidélité à Pine :
//  - Calculate = OnBarClose : Pine évalue le script à la clôture de chaque
//    bougie et remplit les ordres marché à l'ouverture de la suivante.
//  - L'ordre des blocs de ce fichier suit l'ordre d'exécution du script Pine
//    (les variables « var » Pine deviennent des champs de classe).
//  - Les niveaux de pivot (box/line Pine) sont réduits à leur seule donnée
//    utile — le prix — dans deux listes ; tout le cosmétique est retiré.
//
// Écarts assumés (documentés dans le code) :
//  - Le webhook JSON vers le dispatcher FastAPI n'est pas repris : NinjaTrader
//    passe les ordres directement au broker.
//  - Les blocs de debug (table d'échecs de filtres) ne sont pas repris.
//  - Stop et take-profit passent par SetStopLoss / SetProfitTarget (OCO natif,
//    équivalent le plus proche de strategy.exit). En OnBarClose un niveau déjà
//    franchi ne peut pas être exécuté intrabar : la position est alors soldée
//    au marché à la bougie suivante (voir ApplyExits).
// ═══════════════════════════════════════════════════════════════════════════

namespace NinjaTrader.NinjaScript.Strategies
{
	public enum FpMmMarche
	{
		[Description("Auto (symbole)")]		Auto,
		[Description("NQ / indices US")]	NQ,
		[Description("GC (or)")]			GC,
		[Description("SI (argent)")]		SI,
		[Description("CL (pétrole WTI)")]	CL,
		[Description("NG (gaz naturel)")]	NG,
		[Description("FDAX (DAX)")]			FDAX,
		[Description("Personnalisé")]		Perso
	}

	public enum FpMmRevConfirm
	{
		[Description("Aucune")]				Aucune,
		[Description("Engulfante")]			Engulfante,
		[Description("Cassure 2 bougies")]	Cassure2Bougies
	}

	public enum FpMmRevTp
	{
		[Description("Bord proche zone FP")]	BordProcheFp,
		[Description("openValue (original)")]	OpenValue,
		[Description("1,5 ATR")]				Atr15
	}

	public enum FpMmAtrMode
	{
		[Description("Expansion 20 bougies")]		Expansion20,
		[Description("Expansion 10 bougies")]		Expansion10,
		[Description("TR bougie > ATR")]			TrSuperieurAtr,
		[Description("Expansion 20 OU TR > ATR")]	Expansion20OuTr,
		[Description("Désactivé")]					Desactive
	}

	public class FPMultiMarches1mn : Strategy
	{
		#region Niveaux de pivot (remplace box/line Pine)

		// Un niveau mémorisé = le prix du pivot + son type. Le type sert à
		// distinguer les fills : "high"/"low" pour la série 2 (celle qui arme
		// les continuations), "s1" pour la série 1 (qui arme les réversions).
		private class Niveau
		{
			public double Prix;
			public string Type;

			public Niveau(double prix, string type) { Prix = prix; Type = type; }
		}

		private readonly List<Niveau> niveauxSerie1 = new List<Niveau>();
		private readonly List<Niveau> niveauxSerie2 = new List<Niveau>();
		private readonly List<string> fillEvents    = new List<string>();

		#endregion

		#region Champs persistants (variables « var » du script Pine)

		private TimeZoneInfo tzParis;

		private double currentPeriod	= double.NaN;
		private double lastPeriod		= double.NaN;
		private double openValue		= double.NaN;
		private double closeValue		= double.NaN;

		private double entryPrice		= double.NaN;
		private double stopPrice		= double.NaN;
		private double tpPrice			= double.NaN;

		private double minAtrUp			= double.NaN;
		private double minAtrDown		= double.NaN;

		private double beTarget			= double.NaN;
		private bool   beArmed			= false;

		private bool   muchSell			= false;
		private bool   muchBuy			= false;
		private bool   continuePeriod	= true;
		private string tradeType		= "";

		private double pushWickSize		= double.NaN;

		// Pivots : index de la bougie de pivot et dernier extrême série 2
		private int    structBias		= 0;
		private double lastHi2			= double.NaN;
		private double lastLo2			= double.NaN;

		// Armement des setups (index de bougie, -1 = non armé)
		private int armContLongBar		= -1;
		private int armContShortBar		= -1;
		private int armRevLongBar		= -1;
		private int armRevShortBar		= -1;
		private int rearmLongBar		= -1;
		private int rearmShortBar		= -1;
		private int entriesWindow		= 0;

		// Extrêmes de session pour la détection BOS
		private double sessLo			= double.NaN;
		private double sessHi			= double.NaN;

		// Suivi d'état inter-bougies
		private int    lastStartBar		= -1;		// bougie du dernier conditionStart (ta.barssince)
		private double prevPositionSize	= 0;

		// Préréglages résolus une fois par bougie
		private int sAH, sAM, sBH, sBM, sCH, sCM, sDH, sDM;
		private bool sAon, sBon, sCon, sDon;
		private bool hebOn;
		private DayOfWeek hebDow;
		private int hebH, hebM;
		private double kMkt = 1.0;

		#endregion

		#region Paramètres

		[NinjaScriptProperty]
		[Display(Name = "Marché", Order = 0, GroupName = "1. Marché")]
		public FpMmMarche Marche { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Contrats par trade", Order = 1, GroupName = "1. Marché")]
		public int QtyIn { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Perso — session principale activée", Order = 10, GroupName = "2. Marché personnalisé")]
		public bool PSAon { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Perso — session principale (h)", Order = 11, GroupName = "2. Marché personnalisé")]
		public int PSAH { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Perso — session principale (min)", Order = 12, GroupName = "2. Marché personnalisé")]
		public int PSAM { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Perso — réouverture nuit activée", Order = 13, GroupName = "2. Marché personnalisé")]
		public bool PSBon { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Perso — réouverture nuit (h)", Order = 14, GroupName = "2. Marché personnalisé")]
		public int PSBH { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Perso — réouverture nuit (min)", Order = 15, GroupName = "2. Marché personnalisé")]
		public int PSBM { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Perso — session asiatique activée", Order = 16, GroupName = "2. Marché personnalisé")]
		public bool PSCon { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Perso — session asiatique (h)", Order = 17, GroupName = "2. Marché personnalisé")]
		public int PSCH { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Perso — session asiatique (min)", Order = 18, GroupName = "2. Marché personnalisé")]
		public int PSCM { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Perso — session Londres activée", Order = 19, GroupName = "2. Marché personnalisé")]
		public bool PSDon { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Perso — session Londres (h)", Order = 20, GroupName = "2. Marché personnalisé")]
		public int PSDH { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Perso — session Londres (min)", Order = 21, GroupName = "2. Marché personnalisé")]
		public int PSDM { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Perso — news hebdo récurrente", Order = 22, GroupName = "2. Marché personnalisé")]
		public bool PHebOn { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Perso — jour news hebdo", Order = 23, GroupName = "2. Marché personnalisé")]
		public DayOfWeek PHebDay { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Perso — news hebdo (h)", Order = 24, GroupName = "2. Marché personnalisé")]
		public int PHebH { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Perso — news hebdo (min)", Order = 25, GroupName = "2. Marché personnalisé")]
		public int PHebM { get; set; }

		[NinjaScriptProperty]
		[Range(0.000001, double.MaxValue)]
		[Display(Name = "Perso — échelle volatilité vs NQ (k)", Order = 26, GroupName = "2. Marché personnalisé")]
		public double PK { get; set; }

		// Les news du script Pine sont saisies comme une liste de jours du mois.
		// Ici une seule chaîne par créneau : « 1,5,12,30 » (vide = aucune).
		[NinjaScriptProperty]
		[Display(Name = "News 14h30 (jours du mois, ex : 1,5,12)", Order = 30, GroupName = "3. News")]
		public string News1430Days { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "News 20h00 (jours du mois, ex : 1,5,12)", Order = 31, GroupName = "3. News")]
		public string News2000Days { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Rejection Swing — droite", Order = 40, GroupName = "4. Pivots")]
		public int SwingSizeR1 { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Rejection Swing — gauche", Order = 41, GroupName = "4. Pivots")]
		public int SwingSizeL1 { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Continuation Swing — droite", Order = 42, GroupName = "4. Pivots")]
		public int SwingSizeR2 { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Continuation Swing — gauche", Order = 43, GroupName = "4. Pivots")]
		public int SwingSizeL2 { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Filtre BOS structurel — Longs", Order = 50, GroupName = "5. TJR")]
		public bool UseStructFilterLong { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Filtre BOS structurel — Shorts", Order = 51, GroupName = "5. TJR")]
		public bool UseStructFilterShort { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Validité setup armé (bougies)", Order = 52, GroupName = "5. TJR")]
		public int ArmBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Fenêtre continuation (minutes)", Order = 53, GroupName = "5. TJR")]
		public int ContWinMin { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Continuation : suivre le sens de la bougie de référence", Order = 54, GroupName = "5. TJR")]
		public bool UseRefDir { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Réversion : confirmation requise", Order = 55, GroupName = "5. TJR")]
		public FpMmRevConfirm RevConfirm { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Stop structurel (au-delà du swing)", Order = 56, GroupName = "5. TJR")]
		public bool UseStructStop { get; set; }

		[NinjaScriptProperty]
		[Range(2, int.MaxValue)]
		[Display(Name = "Lookback du stop (bougies)", Order = 57, GroupName = "5. TJR")]
		public int StopLookback { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "RR minimum vers le fair price", Order = 58, GroupName = "5. TJR")]
		public double MinRR { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Entrées max par fenêtre", Order = 59, GroupName = "5. TJR")]
		public int MaxEntriesWin { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Re-entrée après SL (thèse intacte)", Order = 60, GroupName = "5. TJR")]
		public bool ReEnterAfterLoss { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Fenêtre re-entrée après SL (bougies)", Order = 61, GroupName = "5. TJR")]
		public int ReArmBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Re-entrée allégée (bougie + cassure + FP)", Order = 62, GroupName = "5. TJR")]
		public bool LiteReentry { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "BOS structurel : remettre à zéro à chaque session", Order = 63, GroupName = "5. TJR")]
		public bool StructBiasReset { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Réversion : take-profit", Order = 70, GroupName = "6. Gestion trade")]
		public FpMmRevTp RevTP { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Continuation : break-even à 1R", Order = 71, GroupName = "6. Gestion trade")]
		public bool UseBE { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Réversion : break-even à 1R", Order = 72, GroupName = "6. Gestion trade")]
		public bool UseBERev { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Filtre volatilité (ATR)", Order = 73, GroupName = "6. Gestion trade")]
		public FpMmAtrMode AtrMode { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Clôture au profit fixe", Order = 74, GroupName = "6. Gestion trade")]
		public bool UseProfitClose { get; set; }

		[NinjaScriptProperty]
		[Range(100, double.MaxValue)]
		[Display(Name = "Seuil de profit ($ par trade)", Order = 75, GroupName = "6. Gestion trade")]
		public double ProfitCloseUsd { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Sortie fair price : fermer aussi les continuations", Order = 76, GroupName = "6. Gestion trade")]
		public bool UseFpCloseCont { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Multiplicateur bandes ATR", Order = 77, GroupName = "6. Gestion trade")]
		public double M { get; set; }

		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "FP Multi-Marchés 1mn — transcription NinjaScript du script Pine v6 (sessions Paris, zone fair price, continuations et réversions).";
				Name						= "FP Multi-Marchés 1mn";
				Calculate					= Calculate.OnBarClose;
				EntriesPerDirection			= 1;
				EntryHandling				= EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy= false;
				ExitOnSessionCloseSeconds	= 30;
				IsFillLimitOnTouch			= false;
				MaximumBarsLookBack			= MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution			= OrderFillResolution.Standard;
				Slippage					= 2;
				StartBehavior				= StartBehavior.WaitUntilFlat;
				TimeInForce					= TimeInForce.Gtc;
				TraceOrders					= false;
				RealtimeErrorHandling		= RealtimeErrorHandling.StopCancelCloseIgnoreRejects;
				StopTargetHandling			= StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade			= 60;

				// Valeurs par défaut = celles du script Pine
				Marche					= FpMmMarche.Auto;
				QtyIn					= 10;
				PSAon = true;  PSAH = 15; PSAM = 29;
				PSBon = true;  PSBH = 0;  PSBM = 0;
				PSCon = true;  PSCH = 2;  PSCM = 0;
				PSDon = true;  PSDH = 9;  PSDM = 0;
				PHebOn					= false;
				PHebDay					= DayOfWeek.Wednesday;
				PHebH					= 16;
				PHebM					= 29;
				PK						= 1.0;
				News1430Days			= string.Empty;
				News2000Days			= string.Empty;
				SwingSizeR1 = 3; SwingSizeL1 = 3;
				SwingSizeR2 = 6; SwingSizeL2 = 6;
				UseStructFilterLong		= true;
				UseStructFilterShort	= true;
				ArmBars					= 5;
				ContWinMin				= 10;
				UseRefDir				= true;
				RevConfirm				= FpMmRevConfirm.Cassure2Bougies;
				UseStructStop			= false;
				StopLookback			= 12;
				MinRR					= 1.0;
				MaxEntriesWin			= 3;
				ReEnterAfterLoss		= true;
				ReArmBars				= 20;
				LiteReentry				= true;
				StructBiasReset			= false;
				RevTP					= FpMmRevTp.BordProcheFp;
				UseBE					= true;
				UseBERev				= true;
				AtrMode					= FpMmAtrMode.TrSuperieurAtr;
				UseProfitClose			= false;
				ProfitCloseUsd			= 5000;
				UseFpCloseCont			= true;
				M						= 1.5;
			}
			else if (State == State.Configure)
			{
				// Les sessions du script sont exprimées en heure de Paris.
				try   { tzParis = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time"); }
				catch { tzParis = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }
			}
			else if (State == State.DataLoaded)
			{
				ResolveMarche();
			}
		}

		#region Préréglages marché

		// Détection automatique par racine du symbole (GC, MCL, FDAX…) puis
		// résolution des 4 créneaux de session, de leur activation, de la news
		// hebdomadaire et de l'échelle de volatilité k.
		private void ResolveMarche()
		{
			FpMmMarche m = Marche;

			if (m == FpMmMarche.Auto)
			{
				string root = (Instrument.MasterInstrument.Name ?? string.Empty).ToUpperInvariant();

				if (root == "GC" || root == "MGC")							m = FpMmMarche.GC;
				else if (root == "SI" || root == "SIL")						m = FpMmMarche.SI;
				else if (root == "CL" || root == "MCL" || root == "QM")		m = FpMmMarche.CL;
				else if (root == "NG" || root == "MNG" || root == "QG")		m = FpMmMarche.NG;
				else if (root.Contains("DAX"))								m = FpMmMarche.FDAX;
				else														m = FpMmMarche.NQ;
			}

			// Sessions (heure de Paris). Slot A = session principale,
			// B = réouverture nuit, C = Asie, D = Londres. La bougie de
			// référence est celle qui OUVRE à l'heure indiquée.
			switch (m)
			{
				case FpMmMarche.GC:
				case FpMmMarche.SI:
					sAH = 14; sAM = 19; sBH = 0; sBM = 0; sCH = 2; sCM = 0; sDH = 9; sDM = 0;
					break;
				case FpMmMarche.CL:
				case FpMmMarche.NG:
					sAH = 14; sAM = 59; sBH = 0; sBM = 0; sCH = 2; sCM = 0; sDH = 9; sDM = 0;
					break;
				case FpMmMarche.FDAX:
					sAH = 8; sAM = 59; sBH = 1; sBM = 10; sCH = 15; sCM = 29; sDH = 17; sDM = 29;
					break;
				case FpMmMarche.Perso:
					sAH = PSAH; sAM = PSAM; sBH = PSBH; sBM = PSBM;
					sCH = PSCH; sCM = PSCM; sDH = PSDH; sDM = PSDM;
					break;
				default:
					sAH = 15; sAM = 29; sBH = 0; sBM = 0; sCH = 2; sCM = 0; sDH = 9; sDM = 0;
					break;
			}

			// Sur CL/NG aucun trade entre 00h00 et 08h00 Paris : slots nuit et
			// Asie désactivés (pas de volume exploitable avant Londres).
			switch (m)
			{
				case FpMmMarche.CL:
				case FpMmMarche.NG:
					sAon = true; sBon = false; sCon = false; sDon = true;
					break;
				case FpMmMarche.Perso:
					sAon = PSAon; sBon = PSBon; sCon = PSCon; sDon = PSDon;
					break;
				default:
					sAon = true; sBon = true; sCon = true; sDon = true;
					break;
			}

			// News hebdomadaire EIA (bougie de référence 1 min avant l'annonce)
			switch (m)
			{
				case FpMmMarche.CL:
					hebOn = true; hebDow = DayOfWeek.Wednesday; hebH = 16; hebM = 29;
					break;
				case FpMmMarche.NG:
					hebOn = true; hebDow = DayOfWeek.Thursday; hebH = 16; hebM = 29;
					break;
				case FpMmMarche.Perso:
					hebOn = PHebOn; hebDow = PHebDay; hebH = PHebH; hebM = PHebM;
					break;
				default:
					hebOn = false; hebDow = DayOfWeek.Wednesday; hebH = 16; hebM = 29;
					break;
			}

			// Échelle de volatilité vs NQ : tous les seuils en points du script
			// NQ (paliers SL/TP, seuils ATR5, buffer FP, stop structurel) sont
			// multipliés par k pour reproduire le comportement à l'échelle du
			// marché traité.
			switch (m)
			{
				case FpMmMarche.GC:		kMkt = 0.15;	break;
				case FpMmMarche.SI:		kMkt = 0.004;	break;
				case FpMmMarche.CL:		kMkt = 0.0045;	break;
				case FpMmMarche.NG:		kMkt = 0.00018;	break;
				case FpMmMarche.FDAX:	kMkt = 0.6;		break;
				case FpMmMarche.Perso:	kMkt = PK;		break;
				default:				kMkt = 1.0;		break;
			}
		}

		#endregion

		#region Utilitaires

		// Heure d'ouverture de la bougie courante en heure de Paris. Pine
		// indexe les bougies sur leur ouverture ; NinjaTrader sur leur clôture.
		private DateTime BarOpenParis()
		{
			// Globals est qualifié complètement : NinjaTrader.Core et
			// NinjaTrader.Gui exposent tous deux une classe de ce nom, et les
			// deux espaces de noms sont importés (ambiguïté CS0104 sinon).
			DateTime close = TimeZoneInfo.ConvertTime(Time[0], NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo, tzParis);
			return close.AddMinutes(-BarsPeriod.Value);
		}

		private static int ToMinutes(int h, int m)
		{
			return h * 60 + m;
		}

		// Reproduit inRange() de Pine, gestion du passage de minuit incluse.
		private bool InRange(int currentMin, int startMin, int offsetStart, int offsetEnd)
		{
			int sessionStart = startMin + offsetStart;
			int sessionEnd   = startMin + offsetEnd;

			if (sessionEnd >= 1440)
				return currentMin >= sessionStart || currentMin <= sessionEnd - 1440;

			if (sessionStart < 0)
				return currentMin <= sessionEnd || currentMin >= sessionStart + 1440;

			return currentMin >= sessionStart && currentMin <= sessionEnd;
		}

		private static bool JourDansListe(string liste, int jour)
		{
			if (string.IsNullOrWhiteSpace(liste))
				return false;

			return ("," + liste.Replace(" ", string.Empty) + ",").Contains("," + jour.ToString() + ",");
		}

		// ta.pivothigh(left, right) : la bougie située « right » barres en
		// arrière est un pivot si son extrême dépasse strictement celui des
		// « left » barres avant et des « right » barres après. Retourne NaN
		// s'il n'y a pas de pivot sur cette bougie.
		private double PivotHigh(int left, int right)
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

		private double PivotLow(int left, int right)
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

		private double LowestLow(int period)
		{
			double v = Low[0];
			for (int i = 1; i < period && i <= CurrentBar; i++)
				v = Math.Min(v, Low[i]);
			return v;
		}

		private double HighestHigh(int period)
		{
			double v = High[0];
			for (int i = 1; i < period && i <= CurrentBar; i++)
				v = Math.Max(v, High[i]);
			return v;
		}

		private double SmaVolume(int period)
		{
			double sum = 0;
			int n = 0;
			for (int i = 0; i < period && i <= CurrentBar; i++)
			{
				sum += Volume[i];
				n++;
			}
			return n == 0 ? 0 : sum / n;
		}

		#endregion

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0 || CurrentBar < BarsRequiredToTrade)
				return;

			// ═══ Contexte temporel (heure de Paris) ═══════════════════════════
			DateTime paris		= BarOpenParis();
			int hourCET			= paris.Hour;
			int minuteCET		= paris.Minute;
			int dayCET			= paris.Day;
			DayOfWeek dowCET	= paris.DayOfWeek;
			int currentMin		= hourCET * 60 + minuteCET;

			// ═══ Indicateurs de base ═════════════════════════════════════════
			double atr5		= ATR(5)[0];
			double atr14	= ATR(14)[0];
			double trNow	= Math.Max(High[0] - Low[0],
							  Math.Max(Math.Abs(High[0] - Close[1]), Math.Abs(Low[0] - Close[1])));
			double avgVol	= SmaVolume(5);

			// Bandes ATR : stop suiveur des trades « cross »
			double a  = ATR(5)[0] * M;
			double x  = a + High[0];		// niveau de stop des shorts
			double x2 = Low[0] - a;			// niveau de stop des longs

			// ═══ Paliers SL/TP mis à l'échelle du marché ══════════════════════
			double sp, tp;
			if (atr5 >= 20 * kMkt)		{ sp = 50   * kMkt; tp = 75    * kMkt; }
			else if (atr5 < 7 * kMkt)	{ sp = 16.5 * kMkt; tp = 24.75 * kMkt; }
			else						{ sp = 25   * kMkt; tp = 38.5  * kMkt; }

			double bodyHigh		= Math.Max(Open[0], Close[0]);
			double bodyLow		= Math.Min(Open[0], Close[0]);
			bool   candleBuy	= Close[0] > Open[0];
			bool   candleSell	= Open[0] > Close[0];

			// ═══ Créneaux de session ══════════════════════════════════════════
			bool isNews1430Today = JourDansListe(News1430Days, dayCET);
			bool isNews2000Today = JourDansListe(News2000Days, dayCET);

			int startUSA		= sAon ? ToMinutes(sAH, sAM) : -1;
			int startMidnight	= sBon ? ToMinutes(sBH, sBM) : -1;
			int startJapan		= sCon ? ToMinutes(sCH, sCM) : -1;
			int startLondon		= sDon ? ToMinutes(sDH, sDM) : -1;
			int startNews1430	= isNews1430Today ? ToMinutes(14, 29) : -1;
			int startNews2000	= isNews2000Today ? ToMinutes(19, 59) : -1;
			int startNewsHebdo	= (hebOn && dowCET == hebDow) ? ToMinutes(hebH, hebM) : -1;

			bool conditionStart =
				   (startUSA		>= 0 && InRange(currentMin, startUSA,		0, 0))
				|| (startMidnight	>= 0 && InRange(currentMin, startMidnight,	0, 0))
				|| (startJapan		>= 0 && InRange(currentMin, startJapan,		0, 0))
				|| (startLondon		>= 0 && InRange(currentMin, startLondon,	0, 0))
				|| (startNews1430	>= 0 && InRange(currentMin, startNews1430,	0, 0))
				|| (startNews2000	>= 0 && InRange(currentMin, startNews2000,	0, 0))
				|| (startNewsHebdo	>= 0 && InRange(currentMin, startNewsHebdo,	0, 0));

			// Fenêtres de trading : la session A/C/D démarre 1 minute après la
			// bougie de référence, le slot nuit (B) dès la bougie elle-même.
			bool tradeTimeUSA		= startUSA			>= 0 && InRange(currentMin, startUSA,		1, 90);
			bool tradeTimeMidnight	= startMidnight		>= 0 && InRange(currentMin, startMidnight,	0, 89);
			bool tradeTimeJapan		= startJapan		>= 0 && InRange(currentMin, startJapan,		1, 90);
			bool tradeTimeLondon	= startLondon		>= 0 && InRange(currentMin, startLondon,	1, 90);
			bool tradeTimeNews1430	= startNews1430		>= 0 && InRange(currentMin, startNews1430,	1, 60);
			bool tradeTimeNews2000	= startNews2000		>= 0 && InRange(currentMin, startNews2000,	1, 90);
			bool tradeTimeNewsHebdo	= startNewsHebdo	>= 0 && InRange(currentMin, startNewsHebdo,	1, 60);

			bool tradeTime = tradeTimeUSA || tradeTimeMidnight || tradeTimeJapan || tradeTimeLondon
						  || tradeTimeNews1430 || tradeTimeNews2000 || tradeTimeNewsHebdo;

			// Fenêtres élargies : mémorisation des pivots avant l'ouverture
			bool swingTimeUSA		= startUSA			>= 0 && InRange(currentMin, startUSA,		-59, 90);
			bool swingTimeMidnight	= startMidnight		>= 0 && InRange(currentMin, startMidnight,	-60, 89);
			bool swingTimeJapan		= startJapan		>= 0 && InRange(currentMin, startJapan,		-59, 90);
			bool swingTimeLondon	= startLondon		>= 0 && InRange(currentMin, startLondon,	-59, 90);
			bool swingTimeNews1430	= startNews1430		>= 0 && InRange(currentMin, startNews1430,	-59, 60);
			bool swingTimeNews2000	= startNews2000		>= 0 && InRange(currentMin, startNews2000,	-59, 90);
			bool swingTimeNewsHebdo	= startNewsHebdo	>= 0 && InRange(currentMin, startNewsHebdo,	-59, 60);

			bool swingTime = swingTimeUSA || swingTimeMidnight || swingTimeJapan || swingTimeLondon
						  || swingTimeNews1430 || swingTimeNews2000 || swingTimeNewsHebdo;

			double positionSize = Position.MarketPosition == MarketPosition.Long  ?  Position.Quantity
								: Position.MarketPosition == MarketPosition.Short ? -Position.Quantity
								: 0;

			// ═══ Remise à zéro quand la position est fermée ═══════════════════
			if (positionSize == 0)
			{
				entryPrice	= double.NaN;
				stopPrice	= double.NaN;
				tpPrice		= double.NaN;
				minAtrUp	= double.NaN;
				minAtrDown	= double.NaN;
				beTarget	= double.NaN;
				beArmed		= false;
			}

			// Une perte libère la fenêtre continuation pour un nouveau BOS
			bool justLost = positionSize == 0 && prevPositionSize != 0
						 && SystemPerformance.AllTrades.Count > 0
						 && SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1].ProfitCurrency < 0;

			if (justLost)
				continuePeriod = true;

			// ═══ Bougie de référence : zone fair price et biais ═══════════════
			if (conditionStart)
			{
				openValue  = Open[0];
				closeValue = Close[0];

				double trend30m = CurrentBar >= 30 ? (Close[0] - Close[30]) / Close[30] * 100 : 0;

				muchSell = trend30m < -0.2;
				muchBuy  = trend30m >  0.2;

				continuePeriod = true;

				// Note : sur la bougie de référence seul le slot nuit a déjà son
				// tradeTime actif (offset 0), les autres démarrent à +1 minute.
				// Comportement repris tel quel du script Pine.
				if (tradeTimeUSA)
					lastPeriod = double.NaN;

				if (tradeTimeMidnight)		lastPeriod = currentPeriod;
				else if (tradeTimeJapan)	lastPeriod = currentPeriod;
				else if (tradeTimeLondon)	lastPeriod = currentPeriod;

				currentPeriod = openValue;

				lastStartBar = CurrentBar;
			}

			double zoneHigh	= Math.Max(openValue, closeValue);
			double zoneLow	= Math.Min(openValue, closeValue);
			bool   refBull	= closeValue > openValue;
			bool   refBear	= openValue > closeValue;

			double buffer = 20 * kMkt;

			// Le prix doit être hors de la zone FP : au-dessous pour chercher un
			// long (retour vers le fair price), au-dessus pour un short.
			bool longTime  = tradeTime && High[0] < closeValue;
			bool shortTime = tradeTime && Low[0]  > closeValue;

			double bodySize		= Math.Abs(bodyHigh - bodyLow);
			double highWickSize	= High[0] - bodyHigh;
			double lowWickSize	= bodyLow - Low[0];

			if (longTime)	pushWickSize = highWickSize;
			if (shortTime)	pushWickSize = lowWickSize;

			bool tooMuchWick	= !double.IsNaN(pushWickSize) && pushWickSize > bodySize * 0.4;
			bool tooCloseOfMean	= bodyHigh >= zoneLow - buffer && bodyLow <= zoneHigh + buffer;
			bool canTrade		= !tooCloseOfMean;
			bool volCond		= Volume[0] > avgVol;

			int lastStart = lastStartBar < 0 ? int.MaxValue : CurrentBar - lastStartBar;

			// ═══ Filtre de volatilité ════════════════════════════════════════
			bool atrFilter;
			switch (AtrMode)
			{
				case FpMmAtrMode.Expansion20:		atrFilter = CurrentBar >= 20 && atr14 > ATR(14)[20];	break;
				case FpMmAtrMode.Expansion10:		atrFilter = CurrentBar >= 10 && atr14 > ATR(14)[10];	break;
				case FpMmAtrMode.TrSuperieurAtr:	atrFilter = trNow > atr14;							break;
				case FpMmAtrMode.Expansion20OuTr:	atrFilter = (CurrentBar >= 20 && atr14 > ATR(14)[20]) || trNow > atr14;	break;
				default:							atrFilter = true;									break;
			}

			// ═══ Pivots et biais structurel ══════════════════════════════════
			double pivHi1 = PivotHigh(SwingSizeL1, SwingSizeR1);
			double pivLo1 = PivotLow (SwingSizeL1, SwingSizeR1);
			double pivHi2 = PivotHigh(SwingSizeL2, SwingSizeR2);
			double pivLo2 = PivotLow (SwingSizeL2, SwingSizeR2);

			bool h1 = !double.IsNaN(pivHi1);
			bool l1 = !double.IsNaN(pivLo1);
			bool h2 = !double.IsNaN(pivHi2);
			bool l2 = !double.IsNaN(pivLo2);

			// BOS confirmé en clôture sur les pivots série 2 :
			// +1 = structure haussière, -1 = structure baissière
			if (StructBiasReset && conditionStart)
			{
				structBias	= 0;
				lastHi2		= double.NaN;
				lastLo2		= double.NaN;
			}

			if (h2) lastHi2 = pivHi2;
			if (l2) lastLo2 = pivLo2;

			if (!double.IsNaN(lastHi2) && Close[0] > lastHi2) structBias =  1;
			if (!double.IsNaN(lastLo2) && Close[0] < lastLo2) structBias = -1;

			double structBuffer	= 2.0  * kMkt;
			double minStopPts	= 10.0 * kMkt;
			double maxStopPts	= 50.0 * kMkt;

			double structStopLongLvl  = LowestLow(StopLookback)   - structBuffer;
			double structStopShortLvl = HighestHigh(StopLookback) + structBuffer;

			// ═══ Mémorisation des niveaux de pivot ═══════════════════════════
			// Série 1 (rejection) : uniquement du côté que l'on cherche à jouer.
			if (h1 && longTime)		niveauxSerie1.Add(new Niveau(pivHi1, "s1"));
			if (l1 && shortTime)	niveauxSerie1.Add(new Niveau(pivLo1, "s1"));

			// Série 2 (continuation) : mémorisée en permanence.
			if (h2) niveauxSerie2.Add(new Niveau(pivHi2, "high"));
			if (l2) niveauxSerie2.Add(new Niveau(pivLo2, "low"));

			// ═══ Détection des retests (fills) ═══════════════════════════════
			fillEvents.Clear();
			ManageLevels(niveauxSerie1, bodyHigh, bodyLow, swingTime);
			ManageLevels(niveauxSerie2, bodyHigh, bodyLow, swingTime);

			// ═══ Armement des setups ═════════════════════════════════════════
			if (conditionStart)
			{
				armContLongBar	= -1;
				armContShortBar	= -1;
				armRevLongBar	= -1;
				armRevShortBar	= -1;
				entriesWindow	= 0;
				rearmLongBar	= -1;
				rearmShortBar	= -1;
			}

			bool fillS2High	= fillEvents.Contains("high");
			bool fillS2Low	= fillEvents.Contains("low");
			bool anyFill	= fillEvents.Count > 0;

			bool contWindow    = tradeTime && !conditionStart && lastStart <= ContWinMin
							  && !tradeTimeMidnight && !tradeTimeUSA;
			bool bosContWindow = tradeTime && !conditionStart && lastStart <= ContWinMin
							  && !tradeTimeMidnight;

			// Extrêmes de session : la clôture au-delà arme un BOS
			bool bosDown = !conditionStart && !double.IsNaN(sessLo) && Close[0] < sessLo;
			bool bosUp   = !conditionStart && !double.IsNaN(sessHi) && Close[0] > sessHi;

			if (conditionStart)
			{
				sessLo = Low[0];
				sessHi = High[0];
			}
			else
			{
				sessLo = double.IsNaN(sessLo) ? Low[0]  : Math.Min(sessLo, Low[0]);
				sessHi = double.IsNaN(sessHi) ? High[0] : Math.Max(sessHi, High[0]);
			}

			// Armement sur retest de pivot
			if (fillS2High && contWindow)	armContLongBar	= CurrentBar;
			if (fillS2Low  && contWindow)	armContShortBar	= CurrentBar;
			if (anyFill && longTime)		armRevLongBar	= CurrentBar;
			if (anyFill && shortTime)		armRevShortBar	= CurrentBar;

			// Armement sur cassure BOS des extrêmes de session
			if (bosUp   && bosContWindow)	armContLongBar	= CurrentBar;
			if (bosDown && bosContWindow)	armContShortBar	= CurrentBar;
			if (bosDown && longTime)		armRevLongBar	= CurrentBar;
			if (bosUp   && shortTime)		armRevShortBar	= CurrentBar;

			// Ré-armement après un stop perdant, si la thèse reste intacte
			if (justLost && ReEnterAfterLoss)
			{
				if (longTime)
				{
					armRevLongBar	= CurrentBar;
					rearmLongBar	= CurrentBar;
				}
				if (shortTime)
				{
					armRevShortBar	= CurrentBar;
					rearmShortBar	= CurrentBar;
				}
			}

			// ═══ Filtres d'entrée ════════════════════════════════════════════
			bool structLongOk	= !UseStructFilterLong  || structBias ==  1;
			bool structShortOk	= !UseStructFilterShort || structBias == -1;
			bool refDirLongOk	= !UseRefDir || refBull;
			bool refDirShortOk	= !UseRefDir || refBear;

			bool engulfCond  = bodyHigh >= Math.Max(Open[1], Close[1]) && bodyLow <= Math.Min(Open[1], Close[1]);
			bool break2Long  = Close[0] > Math.Max(High[1], High[2]);
			bool break2Short = Close[0] < Math.Min(Low[1],  Low[2]);

			bool revOkLong  = RevConfirm == FpMmRevConfirm.Aucune
						   || (RevConfirm == FpMmRevConfirm.Engulfante ? engulfCond : break2Long);
			bool revOkShort = RevConfirm == FpMmRevConfirm.Aucune
						   || (RevConfirm == FpMmRevConfirm.Engulfante ? engulfCond : break2Short);

			// RR calculé sur le risque réel jusqu'au stop structurel, ou filtre
			// sp d'origine quand le stop structurel est désactivé.
			double riskLongNow  = Math.Min(Math.Max(Close[0] - structStopLongLvl,  minStopPts), maxStopPts);
			double riskShortNow = Math.Min(Math.Max(structStopShortLvl - Close[0], minStopPts), maxStopPts);

			bool rrLongOk  = UseStructStop ? (openValue - Close[0]) >= riskLongNow  * MinRR
										   : (openValue - Close[0]) >= sp;
			bool rrShortOk = UseStructStop ? (Close[0] - openValue) >= riskShortNow * MinRR
										   : (Close[0] - openValue) >= sp;

			bool wickOkLong		= !(highWickSize > bodySize * 0.4);
			bool wickOkShort	= !(lowWickSize  > bodySize * 0.4);

			bool entered	= false;
			bool flat		= positionSize == 0 && !HasPendingEntry();

			// ═══ Entrées ═════════════════════════════════════════════════════

			// Continuation long
			if (ArmOk(armContLongBar, ArmBars) && !entered && flat && continuePeriod
				&& entriesWindow < MaxEntriesWin
				&& refDirLongOk
				&& tradeTime && candleBuy && wickOkLong && Close[0] > High[1] && structLongOk)
			{
				EnterLong(QtyIn, "Long");
				tradeType = (swingTimeUSA || lastPeriod < Close[0]) ? "continue" : "crossContinue";
				armContLongBar = -1;
				entered = true;
				entriesWindow++;
			}

			// Continuation short
			if (ArmOk(armContShortBar, ArmBars) && !entered && flat && continuePeriod
				&& entriesWindow < MaxEntriesWin
				&& refDirShortOk
				&& tradeTime && candleSell && wickOkShort && Close[0] < Low[1] && structShortOk)
			{
				EnterShort(QtyIn, "Short");
				tradeType = (swingTimeUSA || lastPeriod > Close[0]) ? "continue" : "crossContinue";
				armContShortBar = -1;
				entered = true;
				entriesWindow++;
			}

			// Réversion long : cible = zone fair price
			if (ArmOk(armRevLongBar, ArmBars) && !entered && flat
				&& entriesWindow < MaxEntriesWin
				&& longTime && candleBuy && canTrade && volCond && !tooMuchWick && !muchSell
				&& atrFilter && Close[0] > High[1] && revOkLong && rrLongOk && structLongOk)
			{
				EnterLong(QtyIn, "Long");
				tradeType = (swingTimeUSA || lastPeriod < Close[0]) ? "simple" : "crossSimple";
				armRevLongBar = -1;
				entered = true;
				entriesWindow++;
			}

			// Réversion short : cible = zone fair price
			if (ArmOk(armRevShortBar, ArmBars) && !entered && flat
				&& entriesWindow < MaxEntriesWin
				&& shortTime && candleSell && canTrade && volCond && !tooMuchWick && !muchBuy
				&& atrFilter && Close[0] < Low[1] && revOkShort && rrShortOk && structShortOk)
			{
				EnterShort(QtyIn, "Short");
				tradeType = (swingTimeUSA || lastPeriod > Close[0]) ? "simple" : "crossSimple";
				armRevShortBar = -1;
				entered = true;
				entriesWindow++;
			}

			// Re-entrée allégée après SL : la thèse ayant déjà été validée à la
			// première entrée, seule la confirmation directionnelle est exigée.
			if (LiteReentry && ReEnterAfterLoss && ArmOk(rearmLongBar, ReArmBars) && !entered && flat
				&& entriesWindow < MaxEntriesWin
				&& longTime && candleBuy && canTrade && Close[0] > High[1] && structLongOk)
			{
				EnterLong(QtyIn, "Long");
				tradeType = (swingTimeUSA || lastPeriod < Close[0]) ? "simple" : "crossSimple";
				rearmLongBar = -1;
				entered = true;
				entriesWindow++;
			}

			if (LiteReentry && ReEnterAfterLoss && ArmOk(rearmShortBar, ReArmBars) && !entered && flat
				&& entriesWindow < MaxEntriesWin
				&& shortTime && candleSell && canTrade && Close[0] < Low[1] && structShortOk)
			{
				EnterShort(QtyIn, "Short");
				tradeType = (swingTimeUSA || lastPeriod > Close[0]) ? "simple" : "crossSimple";
				rearmShortBar = -1;
				entered = true;
				entriesWindow++;
			}

			// ═══ Gestion SL / TP selon le type de trade ══════════════════════
			// tradeType n'est jamais réinitialisé (comme en Pine) : les blocs
			// ci-dessous décrivent le dernier trade pris.

			if (tradeType == "continue")
			{
				if (positionSize > 0 && double.IsNaN(entryPrice))
				{
					entryPrice		= Position.AveragePrice;
					stopPrice		= entryPrice - LongStopDist(entryPrice, sp, structStopLongLvl, minStopPts, maxStopPts);
					tpPrice			= entryPrice + tp;
					beTarget		= entryPrice + (entryPrice - stopPrice);
					continuePeriod	= false;
				}

				if (positionSize < 0 && double.IsNaN(entryPrice))
				{
					entryPrice		= Position.AveragePrice;
					stopPrice		= entryPrice + ShortStopDist(entryPrice, sp, structStopShortLvl, minStopPts, maxStopPts);
					tpPrice			= entryPrice - tp;
					beTarget		= entryPrice - (stopPrice - entryPrice);
					continuePeriod	= false;
				}
			}

			if (tradeType == "simple")
			{
				if (positionSize > 0 && double.IsNaN(entryPrice))
				{
					entryPrice	= Position.AveragePrice;
					stopPrice	= entryPrice - LongStopDist(entryPrice, sp, structStopLongLvl, minStopPts, maxStopPts);
					tpPrice		= RevTP == FpMmRevTp.Atr15			? entryPrice + a
								: RevTP == FpMmRevTp.BordProcheFp	? zoneLow
								: openValue;
					beTarget	= entryPrice + (entryPrice - stopPrice);
				}

				if (positionSize < 0 && double.IsNaN(entryPrice))
				{
					entryPrice	= Position.AveragePrice;
					stopPrice	= entryPrice + ShortStopDist(entryPrice, sp, structStopShortLvl, minStopPts, maxStopPts);
					tpPrice		= RevTP == FpMmRevTp.Atr15			? entryPrice - a
								: RevTP == FpMmRevTp.BordProcheFp	? zoneHigh
								: openValue;
					beTarget	= entryPrice - (stopPrice - entryPrice);
				}
			}

			// Trades « cross » : stop suiveur sur les bandes ATR, cible = la
			// zone fair price de la session précédente (lastPeriod).
			if (tradeType == "crossContinue")
			{
				if (positionSize != 0 && double.IsNaN(minAtrUp) && double.IsNaN(minAtrDown) && continuePeriod)
				{
					minAtrUp	= x;
					minAtrDown	= x2;
					double avgP	= Position.AveragePrice;
					beTarget	= positionSize > 0 ? avgP + (avgP - minAtrDown) : avgP - (minAtrUp - avgP);
					continuePeriod = false;
				}

				if (!double.IsNaN(minAtrUp))	minAtrUp   = Math.Min(minAtrUp, x);
				if (!double.IsNaN(minAtrDown))	minAtrDown = Math.Max(minAtrDown, x2);

				tpPrice = lastPeriod;

				if (positionSize > 0) stopPrice = minAtrDown;
				if (positionSize < 0) stopPrice = minAtrUp;
			}

			if (tradeType == "crossSimple")
			{
				if (positionSize != 0 && double.IsNaN(minAtrUp) && double.IsNaN(minAtrDown))
				{
					minAtrUp	= x;
					minAtrDown	= x2;
					double avgP	= Position.AveragePrice;
					beTarget	= positionSize > 0 ? avgP + (avgP - minAtrDown) : avgP - (minAtrUp - avgP);
				}

				if (!double.IsNaN(minAtrUp))	minAtrUp   = Math.Min(minAtrUp, x);
				if (!double.IsNaN(minAtrDown))	minAtrDown = Math.Max(minAtrDown, x2);

				tpPrice = lastPeriod;

				if (positionSize > 0) stopPrice = minAtrDown;
				if (positionSize < 0) stopPrice = minAtrUp;
			}

			// ═══ Break-even à 1R ═════════════════════════════════════════════
			bool beActive = (tradeType == "continue" || tradeType == "crossContinue") ? UseBE : UseBERev;

			if (beActive && !beArmed && !double.IsNaN(beTarget) && positionSize != 0)
			{
				if (positionSize > 0 && High[0] >= beTarget) beArmed = true;
				if (positionSize < 0 && Low[0]  <= beTarget) beArmed = true;
			}

			if (beArmed && positionSize > 0)
				stopPrice = double.IsNaN(stopPrice) ? Position.AveragePrice : Math.Max(stopPrice, Position.AveragePrice);

			if (beArmed && positionSize < 0)
				stopPrice = double.IsNaN(stopPrice) ? Position.AveragePrice : Math.Min(stopPrice, Position.AveragePrice);

			// ═══ Clôture au profit fixe ══════════════════════════════════════
			if (UseProfitClose && positionSize != 0)
			{
				double pointValue		= Instrument.MasterInstrument.PointValue;
				double profitOffset		= ProfitCloseUsd / (pointValue * Math.Abs(positionSize));

				if (positionSize > 0)
				{
					double limL = Position.AveragePrice + profitOffset;
					tpPrice = double.IsNaN(tpPrice) ? limL : Math.Min(tpPrice, limL);
				}
				if (positionSize < 0)
				{
					double limS = Position.AveragePrice - profitOffset;
					tpPrice = double.IsNaN(tpPrice) ? limS : Math.Max(tpPrice, limS);
				}
			}

			// ═══ Pose des ordres de sortie ═══════════════════════════════════
			ApplyExits(positionSize);

			// ═══ Sortie « fair price atteint » ═══════════════════════════════
			// Une bougie qui ouvre hors zone FP et clôture dedans solde la
			// position ; les continuations peuvent en être exemptées.
			double highFP		= Math.Max(openValue, closeValue);
			double lowFP		= Math.Min(openValue, closeValue);
			bool closeInFP		= Close[0] > lowFP && Close[0] < highFP;
			bool openOutFP		= !(Open[0] > lowFP && Open[0] < highFP);
			bool fairPriceCond	= closeInFP && openOutFP;
			bool fpExemptCont	= !UseFpCloseCont && (tradeType == "continue" || tradeType == "crossContinue");

			if (fairPriceCond && !fpExemptCont && positionSize != 0)
			{
				if (positionSize > 0) ExitLong("Fair price reached", "Long");
				if (positionSize < 0) ExitShort("Fair price reached", "Short");
			}

			// ═══ Suivi d'état pour la bougie suivante ════════════════════════
			prevPositionSize = positionSize;
		}

		#region Sous-programmes

		// Reproduit manageLevel() : un niveau touché par le corps de la bougie
		// est consommé et génère un événement de fill ; hors fenêtre swingTime
		// les niveaux sont simplement oubliés. Parcours arrière pour que les
		// suppressions ne décalent pas les indices restant à traiter.
		private void ManageLevels(List<Niveau> niveaux, double bodyHigh, double bodyLow, bool swingTime)
		{
			for (int j = niveaux.Count - 1; j >= 0; j--)
			{
				double level = niveaux[j].Prix;
				bool filled  = bodyHigh >= level && bodyLow <= level;

				if (filled)
				{
					fillEvents.Add(niveaux[j].Type);
					niveaux.RemoveAt(j);
					continue;
				}

				if (!swingTime)
					niveaux.RemoveAt(j);
			}
		}

		private bool ArmOk(int armBar, int validite)
		{
			return armBar >= 0 && CurrentBar - armBar <= validite;
		}

		// Distance de stop structurelle bornée [min, max], sinon palier ATR sp
		private double LongStopDist(double ep, double sp, double structLvl, double minPts, double maxPts)
		{
			return UseStructStop ? Math.Min(Math.Max(ep - structLvl, minPts), maxPts) : sp;
		}

		private double ShortStopDist(double ep, double sp, double structLvl, double minPts, double maxPts)
		{
			return UseStructStop ? Math.Min(Math.Max(structLvl - ep, minPts), maxPts) : sp;
		}

		// Un ordre d'entrée soumis mais pas encore rempli doit bloquer une
		// nouvelle entrée : en OnBarClose l'ordre marché part à l'ouverture de
		// la bougie suivante, la position est donc encore à zéro ici.
		private bool HasPendingEntry()
		{
			foreach (Order o in Orders)
			{
				if ((o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted
					|| o.OrderState == OrderState.Submitted)
					&& (o.Name == "Long" || o.Name == "Short"))
					return true;
			}
			return false;
		}

		// strategy.exit(stop=, limit=) de Pine → SetStopLoss / SetProfitTarget,
		// qui sont OCO nativement dans NinjaTrader.
		//
		// Écart : Pine exécute stop et limite en intrabar. En OnBarClose un
		// niveau déjà franchi à la clôture ne peut plus être posé (NinjaTrader
		// rejetterait l'ordre) : la position est alors soldée au marché à la
		// bougie suivante — sortie légèrement moins favorable que le backtest
		// TradingView. Passer OrderFillResolution en High avec une série 1 tick
		// pour se rapprocher du remplissage intrabar.
		private void ApplyExits(double positionSize)
		{
			if (positionSize == 0)
				return;

			bool isLong		= positionSize > 0;
			string signal	= isLong ? "Long" : "Short";
			bool soldeAuMarche = false;

			if (!double.IsNaN(stopPrice))
			{
				bool stopDejaFranchi = isLong ? stopPrice >= Close[0] : stopPrice <= Close[0];

				if (stopDejaFranchi)
					soldeAuMarche = true;
				else
					SetStopLoss(signal, CalculationMode.Price, stopPrice, false);
			}

			if (!double.IsNaN(tpPrice) && !soldeAuMarche)
			{
				bool tpDejaAtteint = isLong ? tpPrice <= Close[0] : tpPrice >= Close[0];

				if (tpDejaAtteint)
					soldeAuMarche = true;
				else
					// Surcharge à 3 arguments : contrairement à SetStopLoss,
					// SetProfitTarget n'a pas de paramètre booléen final.
					SetProfitTarget(signal, CalculationMode.Price, tpPrice);
			}

			if (soldeAuMarche)
			{
				if (isLong)	ExitLong("SL/TP LONG", "Long");
				else		ExitShort("SL/TP Short", "Short");
			}
		}

		#endregion
	}
}
