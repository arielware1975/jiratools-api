# Spec: Recordatorios con IA + botón "Ya lo hice"

## Objetivo

El bot de Telegram recibe pedidos de recordatorios en lenguaje natural, los parsea con IA a una estructura, los persiste y los dispara en el horario indicado. Cada aviso trae un botón inline para marcar como hecho, y vuelve a activarse al día siguiente si corresponde.

## Stack asumido

- Backend con acceso a un LLM (Gemini / Claude / OpenAI).
- Cliente de Telegram (polling o webhook).
- Storage simple (JSON en disco, SQLite o KV).

## Data model

```json
Reminder {
  id: string,                  // uuid corto, 8 chars
  chatId: long,
  message: string,             // texto del aviso, sin la parte del "cuándo"
  originalText: string,        // pedido crudo del usuario
  schedule: ReminderSchedule,
  lastFired: datetime?,        // último disparo (zona local del usuario)
  doneUntil: datetime?,        // si > now, no dispara. Setea el botón "Ya lo hice".
  createdAt: datetime,
  enabled: bool
}

ReminderSchedule {
  type: "once" | "daily" | "weekly" | "monthly" | "yearly" | "interval",
  date: "YYYY-MM-DD"?,         // solo type=once
  dayOfWeek: "mon".."sun"?,    // solo weekly
  dayOfMonth: 1..31?,          // monthly / yearly
  month: 1..12?,               // yearly
  offsetBusinessDays: int,     // monthly: -N = N días hábiles antes. 0 default.
  time: "HH:mm",               // hora de disparo (default "09:00"). Para interval: inicio.
  endTime: "HH:mm"?,           // solo interval: hora fin de ventana. Null = 23:59.
  intervalHours: int           // solo interval: cada N horas dentro de la ventana.
}
```

## Comandos Telegram

| Comando | Descripción |
|---|---|
| `/recordar <texto>` | Crea un recordatorio. Parsea con IA, confirma con resumen humano + id. |
| `/recordatorios` | Lista los activos del chat con id y schedule legible. |
| `/hecho <id>` | Marca como hecho por hoy (equivale al botón). |
| `/olvidar <id>` | Elimina el recordatorio. |

**Todos los recordatorios son del chat que los creó** — filtrar siempre por `chatId`.

## Prompt para la IA

Inyectar el texto del usuario y la fecha de hoy:

```
Parseá este pedido de recordatorio a JSON. Hoy es {YYYY-MM-DD} (zona Argentina UTC-3).

Pedido: "{user_text}"

Respondé EXCLUSIVAMENTE este JSON (sin markdown, sin explicaciones):
{
  "message": "mensaje del recordatorio sin la parte del cuándo",
  "schedule": {
    "type": "once|daily|weekly|monthly|yearly|interval",
    "date": "YYYY-MM-DD",
    "dayOfWeek": "mon|tue|wed|thu|fri|sat|sun",
    "dayOfMonth": 25,
    "month": 3,
    "offsetBusinessDays": -2,
    "time": "HH:mm",
    "endTime": "HH:mm",
    "intervalHours": 1
  }
}

Reglas:
- Si no especifica hora, usar "09:00".
- "una sola vez" → type=once, date obligatorio.
- "todos los días" → type=daily, time.
- "todos los lunes/martes/..." → type=weekly, dayOfWeek.
- "día X de cada mes" → type=monthly, dayOfMonth=X.
- "N días hábiles antes/después del día X de cada mes" → type=monthly, dayOfMonth=X, offsetBusinessDays=±N.
- "todos los X de mes Y" → type=yearly, dayOfMonth=X, month=Y.
- "cada N horas" (con o sin ventana) → type=interval, intervalHours=N, time=inicio, endTime=fin si se indica.
- Omití campos que no apliquen.
- Si el texto está confuso, respondé {"error": "no entendí"}.
```

Sanitizar la respuesta: si viene con code fences (```), strip antes de parsear.

## Scheduler

Loop cada **1 minuto**. Convertir `now` a zona local (Argentina UTC-3). Para cada reminder habilitado:

### `shouldFireNow(reminder, now)`

```
if (doneUntil && now < doneUntil) → false

if (type == "interval") → delegar a shouldFireInterval
  - parsear time (startH:startM) y endTime (default 23:59)
  - if now < windowStart || now > windowEnd → false
  - slotsPassed = floor((now - windowStart) en horas / intervalHours)
  - expectedFire = windowStart + slotsPassed * intervalHours
  - if lastFired >= expectedFire → false  // ya disparó este slot
  - return true

// para el resto de tipos:
targetDate = computeTargetDate(schedule, now)
if targetDate == null → false
fireTime = targetDate at schedule.time
if fireTime.date != now.date → false
if now < fireTime → false
if lastFired?.date == now.date → false    // no dos veces el mismo día
return true
```

### `computeTargetDate`

- **once**: parsear `date`.
- **daily**: `now.date`.
- **weekly**: `now.date` si `now.dayOfWeek == schedule.dayOfWeek`, sino null.
- **monthly**: resolver fecha = día X del mes actual + `applyBusinessDayOffset`. Chequear también el mes siguiente (si offset negativo cae en mes anterior).
- **yearly**: `now.date` si coinciden mes+día.

### `applyBusinessDayOffset(anchor, offset)`

```
if offset == 0: return anchor
step = sign(offset), remaining = |offset|, d = anchor
while remaining > 0:
  d = d + step days
  if d.dayOfWeek not in [sat, sun]: remaining -= 1
return d
```

(Para una versión más fina, excluir también feriados argentinos.)

### Cuando dispara

1. Enviar mensaje de Telegram con `InlineKeyboardMarkup` que tenga un botón con `callback_data = "reminder_done:<id>"`.
2. Texto: `⏰ Recordatorio\n\n{message}`.
3. Actualizar `lastFired = now`.
4. Si `type == once`, setear `enabled = false`.

## Handler del botón inline

Cuando llega un `callback_query`:

```
if data starts with "reminder_done:":
  id = data after ":"
  reminder.doneUntil = tomorrow at 00:00 (local)
  save
  answerCallbackQuery(cq.id, "✅ Listo, te dejo tranquilo por hoy")
  opcional: editMessageText tachando el original con ~...~
            y agregando "✅ Marcado como hecho"
```

Habilitar recepción de `callback_query` en las opciones del receiver de Telegram (por ejemplo, `AllowedUpdates = [Message, CallbackQuery]`).

## Persistencia

Archivo `data/reminders.json` o equivalente. Lista plana de reminders serializada. Lockear en escritura. Si corre en Docker, montar `data/` como volumen para que sobreviva rebuilds.

## Formato legible del schedule

```
formatSchedule(s):
  once     → "una vez el {date} a las {time}"
  daily    → "todos los días a las {time}"
  weekly   → "todos los {dayOfWeek_es} a las {time}"
  monthly  → if offsetBusinessDays != 0:
               "{|offset|} día{s} hábil{es} {antes|después} del día {X} de cada mes, a las {time}"
             else:
               "el día {X} de cada mes a las {time}"
  yearly   → "todos los {X} de {mes_es} a las {time}"
  interval → "cada {N} hora{s} desde las {time}" + (endTime ? " hasta las {endTime}" : "")
```

## Criterios de aceptación

- `recordame pagar el alquiler el 5 de cada mes a las 10am` → `monthly`, dayOfMonth=5, time=10:00.
- `pagar el crédito hipotecario 2 días hábiles antes del 25 de cada mes a las 9` → `monthly`, dayOfMonth=25, offsetBusinessDays=-2, time=09:00.
- `cada 1 hora entre las 9 y las 18 revisar CTA-1014` → `interval`, intervalHours=1, time=09:00, endTime=18:00.
- `todos los lunes 8am standup` → `weekly`, dayOfWeek=mon, time=08:00.
- `recordame mañana a las 15 llamar al contador` → `once`, date=mañana, time=15:00.
- Clickear botón "Ya lo hice" → no dispara de nuevo hoy. Al día siguiente reanuda.
- Si el container estuvo apagado cuando correspondía disparar pero vuelve el mismo día → dispara igual (no dos veces).
- Comando desconocido (ej `/patata`) → el bot debería responder "no reconozco ese comando" en lugar de quedar mudo.

## UX extras

- Si el LLM falla o devuelve JSON inválido: responder al usuario con un mensaje claro (`⚠️ No pude procesar el pedido`) en vez de silencio.
- Al crear el recordatorio, mostrar el id y el schedule legible para que el usuario valide.
- `/recordatorios` debería listar id + message corto + schedule legible.

## Notas de portabilidad

- Claude/OpenAI normalmente devuelven JSON limpio sin fences; Gemini a veces los incluye: conviene siempre pasar el sanitizador.
- La zona horaria se fija en el scheduler al convertir UTC → local. Si el bot sirve varios husos, guardar la zona por chat/usuario.
- El intervalo del loop (1 minuto) + `shouldFireNow` tolera drift y reinicios dentro del mismo día.
