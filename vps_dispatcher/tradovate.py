"""Client REST Tradovate — une session par firme (tokens séparés).

Chaque prop firm fournit un environnement Tradovate distinct : les identifiants
viennent de variables d'environnement (jamais dans accounts.json). Les tokens
expirent après ~80 minutes → renouvellement automatique avec marge de 10 min.

Endpoints utilisés :
- POST /v1/auth/accesstokenrequest        → accessToken + expirationTime
- GET  /v1/contract/suggest?t=NQ&l=10     → résolution du contrat front month
- POST /v1/order/placeoso                 → entrée marché + brackets stop/TP
- POST /v1/order/placeorder               → entrée simple si pas de bracket
- GET  /v1/order/list / /v1/order/item    → retrouver le stop actif
- POST /v1/order/modifyorder              → déplacer le stop (break-even, trailing)
- POST /v1/order/cancelorder              → annuler les brackets avant flatten
- POST /v1/order/liquidateposition        → flatten
"""

import os
import time
from datetime import datetime, timezone

import httpx

URLS = {
    "demo": "https://demo.tradovateapi.com/v1",
    "live": "https://live.tradovateapi.com/v1",
}


class TradovateError(RuntimeError):
    pass


def _env(nom_var: str, obligatoire: bool = True) -> str:
    val = os.environ.get(nom_var, "")
    if obligatoire and not val:
        raise TradovateError(f"variable d'environnement manquante : {nom_var}")
    return val


class FirmSession:
    """Session authentifiée pour une firme (un login Tradovate)."""

    def __init__(self, nom: str, cfg: dict):
        self.nom = nom
        self.base = URLS[cfg.get("environnement", "demo")]
        self.cfg = cfg
        self.token: str | None = None
        self.expire_a: float = 0.0
        self._contrats: dict[str, tuple[str, float]] = {}  # root -> (nom contrat, ts cache)

    # ── Authentification ─────────────────────────────────────────────────────
    async def _auth(self, client: httpx.AsyncClient):
        body = {
            "name": _env(self.cfg["username_env"]),
            "password": _env(self.cfg["password_env"]),
            "appId": self.cfg.get("app_id", "FPDispatcher"),
            "appVersion": self.cfg.get("app_version", "1.0"),
            "deviceId": f"vps-{self.nom}",
            "cid": _env(self.cfg["cid_env"]),
            "sec": _env(self.cfg["sec_env"]),
        }
        r = await client.post(f"{self.base}/auth/accesstokenrequest", json=body)
        r.raise_for_status()
        data = r.json()
        if "accessToken" not in data:
            raise TradovateError(f"[{self.nom}] auth refusée : {data}")
        self.token = data["accessToken"]
        # expirationTime au format ISO — marge de sécurité 10 min
        exp = datetime.fromisoformat(data["expirationTime"].replace("Z", "+00:00"))
        self.expire_a = exp.timestamp() - 600

    async def _headers(self, client: httpx.AsyncClient) -> dict:
        if not self.token or time.time() >= self.expire_a:
            await self._auth(client)
        return {"Authorization": f"Bearer {self.token}"}

    async def _get(self, client, chemin, **params):
        r = await client.get(f"{self.base}{chemin}", params=params,
                             headers=await self._headers(client))
        r.raise_for_status()
        return r.json()

    async def _post(self, client, chemin, body):
        r = await client.post(f"{self.base}{chemin}", json=body,
                              headers=await self._headers(client))
        r.raise_for_status()
        data = r.json()
        if isinstance(data, dict) and data.get("failureReason"):
            raise TradovateError(f"[{self.nom}] {chemin} : {data['failureReason']} "
                                 f"{data.get('failureText', '')}")
        return data

    # ── Contrats ─────────────────────────────────────────────────────────────
    async def contrat_front(self, client, root: str, force: str | None = None) -> str:
        """Nom du contrat front month (ex. NQ → NQU6). Cache 1 h."""
        if force:
            return force
        root = root.upper()
        nom, ts = self._contrats.get(root, (None, 0.0))
        if nom and time.time() - ts < 3600:
            return nom
        data = await self._get(client, "/contract/suggest", t=root, l=10)
        candidats = [c["name"] for c in data
                     if c.get("name", "").startswith(root)
                     and len(c["name"]) <= len(root) + 2]
        if not candidats:
            raise TradovateError(f"[{self.nom}] aucun contrat trouvé pour {root}")
        nom = sorted(candidats)[0] if len(candidats) == 1 else candidats[0]
        self._contrats[root] = (nom, time.time())
        return nom

    # ── Ordres ───────────────────────────────────────────────────────────────
    async def entree_bracket(self, client, compte: dict, sens: str, contrat: str,
                             qty: int, stop: float | None, tp: float | None) -> dict:
        """Entrée marché + brackets. sens = 'buy' ou 'sell'."""
        action = "Buy" if sens == "buy" else "Sell"
        oppose = "Sell" if sens == "buy" else "Buy"
        base = {
            "accountSpec": compte["account_spec"],
            "accountId": compte["account_id"],
            "action": action,
            "symbol": contrat,
            "orderQty": qty,
            "orderType": "Market",
            "isAutomated": True,
        }
        brackets = []
        if stop is not None:
            brackets.append({"action": oppose, "orderType": "Stop", "stopPrice": stop})
        if tp is not None:
            brackets.append({"action": oppose, "orderType": "Limit", "price": tp})

        if brackets:
            body = dict(base)
            body["bracket1"] = brackets[0]
            if len(brackets) > 1:
                body["bracket2"] = brackets[1]
            return await self._post(client, "/order/placeoso", body)
        return await self._post(client, "/order/placeorder", base)

    async def _ordres_actifs(self, client, compte: dict, contrat_id_ou_nom: str) -> list[dict]:
        ordres = await self._get(client, "/order/list")
        actifs = []
        for o in ordres:
            if o.get("accountId") != compte["account_id"]:
                continue
            if o.get("ordStatus") not in ("Working", "Suspended"):
                continue
            actifs.append(o)
        return actifs

    async def _versions_ordre(self, client, order_id: int) -> dict:
        versions = await self._get(client, "/orderVersion/deps", masterid=order_id)
        return versions[-1] if versions else {}

    async def deplacer_stop(self, client, compte: dict, contrat: str,
                            nouveau_stop: float) -> bool:
        """Retrouve le stop actif du compte et le déplace. True si modifié."""
        for o in await self._ordres_actifs(client, compte, contrat):
            version = await self._versions_ordre(client, o["id"])
            if version.get("orderType") != "Stop":
                continue
            body = {
                "orderId": o["id"],
                "orderQty": version.get("orderQty", 1),
                "orderType": "Stop",
                "stopPrice": nouveau_stop,
                "isAutomated": True,
            }
            await self._post(client, "/order/modifyorder", body)
            return True
        return False

    async def flatten(self, client, compte: dict, contrat: str):
        """Annule les brackets restants PUIS liquide la position.

        Ordre volontaire (leçon du piège Pine cancel/close) : on annule d'abord
        pour qu'un bracket ne se déclenche pas pendant la liquidation.
        """
        for o in await self._ordres_actifs(client, compte, contrat):
            try:
                await self._post(client, "/order/cancelorder",
                                 {"orderId": o["id"], "isAutomated": True})
            except TradovateError:
                pass  # déjà exécuté/annulé entre le list et le cancel

        positions = await self._get(client, "/position/list")
        for p in positions:
            if p.get("accountId") == compte["account_id"] and p.get("netPos", 0) != 0:
                await self._post(client, "/order/liquidateposition",
                                 {"accountId": compte["account_id"],
                                  "contractId": p["contractId"],
                                  "admin": False})

    async def prix_moyen_position(self, client, compte: dict) -> float | None:
        """Prix d'entrée réel de la position ouverte (pour le break-even)."""
        positions = await self._get(client, "/position/list")
        for p in positions:
            if p.get("accountId") == compte["account_id"] and p.get("netPos", 0) != 0:
                if p.get("netPrice") is not None:
                    return float(p["netPrice"])
        return None


class TradovateHub:
    """Une FirmSession par firme, partagée par tout le dispatcher."""

    def __init__(self, config: dict):
        self.sessions = {nom: FirmSession(nom, cfg)
                         for nom, cfg in config.get("firms", {}).items()}
        self.contrats_forces = {
            k: v for k, v in (config.get("contrats_forces") or {}).items()
            if v and k != "commentaire"
        }
        self.client = httpx.AsyncClient(timeout=15.0)

    def session(self, firm: str) -> FirmSession:
        if firm not in self.sessions:
            raise TradovateError(f"firme inconnue dans la config : {firm}")
        return self.sessions[firm]

    async def close(self):
        await self.client.aclose()
