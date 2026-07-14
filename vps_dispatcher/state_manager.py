"""Gestion d'état multi-comptes / multi-firms.

Règles prop firms appliquées ici (et nulle part ailleurs) :
- 1 trade max par compte par jour (jour calendaire Europe/Paris)
- jamais deux comptes en position sur le même groupe de corrélation
- rotation séquentielle : chaque signal part sur le compte éligible suivant
- kill switch : présence du fichier PAUSE à côté du state → aucun nouveau trade

L'état persiste dans state.json pour survivre aux redémarrages du service.
"""

import json
import os
from datetime import datetime
from pathlib import Path
from zoneinfo import ZoneInfo

BASE_DIR = Path(__file__).resolve().parent
CONFIG_FILE = BASE_DIR / "accounts.json"
STATE_FILE = BASE_DIR / "state.json"


class StateManager:
    def __init__(self, config_path: Path = CONFIG_FILE, state_path: Path = STATE_FILE):
        self.config = json.loads(Path(config_path).read_text(encoding="utf-8"))
        self.state_path = Path(state_path)
        self.tz = ZoneInfo(self.config["global"].get("timezone", "Europe/Paris"))
        self.max_trades_jour = int(self.config["global"].get("max_trades_par_compte_par_jour", 1))
        self.kill_switch = BASE_DIR / self.config["global"].get("kill_switch_file", "PAUSE")
        self._load_state()

    # ── Persistance ──────────────────────────────────────────────────────────
    def _load_state(self):
        if self.state_path.exists():
            self.state = json.loads(self.state_path.read_text(encoding="utf-8"))
        else:
            self.state = {"date": self._aujourdhui(), "rotation_index": 0, "comptes": {}}
        self._reset_si_nouveau_jour()

    def _save_state(self):
        tmp = self.state_path.with_suffix(".tmp")
        tmp.write_text(json.dumps(self.state, indent=2, ensure_ascii=False), encoding="utf-8")
        os.replace(tmp, self.state_path)

    def _aujourdhui(self) -> str:
        return datetime.now(self.tz).strftime("%Y-%m-%d")

    def _reset_si_nouveau_jour(self):
        if self.state.get("date") != self._aujourdhui():
            self.state["date"] = self._aujourdhui()
            for c in self.state.get("comptes", {}).values():
                c["trades_aujourdhui"] = 0
                # on ne touche pas à "position" : une position ouverte survit à minuit
            self._save_state()

    def _etat_compte(self, compte_id: str) -> dict:
        return self.state["comptes"].setdefault(
            compte_id, {"trades_aujourdhui": 0, "position": None}
        )

    # ── Corrélation ──────────────────────────────────────────────────────────
    def groupe_de(self, symbol: str) -> str:
        for groupe, roots in self.config.get("groupes_correlation", {}).items():
            if symbol.upper() in [r.upper() for r in roots]:
                return groupe
        return symbol.upper()  # symbole inconnu = son propre groupe

    def _groupe_occupe(self, groupe: str) -> bool:
        for c in self.state["comptes"].values():
            pos = c.get("position")
            if pos and self.groupe_de(pos["symbol"]) == groupe:
                return True
        return False

    # ── Blackout news ────────────────────────────────────────────────────────
    def en_blackout(self) -> str | None:
        now = datetime.now(self.tz)
        for b in self.config.get("blackouts_news", []):
            debut = datetime.strptime(b["debut"], "%Y-%m-%d %H:%M").replace(tzinfo=self.tz)
            fin = datetime.strptime(b["fin"], "%Y-%m-%d %H:%M").replace(tzinfo=self.tz)
            if debut <= now <= fin:
                return b.get("motif", "news")
        return None

    # ── Sélection du compte (rotation) ───────────────────────────────────────
    def choisir_compte(self, symbol: str) -> tuple[dict | None, str]:
        """Retourne (compte, raison_du_refus). compte=None si aucun éligible."""
        self._reset_si_nouveau_jour()

        if self.kill_switch.exists():
            return None, "kill switch actif (fichier PAUSE présent)"

        motif = self.en_blackout()
        if motif:
            return None, f"blackout news : {motif}"

        groupe = self.groupe_de(symbol)
        if self._groupe_occupe(groupe):
            return None, f"position déjà ouverte sur le groupe {groupe} (règle corrélation)"

        comptes = [c for c in self.config["comptes"] if c.get("enabled")]
        if not comptes:
            return None, "aucun compte activé dans la configuration"

        n = len(comptes)
        depart = self.state.get("rotation_index", 0) % n
        for k in range(n):
            compte = comptes[(depart + k) % n]
            etat = self._etat_compte(compte["id"])
            if etat["trades_aujourdhui"] >= self.max_trades_jour:
                continue
            if etat.get("position"):
                continue
            # compte retenu : la rotation repart après lui
            self.state["rotation_index"] = (depart + k + 1) % n
            self._save_state()
            return compte, ""
        return None, "tous les comptes ont déjà tradé aujourd'hui ou sont en position"

    # ── Cycle de vie d'un trade ──────────────────────────────────────────────
    def enregistrer_entree(self, compte_id: str, symbol: str, sens: str,
                           qty: int, contrat: str, oso_id=None):
        etat = self._etat_compte(compte_id)
        etat["trades_aujourdhui"] += 1
        etat["position"] = {
            "symbol": symbol,
            "sens": sens,
            "qty": qty,
            "contrat": contrat,
            "oso_id": oso_id,
            "ouvert_a": datetime.now(self.tz).isoformat(timespec="seconds"),
        }
        self._save_state()

    def compte_en_position(self, symbol: str) -> tuple[dict | None, dict | None]:
        """Retrouve (config_compte, position) du compte en position sur ce symbole."""
        for compte in self.config["comptes"]:
            etat = self.state["comptes"].get(compte["id"])
            pos = etat.get("position") if etat else None
            if pos and pos["symbol"].upper() == symbol.upper():
                return compte, pos
        return None, None

    def cloturer_position(self, compte_id: str):
        etat = self._etat_compte(compte_id)
        etat["position"] = None
        self._save_state()

    # ── Divers ───────────────────────────────────────────────────────────────
    def qty_pour(self, compte: dict, symbol: str) -> int:
        q = compte.get("qty", {})
        return int(q.get(symbol.upper(), q.get("default", 1)))

    def resume(self) -> dict:
        self._reset_si_nouveau_jour()
        return {
            "date": self.state["date"],
            "kill_switch": self.kill_switch.exists(),
            "blackout": self.en_blackout(),
            "rotation_index": self.state.get("rotation_index", 0),
            "comptes": {
                c["id"]: {
                    "enabled": c.get("enabled", False),
                    **self.state["comptes"].get(
                        c["id"], {"trades_aujourdhui": 0, "position": None}
                    ),
                }
                for c in self.config["comptes"]
            },
        }
