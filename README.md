# VipManager

Plugin de [CounterStrikeSharp](https://docs.cssharp.dev/) para CS2 que da beneficios de VIP a los jugadores que figuran vigentes en una tabla de MariaDB: color de chat, slot reservado y recordatorio de vencimiento por chat.

El plugin **solo lee** la tabla (`SELECT`). Los VIP se altan/renuevan/dan de baja desde afuera (una API externa u otro proceso que escribe en la base).

## Qué hace

- Colorea el chat (`say` / `say_team`) de los jugadores VIP.
- Slot reservado: si el server está lleno y se conecta un VIP, kickea al no-VIP con el slot más alto para hacerle lugar.
- Cada 15 minutos revisa a los VIP conectados y, si les quedan `ReminderDaysBefore` días o menos, les manda un recordatorio por chat (una vez por día, solo en memoria — no escribe nada en la base).
- `css_vip`: comando para que cualquier jugador vea cuánto VIP le queda.

## Requisitos

- CS2 server con [Metamod:Source](https://www.sourcemm.net/) + [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) instalados.
- MariaDB / MySQL accesible desde el server.
- [.NET SDK](https://dotnet.microsoft.com/) 8+ para compilar (se probó con el SDK 10, que compila el paquete `CounterStrikeSharp.API` actual, target `net10.0`).

## Esquema de la base de datos

La tabla la crea y actualiza el sistema externo, no el plugin:

```sql
CREATE TABLE vip_users (
    id        INT UNSIGNED    NOT NULL AUTO_INCREMENT PRIMARY KEY,
    steamid   BIGINT UNSIGNED NOT NULL UNIQUE,
    name      VARCHAR(64)     NOT NULL,
    vip_start DATETIME        NOT NULL,
    vip_end   DATETIME        NOT NULL
);
```

Un jugador es VIP si, al momento de la consulta, `vip_start <= NOW() <= vip_end`. `steamid` es el SteamID64.

## Compilar

```bash
dotnet build -c Release
```

Genera `bin/Release/net10.0/VipManager.dll`.

## Instalar

Copiar estos dos archivos de `bin/Release/net10.0/` a `game/csgo/addons/counterstrikesharp/plugins/VipManager/` en el server:

```
VipManager.dll
MySqlConnector.dll
```

El resto de las DLLs de esa carpeta (`CounterStrikeSharp.API`, `Microsoft.Extensions.*`, etc.) no hace falta copiarlas, ya las trae CounterStrikeSharp instalado en el server.

## Configurar

### Credenciales de la base (variables de entorno)

La conexión a MariaDB/MySQL **no** va en el JSON, va por variables de entorno del proceso del server de CS2 (ver `.env.example`):

| Variable | Default si falta |
|---|---|
| `VIPMANAGER_DB_HOST` | `127.0.0.1` |
| `VIPMANAGER_DB_PORT` | `3306` |
| `VIPMANAGER_DB_NAME` | `cs2vip` |
| `VIPMANAGER_DB_USER` | `root` |
| `VIPMANAGER_DB_PASSWORD` | (vacío) |

Copiá `.env.example` a `.env`, completá los valores reales, y hacé que el proceso del server las tenga seteadas al arrancar (systemd: `EnvironmentFile=/ruta/.env` en la unit; docker: `--env-file .env`; script propio: `export $(cat .env | xargs)` antes de lanzar el server). El plugin las lee de `Environment.GetEnvironmentVariable`, así que tienen que estar en el entorno del proceso, no alcanza con tener el `.env` tirado en una carpeta sin cargarlo.

### VipManager.json

Al arrancar el server una vez con el plugin instalado, se genera:

```
addons/counterstrikesharp/configs/plugins/VipManager/VipManager.json
```

Ahí se configura:

| Clave | Default | Descripción |
|---|---|---|
| `ReminderDaysBefore` | `7` | Días antes del vencimiento en los que empieza a recordarle al jugador que renueve. |

Reiniciar el server (o el plugin) después de editar el config.

`VipManager.example.json` (en la raíz de este repo) muestra cómo queda ese archivo. Si copiás ese ejemplo a `addons/counterstrikesharp/configs/plugins/VipManager/VipManager.example.json` *antes* de arrancar el server por primera vez, CounterStrikeSharp lo usa como base para generar el `VipManager.json` real.

### Slot reservado

Cuando se conecta un VIP, el plugin se fija si el server está lleno (jugadores conectados == `sv_maxplayers`); si lo está, kickea al no-VIP con el slot más alto para hacerle lugar.

El motor de CS2 rechaza conexiones nuevas apenas se llega a `sv_maxplayers`, así que para que un VIP pueda entrar aunque esté "lleno" hace falta ese margen: subí `sv_maxplayers` 1 por encima de la cantidad real de jugadores que querés soportar (ej. si querés 10 jugadores de verdad, poné `sv_maxplayers 11`). El plugin se encarga de que ese cupo extra nunca lo ocupe un no-VIP por más de un instante.

## Comandos

- `css_vip` — cualquier jugador ve cuántos días de VIP le quedan.
