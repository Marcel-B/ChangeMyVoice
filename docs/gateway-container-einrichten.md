# Gateway-Container auf Proxmox einrichten

Schritt-für-Schritt-Anleitung für einen neuen LXC-Container, der das
ChangeMyVoice-Gateway betreibt und über Tailscale mit dem Mac spricht.

Das Vorbild ist der bestehende `stem`-Container: ein eigener Tailscale-Knoten,
erreichbar über einen Namen unter `idsrv.info`, der im Reverse Proxy auf den
Container zeigt.

> Die Tailscale-Schritte sind gegen die offizielle Dokumentation geprüft
> ([LXC](https://tailscale.com/kb/1130/lxc-unprivileged),
> [Linux-Installation](https://tailscale.com/kb/1031/install-linux)).

---

## 1. Container anlegen

Auf der Proxmox-Oberfläche einen unprivilegierten LXC-Container anlegen:

| Einstellung | Wert |
| --- | --- |
| Vorlage | Debian 12 (bookworm) oder Debian 13 (trixie) |
| Festplatte | 8 GB genügen — es liegen keine Audiodateien im Container |
| Arbeitsspeicher | 512 MB |
| Kerne | 1 |
| Hostname | `voice` |

Das Gateway ist zustandslos und reicht nur durch; große Uploads wandern
gestreamt hindurch und belegen keinen Arbeitsspeicher. Mehr Ressourcen bringen
hier nichts.

Die Container-ID im Folgenden als `<CTID>` geschrieben.

## 2. Zwei Berechtigungen setzen

Auf dem **Proxmox-Host**, bei gestopptem Container:

```bash
# nesting: Docker im LXC. keyctl: von Tailscale empfohlen.
pct set <CTID> --features keyctl=1,nesting=1

# Tailscale braucht ein TUN-Gerät, das unprivilegierte Container nicht mitbringen.
pct set <CTID> --dev0 /dev/net/tun
```

Alternativ direkt in `/etc/pve/lxc/<CTID>.conf` (Proxmox 7 und neuer):

```
lxc.cgroup2.devices.allow: c 10:200 rwm
lxc.mount.entry: /dev/net/tun dev/net/tun none bind,create=file
```

Danach den Container starten. Läuft er bereits, muss er **gestoppt und neu
gestartet** werden — ein Neustart aus dem Container heraus genügt nicht.

Prüfen, ob es geklappt hat:

```bash
pct enter <CTID>
ls -l /dev/net/tun      # muss existieren
```

Fehlt das Gerät, greift Schritt 2 nicht — ohne es kommt Tailscale nicht hoch.
Als Ausweg gibt es den Userspace-Modus (siehe unten), der aber Einschränkungen
hat.

## 3. Tailscale installieren

Im Container:

```bash
apt-get update && apt-get install -y curl
curl -fsSL https://tailscale.com/install.sh | sh
```

Das Skript erkennt die Distribution und richtet Paketquelle und Dienst ein. Bei
Debian und Ubuntu startet und aktiviert das Paket `tailscaled` selbst, ein
zusätzliches `systemctl enable` ist nicht nötig.

Wer die Paketquelle lieber von Hand einträgt (Beispiel Debian 12):

```bash
mkdir -p --mode=0755 /usr/share/keyrings
curl -fsSL https://pkgs.tailscale.com/stable/debian/bookworm.noarmor.gpg \
    | tee /usr/share/keyrings/tailscale-archive-keyring.gpg >/dev/null
curl -fsSL https://pkgs.tailscale.com/stable/debian/bookworm.tailscale-keyring.list \
    | tee /etc/apt/sources.list.d/tailscale.list
apt-get update && apt-get install -y tailscale
```

Für Debian 13 `bookworm` durch `trixie` ersetzen.

## 4. Am Tailnet anmelden

```bash
tailscale up
```

Die Ausgabe enthält eine Adresse, die im Browser zu öffnen ist. Nach der
Anmeldung erscheint der Knoten in der Admin-Konsole.

Dann die Adresse notieren — sie wird im nächsten Schritt gebraucht:

```bash
tailscale ip -4
```

**Schlüsselablauf abschalten.** In der Tailscale-Admin-Konsole unter *Machines*
beim neuen Knoten **Disable key expiry** setzen. Ohne das erzwingt Tailscale
nach einigen Monaten eine erneute Anmeldung, und das Gateway fällt mitten im
Betrieb aus. Die Dokumentation weist darauf hin, dass das die Sicherheit senkt
und nur für vertrauenswürdige Geräte gemacht werden sollte — ein eigener
Container im eigenen Tailnet erfüllt das.

Verbindung zum Mac prüfen:

```bash
tailscale status
ping -c3 mac-mini-von-marcel
```

## 5. Docker installieren

```bash
curl -fsSL https://get.docker.com | sh
```

Klappt das nicht, fehlt meist `nesting=1` aus Schritt 2.

## 6. Freigabe auf dem Mac eintragen

Auf dem **Mac**, in `~/.config/changemyvoice/appsettings.Production.json`, die in
Schritt 4 notierte Adresse eintragen:

```json
"AllowedClientAddresses": [ "100.x.y.z" ]
```

Bewusst diese eine Adresse und nicht der ganze Tailnet-Bereich: Sonst könnte
jedes Gerät im Tailnet die API direkt ansprechen und das Gateway umgehen.

Danach den Dienst neu laden:

```bash
launchctl kickstart -k gui/$(id -u)/com.b-velop.changemyvoice
```

## 7. Gateway starten

Im Container:

```bash
mkdir -p /opt/changemyvoice-gateway && cd /opt/changemyvoice-gateway

curl -fsSLO https://raw.githubusercontent.com/Marcel-B/ChangeMyVoice/main/deploy/gateway/compose.yaml
curl -fsSL  https://raw.githubusercontent.com/Marcel-B/ChangeMyVoice/main/deploy/gateway/.env.beispiel -o .env
```

Die `.env` ausfüllen. Den Streuwert für den Client-Schlüssel so erzeugen:

```bash
printf '%s' 'der-client-schluessel' | sha256sum | cut -d' ' -f1
```

Dann starten:

```bash
docker compose pull && docker compose up -d
docker compose logs -f
```

Ein Anmelden an der Registry ist nicht nötig, das Abbild ist öffentlich.

## 8. Reverse Proxy

Im Proxy auf `192.168.2.71` einen Eintrag anlegen, parallel zu dem für `stem`:

| | |
| --- | --- |
| Domain | `voice.idsrv.info` |
| Ziel | Adresse des neuen Containers, Port `8080` |

Wichtig ist eine großzügige Zeitüberschreitung für den Upload und die
Weitergabe des Kopfes `X-Api-Key` — ohne ihn kommt jede Anfrage als `401`
zurück.

## 9. Probelauf

```bash
KEY='der-client-schluessel'

# Erreichbarkeit
curl -s https://voice.idsrv.info/health/live

# Ohne Schlüssel muss 401 kommen
curl -s -o /dev/null -w '%{http_code}\n' https://voice.idsrv.info/api/v1/voices

# Mit Schlüssel die Stimmen auflisten
curl -s https://voice.idsrv.info/api/v1/voices -H "X-Api-Key: $KEY"
```

Antwortet der letzte Aufruf mit `[]`, steht die Kette vom Proxy über das
Gateway und das Tailnet bis zur API auf dem Mac.

---

## Wenn etwas nicht läuft

| Beobachtung | Wahrscheinliche Ursache |
| --- | --- |
| `tailscale up` meldet ein fehlendes TUN-Gerät | Schritt 2 hat nicht gegriffen; Container stoppen und neu starten, nicht nur neu booten |
| Docker lässt sich nicht installieren | `nesting=1` fehlt |
| Gateway meldet `503 UPSTREAM_UNAVAILABLE` | Mac nicht erreichbar: `tailscale status` auf beiden Seiten, und lauscht die API auf der Tailscale-Adresse statt auf `127.0.0.1`? |
| API antwortet `403` | Die Adresse in der Freigabeliste passt nicht zu der, mit der der Container ankommt; im Protokoll des Macs steht die tatsächliche |
| API antwortet `401` | Der Proxy reicht `X-Api-Key` nicht durch, oder im Gateway steht ein falscher Streuwert |
| Nach Wochen plötzlich kein Zugriff mehr | Schlüsselablauf in Tailscale nicht abgeschaltet (Schritt 4) |

## Alternative ohne TUN-Gerät

Lässt sich `/dev/net/tun` nicht durchreichen, kann Tailscale im
Userspace-Modus laufen (`--tun=userspace-networking`, dauerhaft über
`FLAGS="--tun=userspace-networking"` in `/etc/default/tailscaled`).

Dabei entsteht allerdings **keine Netzwerkschnittstelle**: Tailscale arbeitet
dann als SOCKS5- beziehungsweise HTTP-Proxy, durch den sich jede Anwendung
ausdrücklich verbinden muss. Für das Gateway hieße das, den ausgehenden Verkehr
im Container auf diesen Proxy umzubiegen — deutlich umständlicher als der
Geräte-Durchgriff. Der reguläre Weg ist vorzuziehen.
