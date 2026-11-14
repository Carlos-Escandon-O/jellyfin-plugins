# Jellyfin Plugins (Carlos)

Dos plugins caseros para **Jellyfin 10.11.x** (compilados contra **.NET 9**, probados en un Raspberry Pi 4B con 10.11.9).

## 🛡️ History Watchdog — Perro guardián del historial

Protege el historial de reproducción de accidentes como marcar una **serie o biblioteca entera** como vista por error (el clásico ✓ gigante que marca 1.000 episodios en 3 segundos).

**Cómo funciona:**
- Escucha en vivo cada guardado de "visto" (`UserDataSaved`) y lo acumula en un log persistente que sobrevive reinicios (`plugins/HistoryWatchdog/state.json`)
- Dos veces al día (+ una pasada al arrancar) analiza el buffer: si un usuario marca **≥ 25 items con huecos ≤ 180 s** entre ellos → ráfaga
- Revierte las marcas por la vía oficial (`SaveUserData`): si el item estaba sin ver → reset completo (visto, contador, fecha, progreso); si ya tenía visionados previos → decrementa en 1
- Deja **auditoría CSV** de cada reversión en `data/backups/history-watchdog/`
- Modo `DryRun` para ensayar sin tocar nada

**Tarea programada:** "Perro guardián del historial" (Panel → Programado)

**Config** (`/var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.HistoryWatchdog.xml`):

| Opción | Default | Descripción |
|---|---|---|
| `Enabled` | `true` | vigilancia activa |
| `Threshold` | `25` | marcas consecutivas para considerar ráfaga |
| `WindowSeconds` | `180` | segundos máximos entre marcas consecutivas |
| `DryRun` | `false` | detectar + CSV pero no revertir |
| `ResetProgress` | `true` | al revertir, borrar también el progreso |

## 🎃 Guest Mode — Modo huésped

Cuenta desechable para visitas: el invitado ve lo que quiera **sin ensuciar el historial de nadie**.

- Crea/reinicia la cuenta **"Invitado"** (borrada y recreada = perfil siempre limpio)
- Tarea programada "Modo huésped: reiniciar cuenta de invitado": **2 veces al día** automática + ejecutable a mano desde Panel → Programado
- Los demás usuarios nunca se tocan

**Config** (`/var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.GuestMode.xml`):

| Opción | Default | Descripción |
|---|---|---|
| `GuestModeEnabled` | `false` | con `true` el reinicio automático funciona |
| `GuestUserName` | `Invitado` | nombre de la cuenta huésped |

## 🔨 Instalación manual

```bash
# Requiere .NET SDK 9
cd HistoryWatchdog && dotnet publish -c Release -o out
sudo mkdir -p "/var/lib/jellyfin/plugins/History Watchdog_1.0.0.0"
sudo cp out/Jellyfin.Plugin.HistoryWatchdog.dll "/var/lib/jellyfin/plugins/History Watchdog_1.0.0.0/"
sudo cp meta.json "/var/lib/jellyfin/plugins/History Watchdog_1.0.0.0/"
sudo chown -R jellyfin:jellyfin "/var/lib/jellyfin/plugins/History Watchdog_1.0.0.0"
sudo systemctl restart jellyfin
```

(Guest Mode igual, con su carpeta `Guest Mode_1.0.0.0`.)

## 📁 Estructura

```
HistoryWatchdog/   → plugin del perro guardián
GuestMode/         → plugin del modo huésped
```

## Notas

- Compilado contra los paquetes NuGet oficiales `Jellyfin.Controller` / `Jellyfin.Database.Implementations` **10.11.9**
- Sin acceso directo a la BD: solo APIs oficiales del servidor (evento `IUserDataManager.UserDataSaved`, `SaveUserData`, `IUserManager`, `ILibraryManager`)
- El guardián tiene anti-reentrancia: sus propias reversiones no se auto-registran
