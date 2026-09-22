# CLAUDE.md

Leitlinien für die Arbeit in diesem Repository.

## Projektkontext

Ziel ist eine API für Singing Voice Conversion (Audio → Audio) auf Basis von
Seed-VC / mlx-vc auf Apple Silicon. Die technische Grundlage ist in
[init.md](init.md) beschrieben.

## Verbindliche Vorgaben

Diese Punkte gelten immer, ohne dass sie erneut angefordert werden müssen:

- **.NET 10 / C#** — Die Anwendung wird in C# auf .NET 10 geschrieben.
- **OpenAPI mit Swagger** — Es muss eine OpenAPI-Dokumentation über Swagger
  generiert werden, in der *alle* Endpunkte dokumentiert sind. Neue Endpunkte
  gelten erst als fertig, wenn sie dort vollständig erscheinen (inkl.
  Request-/Response-Schemata und Statuscodes).
- **Tests sind verpflichtend** — Zu jeder Änderung gehören Tests. Kein Feature
  ohne zugehörige Tests.
- **Hexagonale Architektur** — Saubere Trennung der Zuständigkeiten nach dem
  Hexagonal-/Ports-and-Adapters-Pattern: Die Domäne ist frei von
  Infrastruktur-Abhängigkeiten, Ein- und Ausgänge laufen über Ports, konkrete
  Technik (HTTP, Dateisystem, Seed-VC/mlx-vc-Aufrufe, Persistenz) lebt
  ausschließlich in Adaptern.
