# Jellyfin Plugins (Carlos)

Dos plugins caseros para **Jellyfin 10.11.x** (compilados contra **.NET 9**, probados en un Raspberry Pi 4B con 10.11.9).

##  History Watchdog — Perro guardián del historial

Protege el historial de reproducción de accidentes como marcar una **serie o biblioteca entera** como vista por error (el clásico ✓ gigante que marca 500 episodios de un jalón).

**Cómo funciona (v1.1):**
- Escucha en vivo cada guardado de "visto" (`UserDataSaved`) y lo acumula en un log persistente que sobrevive reinicios (`plugins/HistoryWatchdog/state.json`)
- Si un usuario marca **≥ 5 episodios DISTINTOS en pocos segundos** (ráfaga) → aparece un **aviso** en **Panel → Plugins → History Watchdog** listando la ráfaga y sus episodios. El servidor **nunca deshace nada por su cuenta**
- En esa página tienes dos botones por ráfaga:
  - **"✓ Sí, estoy seguro — dejar marcado"** → **queda todo como está, sin importar cuántos episodios sean**
  - **"↺ Deshacer la ráfaga"** → se desmarcan únicamente los episodios de esa ráfaga (playcount -1, o reset completo si el item estaba sin ver; auditoría CSV)
- Las ráfagas se actualizan en vivo mientras siguen llegando marcas (una ráfaga de 500 episodios queda como una sola decisión)
- La tarea programada "Perro guardián del historial" aplica tus decisiones (Panel → Programado → ▶) y también corre sola 2 veces al día
- Auditoría CSV de cada decisión en `data/backups/history-watchdog/`
- Modo `DryRun` solo afecta al botón "Deshacer" (auditoría sin tocar); los avisos y "sí, estoy seguro" no se ven afectados

**Tarea programada:** "Perro guardián del historial" (Panel → Programado)

**Config** (`/var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.HistoryWatchdog.xml`):

| Opción | Default | Descripción |
|---|---|---|
| `Enabled` | `true` | vigilancia activa |
| `Threshold` | `5` | marcas distintas en la ventana para considerar ráfaga |
| `WindowSeconds` | `180` | segundos máximos entre marcas consecutivas |
| `DryRun` | `false` | el botón "deshacer" solo audita, no revierte |
| `ResetProgress` | `true` | al deshacer, borrar también el progreso |
| `PendingBursts` | `[]` | ráfagas pendientes de decisión (las gestiona la página; no editar a mano) |

**Instalación (v1.1.0.0):** carpeta `History Watchdog_1.1.0.0` con el DLL + meta.json, reiniciar Jellyfin, y entra a Panel → Plugins → History Watchdog.

##  Guest Mode — Modo huésped

Cuenta desechable para visitas: el invitado entra con su propio perfil (aparece en el selector de usuarios) y **no queda rastro de nada**.

- **Historial congelado en vivo**: cada guardado del invitado (visto, progreso, favorito, valoración) se deshace al momento dentro del servidor — su "Continuar viendo" queda siempre vacío
- **Inicio idéntico al tuyo**: al crear/reiniciar la cuenta se copian tus secciones de home, mismo orden (incluye el truco `usersettings`→MD5 que usa el cliente web, así que vale en cualquier dispositivo)
- Cuenta **"Invitado"** borrada y recreada = perfil siempre limpio — **nunca mientras la esté usando**: si el invitado está activo, el reinicio se aplaza
- **No corta películas**: si el invitado está reproduciendo algo (pausa incluida) o tiene actividad reciente en el servidor, el reinicio se salta y espera a la siguiente pasada
- Tarea "Modo huésped: reiniciar cuenta de invitado": **2 veces al día** automática + ejecutable a mano desde Panel → Programado
- Los demás usuarios nunca se tocan

**Config** (`/var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.GuestMode.xml`):

| Opción | Default | Descripción |
|---|---|---|
| `GuestModeEnabled` | `false` | con `true` el reinicio automático funciona |
| `GuestUserName` | `Invitado` | nombre de la cuenta huésped |
| `FreezeHistory` | `true` | congela en vivo todo lo que el invitado marque |
| `HomeCopyFrom` | `admin` | usuario cuyo Inicio se copia al invitado |
| `ActiveWindowMinutes` | `30` | minutos de inactividad para considerar al invitado "activo"; mientras reproducza —o navegue— dentro de esa ventana, el reinicio no se ejecuta. Con `0` solo se bloquea si está reproduciendo en ese instante |

###  Uso de la cuenta "Invitado", paso a paso

**Entrar al modo huésped.** En la pantalla de acceso de Jellyfin ("¿Quién está viendo?") aparece el perfil **Invitado** como uno más. Como no tiene contraseña, se entra con un solo toque — no aparece ningún campo de texto. También se puede cambiar de usuario en caliente desde el menú del avatar (arriba a la derecha) sin cerrar lo que estás viendo.

**Por qué no tiene contraseña (y por qué es a propósito).** El plugin crea la cuenta con `CreateUserAsync`, que genera usuarios **sin contraseña**, y Jellyfin solo pide contraseña cuando existe. Además, la cuenta se **borra y se vuelve a crear** en cada reinicio (2 veces al día o manual): cualquier contraseña que le pusieras a mano desaparecería con ella. Es una puerta deliberadamente sin llave — y es seguro porque es **la única**: todas las demás cuentas de la casa conservan la suya propia (el invitado no puede entrar a perfiles de otros, ni al Panel, ni tocar nada de administración; solo entra a la biblioteca con su perfil desechable).

**Lo que ve y lo que deja el invitado.**
- Su Inicio es una **copia idéntica** del de `HomeCopyFrom` (mismas secciones, mismo orden) y vale en cualquier dispositivo, porque la clave que usa el cliente web (`'usersettings'` → MD5) es universal
- Ve la biblioteca completa (películas, series, libros, LiveTV) con permisos de usuario normal — no admin
- Puede marcar episodios como vistos, pausar, continuar, añadir favoritos, valorar… pero **nada se queda**: el plugin deshace cada guardado al momento. Su "Continuar viendo" y "Siguiente" quedan siempre vacíos
- Dentro de la misma sesión de reproducción puede pausar y retomar sin problema; si **cierra y reabre** el reproductor, el vídeo empieza de cero — ese es el precio de "no se guarda nada"

**El ciclo de reinicio.** Dos veces al día (cada 12 h desde que arranca el servidor) la tarea borra la cuenta y la recrea limpia con tu Inicio — **salvo que el invitado esté usándola en ese momento**: si está reproduciendo algo (incluso en pausa) o tuvo actividad dentro de los últimos `ActiveWindowMinutes`, la pasada se salta con un apunte en el log ("reinicio aplazado") y se reintentará 12 h después. Si el cliente se cuelga sin cerrar sesión, el servidor limpia la sesión muerta en ~5 min y el reinicio vuelve a poder ejecutarse. Para reiniciar a demanda —p. ej. cuando se marcha la visita—: **Panel → Programado → "Modo huésped: reiniciar cuenta de invitado" → ▶ Ejecutar** (mismo bloqueo: si el invitado acaba de cerrar sesión, espera la ventana; el invitado puede salir con avatar → Cerrar sesión y el reinicio procede al momento).

**Ajustes típicos** (edita el XML y reinicia Jellyfin, o pídemelo):
- Otra cuenta huésped ("Visita", "Niños de paso"…): cambia `GuestUserName`
- Copiar el Inicio de otro miembro de la casa: cambia `HomeCopyFrom`
- Permitir que la visita acumule su propio progreso hasta el siguiente reinicio: `FreezeHistory=false`
- Cambiar cuánto tiempo cuenta como "activo" (p. ej. `5` para reinicios más agresivos): `ActiveWindowMinutes`
- Apagar el modo por completo (la cuenta deja de reiniciarse; el congelado sigue activo si la cuenta existe): `GuestModeEnabled=false`

**Ideas para el futuro:** `GuestPassword` (reaplicar una contraseña tras cada recreación, por si prefieres una puerta con llave) — pídela y la añado.

## 🔨 Instalación manual

```bash
# Requiere .NET SDK 9
cd HistoryWatchdog && dotnet publish -c Release -o out
sudo mkdir -p "/var/lib/jellyfin/plugins/History Watchdog_1.1.0.0"
sudo cp out/Jellyfin.Plugin.HistoryWatchdog.dll "/var/lib/jellyfin/plugins/History Watchdog_1.1.0.0/"
sudo cp meta.json "/var/lib/jellyfin/plugins/History Watchdog_1.1.0.0/"
sudo chown -R jellyfin:jellyfin "/var/lib/jellyfin/plugins/History Watchdog_1.1.0.0"
sudo systemctl restart jellyfin
```

(Guest Mode igual, con su carpeta `Guest Mode_1.0.0.0`.)

##  Estructura

```
HistoryWatchdog/   → plugin del perro guardián
GuestMode/         → plugin del modo huésped
```

## Notas

- Compilado contra los paquetes NuGet oficiales `Jellyfin.Controller` / `Jellyfin.Database.Implementations` **10.11.9**
- Sin acceso directo a la BD: solo APIs oficiales del servidor (evento `IUserDataManager.UserDataSaved`, `SaveUserData`, `IUserManager`, `ILibraryManager`)
- El guardián tiene anti-reentrancia: sus propias reversiones no se auto-registran
