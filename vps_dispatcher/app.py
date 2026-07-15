"""Webhook dispatcher TradingView → Tradovate (multi-comptes, multi-firms).

Reçoit les alertes JSON du script Pine « FP Multi-Marchés 1mn » (v7, groupe
d'inputs Webhook) et route chaque événement :

  buy / sell    → choisir_compte (rotation 1 trade/compte/jour, corrélation,
                  blackout, kill switch) puis entrée marché + brackets SL/TP
  breakeven     → remonter le stop au prix de fill RÉEL du compte en position
  modify_stop   → suivre le stop suiveur envoyé par le script (types cross)
  flatten       → annuler les brackets puis liquider (fair price atteint)

Payload attendu (produit par whAlert dans le script Pine) :
{"secret":"...","strategy":"FP_MULTI_1MN","symbol":"NQ","ticker":"CME_MINI:NQ1!",
 "action":"buy","tradeType":"continue","qty":10,"price":23145.25,
 "stopLoss":23120.25,"takeProfit":23183.75,"time":"2026-07-14 15:42:00"}

Lancement : uvicorn app:app --host 0.0.0.0 --port 8000
"""

import asyncio
import hmac
import logging
import os
from contextlib import asynccontextmanager

from fastapi import FastAPI, HTTPException, Request

from state_manager import StateManager
from tradovate import TradovateHub, TradovateError

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(message)s",
)
log = logging.getLogger("dispatcher")

sm: StateManager | None = None
hub: TradovateHub | None = None
verrou = asyncio.Lock()          # un seul événement traité à la fois
recents: list[str] = []          # anti-doublon (retries TradingView)


@asynccontextmanager
async def lifespan(app: FastAPI):
    global sm, hub
    sm = StateManager()
    hub = TradovateHub(sm.config)
    log.info("dispatcher démarré — %d comptes configurés",
             len(sm.config.get("comptes", [])))
    yield
    await hub.close()


app = FastAPI(lifespan=lifespan)


def _verifier_secret(payload: dict):
    attendu = os.environ.get(
        sm.config["global"].get("webhook_secret_env", "WEBHOOK_SECRET"), "")
    if not attendu:
        raise HTTPException(500, "WEBHOOK_SECRET non défini côté serveur")
    if not hmac.compare_digest(str(payload.get("secret", "")), attendu):
        raise HTTPException(403, "secret invalide")


def _doublon(payload: dict) -> bool:
    cle = f"{payload.get('action')}|{payload.get('symbol')}|{payload.get('time')}"
    if cle in recents:
        return True
    recents.append(cle)
    del recents[:-50]
    return False


# ── Handlers d'événements ────────────────────────────────────────────────────

async def _entree(payload: dict) -> dict:
    symbol = payload["symbol"]
    sens = payload["action"]  # buy | sell

    compte, refus = sm.choisir_compte(symbol)
    if compte is None:
        log.info("entrée %s %s REFUSÉE : %s", sens, symbol, refus)
        return {"statut": "ignoré", "raison": refus}

    session = hub.session(compte["firm"])
    contrat = await session.contrat_front(
        hub.client, symbol, hub.contrats_forces.get(symbol.upper()))
    qty = sm.qty_pour(compte, symbol)
    stop = payload.get("stopLoss")
    tp = payload.get("takeProfit")

    resultat = await session.entree_bracket(
        hub.client, compte, sens, contrat, qty, stop, tp)
    oso_id = resultat.get("orderId") or resultat.get("osoId")

    sm.enregistrer_entree(compte["id"], symbol, sens, qty, contrat, oso_id)
    log.info("ENTRÉE %s %s x%d sur %s (%s) SL=%s TP=%s type=%s",
             sens, contrat, qty, compte["id"], compte["firm"],
             stop, tp, payload.get("tradeType"))
    return {"statut": "exécuté", "compte": compte["id"], "contrat": contrat,
            "qty": qty, "ordre": oso_id}


async def _breakeven(payload: dict) -> dict:
    compte, pos = sm.compte_en_position(payload["symbol"])
    if compte is None:
        return {"statut": "ignoré", "raison": "aucun compte en position"}
    session = hub.session(compte["firm"])
    # break-even = prix de fill RÉEL du compte, pas le prix du payload
    prix = await session.prix_moyen_position(hub.client, compte)
    if prix is None:
        prix = payload.get("stopLoss") or payload.get("price")
    ok = await session.deplacer_stop(hub.client, compte, pos["contrat"], prix)
    log.info("BREAK-EVEN %s → stop %.2f (%s)", compte["id"], prix,
             "modifié" if ok else "stop introuvable")
    return {"statut": "exécuté" if ok else "ignoré", "compte": compte["id"],
            "stop": prix}


async def _modify_stop(payload: dict) -> dict:
    compte, pos = sm.compte_en_position(payload["symbol"])
    if compte is None or payload.get("stopLoss") is None:
        return {"statut": "ignoré", "raison": "pas de position ou pas de stop"}
    session = hub.session(compte["firm"])
    ok = await session.deplacer_stop(
        hub.client, compte, pos["contrat"], float(payload["stopLoss"]))
    log.info("MODIFY_STOP %s → %.2f (%s)", compte["id"],
             float(payload["stopLoss"]), "modifié" if ok else "stop introuvable")
    return {"statut": "exécuté" if ok else "ignoré", "compte": compte["id"]}


async def _modify_tp(payload: dict) -> dict:
    compte, pos = sm.compte_en_position(payload["symbol"])
    if compte is None or payload.get("takeProfit") is None:
        return {"statut": "ignoré", "raison": "pas de position ou pas de TP"}
    session = hub.session(compte["firm"])
    ok = await session.deplacer_tp(
        hub.client, compte, pos["contrat"], float(payload["takeProfit"]))
    log.info("MODIFY_TP %s → %.2f (%s)", compte["id"],
             float(payload["takeProfit"]), "modifié" if ok else "TP introuvable")
    return {"statut": "exécuté" if ok else "ignoré", "compte": compte["id"]}


async def _flatten(payload: dict) -> dict:
    compte, pos = sm.compte_en_position(payload["symbol"])
    if compte is None:
        return {"statut": "ignoré", "raison": "aucun compte en position"}
    session = hub.session(compte["firm"])
    await session.flatten(hub.client, compte, pos["contrat"])
    sm.cloturer_position(compte["id"])
    log.info("FLATTEN %s (%s)", compte["id"], pos["contrat"])
    return {"statut": "exécuté", "compte": compte["id"]}


HANDLERS = {
    "buy": _entree,
    "sell": _entree,
    "breakeven": _breakeven,
    "modify_stop": _modify_stop,
    "modify_tp": _modify_tp,
    "flatten": _flatten,
}


# ── Endpoints ────────────────────────────────────────────────────────────────

@app.post("/webhook")
async def webhook(request: Request):
    try:
        payload = await request.json()
    except Exception:
        raise HTTPException(400, "JSON invalide")

    _verifier_secret(payload)

    action = payload.get("action")
    if action not in HANDLERS:
        raise HTTPException(400, f"action inconnue : {action}")
    if _doublon(payload):
        return {"statut": "ignoré", "raison": "doublon"}

    async with verrou:
        try:
            return await HANDLERS[action](payload)
        except TradovateError as e:
            log.error("Tradovate : %s", e)
            raise HTTPException(502, str(e))


@app.get("/status")
async def status():
    return sm.resume()


@app.get("/health")
async def health():
    return {"ok": True}
